using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekHarness.Native;

/// <summary>
/// In-process updater: downloads, extracts, runs npm install, and swaps the
/// harness bundle — all without shelling out to PowerShell.
/// </summary>
public static class DshUpdater
{
    public record UpdateProgress(string Step, int Percent);

    /// <summary>Human-readable reason of the last failed update (null on success).</summary>
    public static string? LastError { get; private set; }

    private static List<string> LastNpmOutput { get; } = new();

    private static string InstallRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            if (string.Equals(dir.Name, "publish", StringComparison.OrdinalIgnoreCase))
                dir = dir.Parent ?? dir;
            return dir.Parent?.FullName ?? dir.FullName;
        }
    }

    private static string BundleDir => Path.Combine(
        InstallRoot, "harness", "node_modules", "@deepseek-ai", "dsh");

    private static string NodeExe
    {
        get
        {
            // 1. Bundled node (preferred)
            var bundled = Path.Combine(InstallRoot, "node", "node.exe");
            if (File.Exists(bundled)) return bundled;
            // 2. Node in install dir
            var local = Path.Combine(InstallRoot, "harness", "node.exe");
            if (File.Exists(local)) return local;
            // 3. System PATH
            return "node.exe";
        }
    }

    private static string NpmCmd
    {
        get
        {
            // 1. Bundled npm (preferred)
            var bundled = Path.Combine(InstallRoot, "node", "npm.cmd");
            if (File.Exists(bundled)) return bundled;
            // 2. Try npx.cmd next to node
            var nodeDir = Path.GetDirectoryName(NodeExe);
            if (nodeDir != null)
            {
                var npmNearNode = Path.Combine(nodeDir, "npm.cmd");
                if (File.Exists(npmNearNode)) return npmNearNode;
            }
            // 3. System PATH
            return "npm.cmd";
        }
    }

    private static string LogFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DeepSeekHarness", "logs", "update.log");

    private static void Log(string msg)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogFile)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}\n");
        }
        catch { }
    }

    /// <returns>(installed version, latest registry version); either may be null when unavailable.</returns>
    public static async Task<(string? Installed, string? Latest)> CheckAsync()
    {
        var installed = ReadInstalledVersion();
        string? latest = null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var json = await http.GetStringAsync("https://registry.npmjs.org/@deepseek-ai/dsh/latest");
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("version", out var v)) latest = v.GetString();
        }
        catch { /* offline */ }
        return (installed, latest);
    }

    /// <summary>
    /// Performs the full update in-process: download → extract → npm install → swap bundle.
    /// Reports progress via the callback.  Stops the harness process on the given port first.
    /// </summary>
    public static async Task<bool> UpdateAsync(
        int port,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken ct = default)
    {
        Log("=== Update started ===");
        LastError = null;
        LastNpmOutput.Clear();
        var (_, latest) = await CheckAsync();
        if (string.IsNullOrEmpty(latest)) { Log("No latest version"); LastError = "Не удалось определить последнюю версию (npm registry недоступен)."; return false; }

        var tgz = Path.Combine(Path.GetTempPath(), $"dsh-{latest}.tgz");
        var stage = Path.Combine(Path.GetTempPath(), $"dsh-update-{latest}");

        try
        {
            // 1. Download tarball with real byte-level progress (0% → 30%).
            //    Deliberately BEFORE stopping the harness: a broken release must
            //    never interrupt a working session.
            progress?.Report(new("Загрузка пакета...", 2));
            Log($"Downloading dsh {latest}");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                using var resp = await http.GetAsync(
                    $"https://registry.npmjs.org/@deepseek-ai/dsh/-/dsh-{latest}.tgz",
                    HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? -1;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(tgz);
                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;
                    if (total > 0)
                        progress?.Report(new("Загрузка пакета...", (int)(2 + 28.0 * read / total)));
                }
            }
            Log($"Downloaded {tgz}");

            // 2. Extract.
            progress?.Report(new("Распаковка...", 30));
            Log("Extracting tarball");
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            Directory.CreateDirectory(stage);
            ExtractTarGz(tgz, stage);
            Log("Extracted");

            var pkgDir = Path.Combine(stage, "package");

            // 4. Swap bundle. Fast path: dependencies unchanged → swap only
            //    lib/config/package.json. Full path: dependencies changed →
            //    replace the whole bundle (fresh node_modules) with rollback backup.
            var depsChanged = DependenciesChanged(
                Path.Combine(BundleDir, "package.json"),
                Path.Combine(pkgDir, "package.json"));

            if (!depsChanged)
            {
                // Stop the harness before replacing files it may hold open.
                progress?.Report(new("Остановка сервера...", 42));
                Log($"Killing port {port}");
                KillPortOwner(port);
                await Task.Delay(1000, ct);

                progress?.Report(new("Замена файлов...", 50));
                Log("Updating bundle in-place (deps unchanged)");
                BackupBundleLight();
                var srcLib = Path.Combine(pkgDir, "lib");
                var srcPkgJson = Path.Combine(pkgDir, "package.json");
                var srcConfig = Path.Combine(pkgDir, "config");
                var dstLib = Path.Combine(BundleDir, "lib");
                var dstPkgJson = Path.Combine(BundleDir, "package.json");
                var dstConfig = Path.Combine(BundleDir, "config");

                if (Directory.Exists(dstLib)) Directory.Delete(dstLib, true);
                if (Directory.Exists(srcLib)) CopyDirectory(srcLib, dstLib);

                if (Directory.Exists(dstConfig)) Directory.Delete(dstConfig, true);
                if (Directory.Exists(srcConfig)) CopyDirectory(srcConfig, dstConfig);

                if (File.Exists(srcPkgJson)) File.Copy(srcPkgJson, dstPkgJson, overwrite: true);
                Log("Bundle updated (lib + config + package.json)");
            }
            else
            {
                // 3a. PRE-FLIGHT: resolve the dependency tree without touching
                //     anything (npm --dry-run). Official releases sometimes ship
                //     with unpublished deps (0.1.2-rc.1 → dsh-experimental-agent-team
                //     404) — such releases must be rejected BEFORE we stop the
                //     harness or modify a single file.
                progress?.Report(new("Проверка целостности релиза...", 35));
                Log("Pre-flight: npm install --dry-run (resolve-only)");
                var preExit = await RunProcessAsync(NpmCmd,
                    "install --dry-run --ignore-scripts --no-audit --no-fund --no-progress --prefer-offline",
                    pkgDir, TimeSpan.FromMinutes(4), ct);
                Log($"pre-flight exit={preExit}");
                if (preExit != 0)
                {
                    var missing = LastNpmOutput.FirstOrDefault(l =>
                        l.Contains("E404") || l.Contains("could not be found"), "");
                    LastError = string.IsNullOrEmpty(missing)
                        ? "Официальный релиз 0.1.2… повреждён: зависимости не разрешаются в npm. Обновление отменено, ваша версия не тронута."
                        : $"Официальный релиз повреждён: {missing.Trim()} — пакета нет в npm. Обновление отменено, ваша версия не тронута.";
                    Log("pre-flight FAILED — release rejected, harness left running");
                    return false;
                }

                // 3b. Stop the running harness — only after the release passed
                //     the pre-flight and we are committed to swapping.
                progress?.Report(new("Остановка сервера...", 42));
                Log($"Killing port {port}");
                KillPortOwner(port);
                await Task.Delay(1000, ct);

                progress?.Report(new("Установка зависимостей...", 45));
                Log("Dependencies changed — full bundle replace with fresh node_modules");
                // Creeping progress while npm runs: it gives no usable output
                // chunks, so the bar advances slowly up to 74% instead of freezing.
                var creep = 45;
                using var creepTimer = new PeriodicTimer(TimeSpan.FromSeconds(2));
                var creepTask = Task.Run(async () =>
                {
                    try
                    {
                        while (await creepTimer.WaitForNextTickAsync(ct))
                        {
                            if (creep < 74) creep += 2;
                            progress?.Report(new("Установка зависимостей (может занять несколько минут)...", creep));
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (ObjectDisposedException) { }
                });
                int exit;
                try
                {
                    exit = await RunProcessAsync(NpmCmd,
                        "install --omit=dev --no-audit --no-fund --no-progress --prefer-offline --maxsockets=16",
                        pkgDir, TimeSpan.FromMinutes(10), ct);
                }
                finally
                {
                    creepTimer.Dispose();
                    try { await creepTask; } catch { }
                }
                Log($"npm install exit={exit}");
                if (exit != 0) throw new Exception($"npm install failed with code {exit}");

                progress?.Report(new("Замена файлов...", 78));
                BackupBundleFull();
                if (Directory.Exists(BundleDir)) Directory.Delete(BundleDir, true);
                Directory.Move(pkgDir, BundleDir);
                // The profile's own node_modules (installed at first boot) still
                // references the OLD bundle versions — rotate it aside so dsh
                // re-prepares the profile consistently on next start.
                RotateProfileModules();
                Log("Bundle fully replaced (with node_modules)");
            }

            // 5. Apply Russian patch.
            progress?.Report(new("Русификация...", 80));
            Log("Applying RU patch");
            var patchScript = Path.Combine(InstallRoot, "i18n-ru", "apply-ru.mjs");
            if (File.Exists(patchScript))
            {
                var modulesRoot = Path.Combine(BundleDir, "node_modules", "@deepseek-ai");
                var args = Directory.Exists(modulesRoot)
                    ? $"\"{patchScript}\" --base=\"{modulesRoot}\""
                    : $"\"{patchScript}\"";
                await RunProcessAsync(NodeExe, args, BundleDir, TimeSpan.FromMinutes(1), ct);
            }
            Log("RU patch done");

            // 7. Preserve shortcuts.
            progress?.Report(new("Обновление ярлыков...", 95));
            PreserveShortcuts();
            Log("Shortcuts preserved");

            progress?.Report(new($"Обновлено до {latest}", 100));
            Log($"=== Update complete: {latest} ===");
            return true;
        }
        catch (OperationCanceledException)
        {
            Log("Update cancelled");
            LastError = "Обновление отменено.";
            return false;
        }
        catch (Exception ex)
        {
            Log($"Update failed: {ex.Message}");
            LastError ??= ex.Message;
            return false;
        }
        finally
        {
            try { if (File.Exists(tgz)) File.Delete(tgz); } catch { }
            try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch { }
        }
    }

    /// <summary>Kills the process listening on the given port (Windows).</summary>
    private static void KillPortOwner(int port)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"Get-NetTCPConnection -LocalPort {port} -State Listen -ErrorAction SilentlyContinue | ForEach-Object {{ Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
        }
        catch { }
    }

    /// <summary>Extracts a .tgz archive to the target directory.</summary>
    private static void ExtractTarGz(string tgzPath, string targetDir)
    {
        var tar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
        if (!File.Exists(tar))
            throw new FileNotFoundException("tar.exe not found in System32");

        var psi = new ProcessStartInfo
        {
            FileName = tar,
            Arguments = $"-xzf \"{tgzPath}\" -C \"{targetDir}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi);
        p?.WaitForExit(30000);
        if (p is { ExitCode: not 0 })
            throw new Exception($"tar extraction failed: {p.StandardError.ReadToEnd()}");
    }

    private static async Task<int> RunProcessAsync(
        string fileName, string arguments, string workDir, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        Log($"Running: {fileName} {arguments}");
        using var p = Process.Start(psi) ?? throw new Exception($"Failed to start {fileName}");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        // Capture output for diagnostics (update.log keeps the tail on failure).
        var lines = new List<string>();
        var pumpOut = Task.Run(async () =>
        {
            try
            {
                while (await p.StandardOutput.ReadLineAsync() is { } l)
                {
                    lock (lines) { lines.Add(l); if (lines.Count > 60) lines.RemoveAt(0); }
                }
            }
            catch { }
        }, CancellationToken.None);
        var pumpErr = Task.Run(async () =>
        {
            try
            {
                while (await p.StandardError.ReadLineAsync() is { } l)
                {
                    lock (lines) { lines.Add(l); if (lines.Count > 60) lines.RemoveAt(0); }
                }
            }
            catch { }
        }, CancellationToken.None);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Log($"Process timed out after {timeout.TotalMinutes}min, killing");
            try { p.Kill(true); } catch { }
            throw;
        }
        try { await Task.WhenAll(pumpOut, pumpErr).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        var snapshot = new List<string>();
        lock (lines) snapshot.AddRange(lines);
        LastNpmOutput.Clear();
        LastNpmOutput.AddRange(snapshot);
        if (p.ExitCode != 0)
            foreach (var l in snapshot.TakeLast(15)) Log($"  npm| {l}");
        Log($"Process exit={p.ExitCode}");
        return p.ExitCode;
    }

    /// <summary>
    /// Ensures shortcuts (Desktop, Start Menu, Startup) point to the current install.
    /// Called after update so shortcuts never disappear.
    /// </summary>
    private static void PreserveShortcuts()
    {
        try
        {
            var launcher = Path.Combine(InstallRoot, "DeepSeekHarness.cmd");
            if (!File.Exists(launcher)) return;

            var exeIcon = Path.Combine(InstallRoot, "NativeClient", "DeepSeekHarness.Native.exe");
            if (!File.Exists(exeIcon))
                exeIcon = Path.Combine(InstallRoot, "NativeClient", "publish", "DeepSeekHarness.Native.exe");

            // Desktop
            var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            CreateOrUpdateShortcut(Path.Combine(desktopDir, "DeepSeek Harness.lnk"), launcher, InstallRoot, exeIcon);

            // Start Menu
            var programsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "DeepSeek Harness");
            Directory.CreateDirectory(programsDir);
            CreateOrUpdateShortcut(Path.Combine(programsDir, "DeepSeek Harness.lnk"), launcher, InstallRoot, exeIcon);

            // Startup (only if it already existed)
            var startupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            var startupLnk = Path.Combine(startupDir, "DeepSeek Harness.lnk");
            if (File.Exists(startupLnk))
                CreateOrUpdateShortcut(startupLnk, launcher, InstallRoot, exeIcon);
        }
        catch { /* best effort */ }
    }

    private static void CreateOrUpdateShortcut(string shortcutPath, string target, string workDir, string iconLocation)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t is null) return;
            dynamic shell = Activator.CreateInstance(t)!;
            var sc = shell.CreateShortcut(shortcutPath);
            sc.TargetPath = target;
            sc.WorkingDirectory = workDir;
            sc.Description = "DeepSeek Harness";
            if (File.Exists(iconLocation)) sc.IconLocation = iconLocation + ",0";
            sc.Save();
        }
        catch { }
    }

    public static string? ReadInstalledVersion()
    {
        try
        {
            var pkg = Path.Combine(BundleDir, "package.json");
            if (!File.Exists(pkg)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(pkg));
            return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch { return null; }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }

    // ── Update safety: dependency comparison + rollback backups ────────────

    private static string BackupDir => BundleDir + ".bak";

    /// <summary>True when the new package.json declares different dependencies.</summary>
    private static bool DependenciesChanged(string oldPkg, string newPkg)
    {
        try
        {
            using var a = JsonDocument.Parse(File.ReadAllText(oldPkg));
            using var b = JsonDocument.Parse(File.ReadAllText(newPkg));
            var da = a.RootElement.TryGetProperty("dependencies", out var x) ? x : default;
            var db = b.RootElement.TryGetProperty("dependencies", out var y) ? y : default;
            return !JsonElementEquals(da, db);
        }
        catch { return true; } // unverifiable → treat as changed (safe path)
    }

    private static bool JsonElementEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        if (a.ValueKind == JsonValueKind.Undefined) return true;
        if (a.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in a.EnumerateObject())
                if (!b.TryGetProperty(p.Name, out var v) || !JsonElementEquals(p.Value, v)) return false;
            foreach (var p in b.EnumerateObject())
                if (!a.TryGetProperty(p.Name, out _)) return false;
            return true;
        }
        return a.GetRawText() == b.GetRawText();
    }

    /// <summary>Backup for the fast path: only the three swapped items.</summary>
    private static void BackupBundleLight()
    {
        try
        {
            if (Directory.Exists(BackupDir)) Directory.Delete(BackupDir, true);
            Directory.CreateDirectory(BackupDir);
            foreach (var part in new[] { "lib", "config", "package.json" })
            {
                var src = Path.Combine(BundleDir, part);
                var dst = Path.Combine(BackupDir, part);
                if (Directory.Exists(src)) CopyDirectory(src, dst);
                else if (File.Exists(src)) File.Copy(src, dst, overwrite: true);
            }
        }
        catch (Exception ex) { Log($"light backup failed: {ex.Message}"); }
    }

    /// <summary>Backup for the full path: rename the whole bundle (instant, no copy).</summary>
    private static void BackupBundleFull()
    {
        try
        {
            if (Directory.Exists(BackupDir)) Directory.Delete(BackupDir, true);
            Directory.Move(BundleDir, BackupDir);
        }
        catch (Exception ex) { Log($"full backup failed: {ex.Message}"); }
    }

    private static string DataHome => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DeepSeekHarness", "data");

    private static string ProfileModulesBackup => Path.Combine(
        DataHome, "profiles", "node_modules.pre-update");

    /// <summary>
    /// Rotates $DSH_HOME/profiles/node_modules aside: it caches bundle plugin
    /// versions from the previous dsh release and a stale copy causes version
    /// skew (broken settings UI, false offline banners) after an update.
    /// dsh re-installs it consistently on the next boot.
    /// </summary>
    private static void RotateProfileModules()
    {
        try
        {
            var nm = Path.Combine(DataHome, "profiles", "node_modules");
            if (!Directory.Exists(nm)) return;
            if (Directory.Exists(ProfileModulesBackup)) Directory.Delete(ProfileModulesBackup, true);
            Directory.Move(nm, ProfileModulesBackup);
            Log("profile node_modules rotated (will re-prepare on next boot)");
        }
        catch (Exception ex) { Log($"profile rotation failed: {ex.Message}"); }
    }

    /// <summary>
    /// Restores the pre-update bundle if a previous update left the harness
    /// unable to boot. Returns true when a rollback happened.
    /// </summary>
    public static bool TryRollback()
    {
        try
        {
            if (!Directory.Exists(BackupDir)) return false;
            Log("rolling back to pre-update bundle");
            if (Directory.Exists(BundleDir)) Directory.Delete(BundleDir, true);
            Directory.Move(BackupDir, BundleDir);
            // Restore the matching profile modules backup when present.
            var nm = Path.Combine(DataHome, "profiles", "node_modules");
            if (Directory.Exists(ProfileModulesBackup))
            {
                if (Directory.Exists(nm)) Directory.Delete(nm, true);
                Directory.Move(ProfileModulesBackup, nm);
            }
            Log("rollback complete");
            return true;
        }
        catch (Exception ex)
        {
            Log($"rollback failed: {ex.Message}");
            return false;
        }
    }
}
