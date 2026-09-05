using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeepSeekHarness.Setup;

public static class InstallerCore
{
    public const string ResourceName = "DeepSeekHarness.Setup.payload.DeepSeekHarness-Distribution.zip";

    public static string DefaultTarget =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DeepSeekHarness");

    public static void Extract(string target, Action<int, int>? progress = null)
    {
        Directory.CreateDirectory(target);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("Встроенный архив не найден.");
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var total = zip.Entries.Count;
        var done = 0;
        var createdDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { target };
        foreach (var entry in zip.Entries)
        {
            var rel = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            if (rel.Length == 0) continue;
            var dest = Path.Combine(target, rel);
            if (rel.EndsWith(Path.DirectorySeparatorChar))
            {
                if (createdDirs.Add(dest)) Directory.CreateDirectory(dest);
            }
            else
            {
                var dir = Path.GetDirectoryName(dest)!;
                if (createdDirs.Add(dir)) Directory.CreateDirectory(dir);
                entry.ExtractToFile(dest, overwrite: true);
            }
            done++;
            if (done % 200 == 0) progress?.Invoke(done, total);
        }
        progress?.Invoke(total, total);
    }

    /// <summary>
    /// Ensures a working Node.js (18+) is available: uses the bundled <paramref name="target"/>\node,
    /// then the system PATH, and finally downloads the portable LTS build into <paramref name="target"/>\node.
    /// </summary>
    public static async Task EnsureNodeAsync(string target, Action<string>? status = null)
    {
        Directory.CreateDirectory(target);
        var bundled = Path.Combine(target, "node", "node.exe");
        // Always bundle Node.js — the updater needs it even when system node exists.
        if (File.Exists(bundled) && IsNodeAtLeast18(bundled)) return;

        status?.Invoke("Node.js не найден — скачивание…");
        var url = await ResolveNodeLtsUrlAsync().ConfigureAwait(false);

        var zip = Path.Combine(Path.GetTempPath(), $"nodejs-{Guid.NewGuid():N}.zip");
        var tmp = Path.Combine(Path.GetTempPath(), $"nodejs-{Guid.NewGuid():N}");
        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            using (var resp = await http.GetAsync(url).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                using var fs = File.Create(zip);
                await resp.Content.CopyToAsync(fs).ConfigureAwait(false);
            }

            status?.Invoke("Распаковка Node.js…");
            ZipFile.ExtractToDirectory(zip, tmp);
            var root = Directory.GetDirectories(tmp)[0]; // node-vX.Y.Z-win-x64
            var nodeDir = Path.Combine(target, "node");
            if (Directory.Exists(nodeDir)) Directory.Delete(nodeDir, true);
            Directory.Move(root, nodeDir);
        }
        finally
        {
            if (File.Exists(zip)) File.Delete(zip);
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        }
        status?.Invoke("Node.js установлен.");
    }

    public static void CreateShortcuts(string target, bool startMenu, bool desktop, bool autostart)
    {
        var launcher = Path.Combine(target, "DeepSeekHarness.cmd");
        var exeIcon = Path.Combine(target, "NativeClient", "DeepSeekHarness.Native.exe");

        if (startMenu)
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "DeepSeek Harness");
            Directory.CreateDirectory(dir);
            CreateShortcut(Path.Combine(dir, "DeepSeek Harness.lnk"), launcher, target, exeIcon);
        }
        if (desktop)
        {
            var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            CreateShortcut(Path.Combine(desktopDir, "DeepSeek Harness.lnk"), launcher, target, exeIcon);
        }
        if (autostart)
        {
            var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            CreateShortcut(Path.Combine(startup, "DeepSeek Harness.lnk"), launcher, target, exeIcon);
        }
    }

    private static bool IsSystemNodeAtLeast18()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "node",
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            var version = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(10000);
            return ParseMajor(version, out var major) && major >= 18;
        }
        catch { return false; }
    }

    private static bool IsNodeAtLeast18(string nodeExe)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = nodeExe,
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            var version = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(10000);
            return ParseMajor(version, out var major) && major >= 18;
        }
        catch { return false; }
    }

    private static bool ParseMajor(string version, out int major)
    {
        major = 0;
        if (string.IsNullOrWhiteSpace(version)) return false;
        var v = version.Trim().TrimStart('v', 'V');
        var dot = v.IndexOf('.');
        return int.TryParse(dot < 0 ? v : v[..dot], out major);
    }

    private static async Task<string> ResolveNodeLtsUrlAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var json = await http.GetStringAsync("https://nodejs.org/dist/index.json").ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            if (el.TryGetProperty("lts", out var lts) && lts.ValueKind == JsonValueKind.String)
            {
                var version = el.GetProperty("version").GetString()!;
                return $"https://nodejs.org/dist/{version}/node-{version}-win-x64.zip";
            }
        }
        throw new InvalidOperationException("Не удалось определить актуальную версию Node.js.");
    }

    private static void CreateShortcut(string shortcutPath, string target, string workingDir, string iconLocation)
    {
        var t = Type.GetTypeFromProgID("WScript.Shell")!;
        dynamic shell = Activator.CreateInstance(t)!;
        var sc = shell.CreateShortcut(shortcutPath);
        sc.TargetPath = target;
        sc.WorkingDirectory = workingDir;
        sc.Description = "DeepSeek Harness";
        if (File.Exists(iconLocation)) sc.IconLocation = iconLocation + ",0";
        sc.Save();
    }
}
