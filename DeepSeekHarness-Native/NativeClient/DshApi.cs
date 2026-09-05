using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekHarness.Native;

public sealed class DshHost : IDisposable
{
    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";
    private readonly string dataHome;
    private Process? process;
    private readonly List<string> stderrTail = new();

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

    public DshHost(int port = 3080)
    {
        Port = port;
        dataHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeepSeekHarness", "data");
        Directory.CreateDirectory(dataHome);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        EnsureUpdaterWiring();
        await ApplyRussianPatchAsync();

        if (await IsReadyAsync(cancellationToken)) return;

        // Offline bundle ships as <install>/harness/node_modules/@deepseek-ai/dsh;
        // fall back to `npx @deepseek-ai/dsh` when it is absent.
        var harnessBin = Path.GetFullPath(Path.Combine(
            InstallRoot, "harness", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js"));

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = InstallRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (File.Exists(harnessBin))
        {
            psi.FileName = ResolveNode();
            // --no-open: the shell owns the UI; dsh must not open a browser tab.
            psi.Arguments = $"\"{harnessBin}\" web --port {Port} --no-open";
        }
        else
        {
            psi.FileName = "npx.cmd";
            psi.Arguments = $"--yes @deepseek-ai/dsh web --port {Port} --no-open";
        }
        psi.Environment["DSH_HOME"] = dataHome;
        // Tells the updater plugin exactly which dsh copy is the bundle — the
        // plugin runs from a wiring copy inside the profile and cannot find
        // the bundle by walking up the directory tree.
        if (File.Exists(harnessBin))
            psi.Environment["DSH_BUNDLE_DIR"] = Path.GetFullPath(Path.Combine(
                InstallRoot, "harness", "node_modules", "@deepseek-ai", "dsh"));
        process = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить DeepSeek Harness.");
        _ = DrainAsync(process.StandardOutput);
        _ = CollectAsync(process.StandardError, stderrTail);
        for (var i = 0; i < 60; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(500, cancellationToken);
            if (process.HasExited)
            {
                var details = stderrTail.Count > 0
                    ? "\n\nПодробности:\n" + string.Join("\n", stderrTail)
                    : "";
                throw new InvalidOperationException(
                    $"DeepSeek Harness завершился во время запуска (код {process.ExitCode}).{details}");
            }
            if (await IsReadyAsync(cancellationToken)) return;
        }
        throw new TimeoutException("DeepSeek Harness не открыл локальный порт за 30 секунд.");
    }

    /// <summary>Stops the running harness (if any) and starts a fresh instance.</summary>
    public async Task RestartAsync(CancellationToken ct = default)
    {
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { }
        process?.Dispose();
        process = null;
        stderrTail.Clear();
        await StartAsync(ct);
    }

    /// <summary>
    /// Wires the in-app updater plugin into the web profile. The cordis loader
    /// resolves `- insert:` entries from the profile directory, so the plugin
    /// (stable home: harness/extra/) must be copied into the profile's
    /// node_modules and registered in the profile's own cordis.patch.yml —
    /// the profile layer lives in user data and survives dsh updates.
    /// </summary>
    private void EnsureUpdaterWiring()
    {
        try
        {
            var extra = Path.Combine(InstallRoot, "harness", "extra", "dsh-plugin-updater");
            if (!Directory.Exists(extra)) return;

            var profile = Path.Combine(dataHome, "profiles", "web");
            var dst = Path.Combine(profile, "node_modules", "@deepseek-ai", "dsh-plugin-updater");
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            if (Directory.Exists(dst)) Directory.Delete(dst, true);
            CopyDirectory(extra, dst);

            var yml = Path.Combine(profile, "cordis.patch.yml");
            var content = File.Exists(yml) ? File.ReadAllText(yml) : "[]";
            if (!content.Contains("dsh-plugin-updater"))
            {
                content = content.Replace("[]", "").TrimEnd();
                content += "\n- insert:\n    - id: updater\n      name: '@deepseek-ai/dsh-plugin-updater'\n";
                File.WriteAllText(yml, content);
            }
        }
        catch { /* best effort — must never block startup */ }
    }

    /// <summary>
    /// Re-applies the Russian localization (idempotent) against either the bundled
    /// offline copy or the global npm install, so the UI stays Russian across updates.
    /// </summary>
    private async Task ApplyRussianPatchAsync()
    {
        try
        {
            var script = Path.GetFullPath(Path.Combine(InstallRoot, "i18n-ru", "apply-ru.mjs"));
            if (!File.Exists(script)) return;

            var modulesRoot = Path.GetFullPath(Path.Combine(
                InstallRoot, "harness", "node_modules", "@deepseek-ai", "dsh", "node_modules", "@deepseek-ai"));
            var args = Directory.Exists(modulesRoot)
                ? $"\"{script}\" --base=\"{modulesRoot}\""
                : $"\"{script}\"";

            var psi = new ProcessStartInfo
            {
                FileName = ResolveNode(),
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p is null) return;
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch
        {
            // Best effort — the bundled copy ships pre-patched anyway.
        }
    }

    /// <summary>Bundled portable Node.js (installed by the setup) if present, else the system one.</summary>
    private static string ResolveNode()
    {
        var bundled = Path.Combine(InstallRoot, "node", "node.exe");
        return File.Exists(bundled) ? bundled : "node.exe";
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        try { while (await reader.ReadLineAsync() is not null) { } } catch { }
    }

    /// <summary>Drains a stream while keeping the last lines for diagnostics.</summary>
    private static async Task CollectAsync(StreamReader reader, List<string> tail)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                lock (tail)
                {
                    tail.Add(line);
                    if (tail.Count > 40) tail.RemoveRange(0, tail.Count - 40);
                }
            }
        }
        catch { }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await client.GetAsync(BaseUrl, cancellationToken);
            return true;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { }
        process?.Dispose();
    }
}
