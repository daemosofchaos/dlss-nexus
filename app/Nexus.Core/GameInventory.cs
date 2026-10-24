using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Nexus.Core;

public sealed record InstalledComponent(string Name, string FileName, string Path, string Version,
    string VersionSource, string Hash, string Status);
public sealed record GameSnapshot(DateTimeOffset CheckedAt, List<InstalledComponent> Components,
    List<string> Notices, bool Complete)
{
    public ConfigurationSnapshot? Configuration { get; init; }
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        (Configuration?.Fingerprint ?? "") + "\n" + string.Join("\n", Components.OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase).Select(c => c.Path.ToUpperInvariant() + ":" + c.Hash)))));
    public bool Has(string fileName) => Components.Any(c => c.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase) && c.Status == "Installed");
}

public sealed class GameInventory(BundleCatalog catalog)
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nvngx_dlss.dll"] = "Super Resolution", ["nvngx_dlssd.dll"] = "Ray Reconstruction",
        ["nvngx_dlssg.dll"] = "Frame Generation", ["nvngx_dlssnr.dll"] = "Neural Rendering",
        ["dlss5-feed.addon64"] = "Feeder", ["renodx-dlss.addon64"] = "ShortFuse",
        ["dlss5-feed-helper.addon64"] = "Feeder (DX10 helper)", ["dlss5-feed-host64.exe"] = "Feeder host",
        ["renodx-dlss5.addon64"] = "RenoDX Generic"
    };
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
        { ".git", "Content", "Saved", "Intermediate", "archive", "backup", "backups", "redist", "_CommonRedist", "DLSS-Nexus-Backups" };

    public GameSnapshot Inspect(GameTarget target, CancellationToken cancellation = default)
    {
        List<InstalledComponent> components = []; List<string> notices = [];
        HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> hostDirectories = new(StringComparer.OrdinalIgnoreCase);
        bool complete = true;
        ConfigurationSnapshot? configuration = null;
        void ReadFile(string path)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!files.Add(path)) return;
            string filename = System.IO.Path.GetFileName(path);
            try
            {
                FileSafety.EnsureNoLinks(path);
                if (!FileSafety.IsInside(target.Root, path) || !File.Exists(path)) return;
                if (new FileInfo(path).Length > 512L * 1024 * 1024) throw new InvalidDataException("Component exceeds size limit.");
                bool reshade = InstallerEngine.GraphicsProxyNames.Contains(filename, StringComparer.OrdinalIgnoreCase) &&
                    (System.IO.Path.GetDirectoryName(path)!.Equals(target.Directory, StringComparison.OrdinalIgnoreCase) ||
                     filename.Equals("dxgi.dll", StringComparison.OrdinalIgnoreCase) && hostDirectories.Contains(System.IO.Path.GetDirectoryName(path)!)) && FileSafety.IsReShade(path);
                if (!Names.TryGetValue(filename, out string? name) && !reshade) return;
                bool valid = FileSafety.IsX64(path) && (!reshade || FileSafety.IsReShade(path));
                string hash = FileSafety.Hash(path), version = "Unknown", source = "No version information";
                var known = catalog.Manifest.Components.FirstOrDefault(c => c.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase));
                if (known?.Version is { Length: > 0 } matched) { version = matched; source = "Package hash match"; }
                else
                {
                    string? fileVersion = FileVersionInfo.GetVersionInfo(path).FileVersion;
                    if (!string.IsNullOrWhiteSpace(fileVersion)) { version = fileVersion; source = "File metadata"; }
                }
                components.Add(new(reshade ? "ReShade" : name!, filename, path, version, source, hash, valid ? "Installed" : "Invalid file"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            { complete = false; notices.Add($"Could not inspect {filename}."); }
        }
        try
        {
            FileSafety.EnsureNoLinks(target.Root);
            if (!Directory.Exists(target.Root)) return new(DateTimeOffset.UtcNow, [], ["Game folder unavailable."], false);
            var paths = ReShadePaths.Resolve(target, notices, includeShaders: false);
            configuration = GameConfiguration.Read(target, paths.AddonSearchDirectories);
            foreach (string proxy in InstallerEngine.GraphicsProxyNames) ReadFile(System.IO.Path.Combine(target.Directory, proxy));
            // Honor local ReShade add-on paths, not just adjacent files.
            foreach (string directory in paths.AddonSearchDirectories.Take(400))
            {
                foreach (string name in Names.Keys.Where(n => n.EndsWith(".addon64"))) ReadFile(System.IO.Path.Combine(directory, name));
                if (File.Exists(System.IO.Path.Combine(directory, InstallerEngine.HelperAddon)))
                {
                    string host = FileSafety.SafePath(directory, "host64"); hostDirectories.Add(host);
                    var hostTarget = new GameTarget(host, host, FileSafety.SafePath(host, InstallerEngine.HelperExecutable), []);
                    var hostPaths = ReShadePaths.Resolve(hostTarget, notices, includeShaders: false);
                    ReadFile(System.IO.Path.Combine(host, "dxgi.dll"));
                    ReadFile(hostTarget.Executable);
                    foreach (string addonDirectory in hostPaths.AddonSearchDirectories.Take(400))
                        foreach (string name in Names.Keys.Where(n => n.EndsWith(".addon64"))) ReadFile(System.IO.Path.Combine(addonDirectory, name));
                    if (hostPaths.AddonSearchDirectories.Length > 400) { complete = false; notices.Add("Helper add-on scan limit reached."); }
                }
            }
            if (paths.AddonSearchDirectories.Length > 400) { complete = false; notices.Add("Add-on scan limit reached."); }
            Queue<(string Path, int Depth)> pending = new(); pending.Enqueue((target.Root, 0)); int count = 0;
            while (pending.TryDequeue(out var directory))
            {
                cancellation.ThrowIfCancellationRequested();
                if (++count > 400) { complete = false; notices.Add("Scan limit reached. Some components may not be listed."); break; }
                try
                {
                    FileSafety.EnsureNoLinks(directory.Path);
                    foreach (string name in Names.Keys.Where(n => n.EndsWith(".dll"))) ReadFile(System.IO.Path.Combine(directory.Path, name));
                    foreach (string name in Names.Keys.Where(n => n.EndsWith(".addon64"))) ReadFile(System.IO.Path.Combine(directory.Path, name));
                    foreach (string child in Directory.EnumerateDirectories(directory.Path))
                    {
                        if (Skipped.Contains(System.IO.Path.GetFileName(child)) || (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                        if (directory.Depth >= 8 || pending.Count >= 400)
                        { complete = false; continue; }
                        pending.Enqueue((child, directory.Depth + 1));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                { complete = false; notices.Add("A folder could not be inspected."); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { complete = false; notices.Add("Could not inspect the game folder or ReShade paths."); }
        if (!complete && notices.Count == 0) notices.Add("The component scan was incomplete.");
        return new(DateTimeOffset.UtcNow, components.OrderBy(c => c.Name).ThenBy(c => c.Path).ToList(), notices.Distinct().ToList(), complete) { Configuration = configuration };
    }
}
