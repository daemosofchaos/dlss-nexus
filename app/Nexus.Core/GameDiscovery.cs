using System.Text.RegularExpressions;

namespace Nexus.Core;

public sealed partial class GameDiscovery
{
    private static readonly string[] ProxyNames = ["dxgi.dll", "d3d10.dll", "d3d11.dll", "d3d12.dll"];
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "archive", "backups", "backup", "_CommonRedist", "redist", "__Installer", ".git", "node_modules",
        "reshade-shaders", "Saved", "Intermediate", "Content"
    };
    [GeneratedRegex(@"(launcher|crashreport|crashpad|unins|setup|installer|vcredist|dxsetup|updater|unitycrash|anticheat|dlss5-feed-host64)", RegexOptions.IgnoreCase)]
    private static partial Regex UtilityName();

    public DiscoveryResult Scan(string selectedPath, CancellationToken cancellation = default, IProgress<string>? progress = null,
        bool includeWithoutReShade = false)
    {
        string input = Path.GetFullPath(selectedPath.Trim().Trim('"'));
        bool exactExecutable = File.Exists(input) && Path.GetExtension(input).Equals(".exe", StringComparison.OrdinalIgnoreCase);
        string root = exactExecutable ? Path.GetDirectoryName(input)! : input;
        if (!Directory.Exists(root)) throw new InvalidOperationException("Choose an existing game folder or executable.");
        FileSafety.EnsureNoLinks(root);
        if (root.Equals(Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a game folder, not an entire drive.");
        List<GameTarget> targets = []; List<string> notices = [];
        Queue<(string Path, int Depth)> pending = new(); pending.Enqueue((root, 0));
        int count = 0;
        while (pending.TryDequeue(out var current))
        {
            cancellation.ThrowIfCancellationRequested();
            if (++count > 10000) { notices.Add("The search limit was reached. Choose a more specific game folder."); break; }
            progress?.Report($"Searching {current.Path}");
            try
            {
                var proxies = ProxyNames.Select(n => Path.Combine(current.Path, n)).Where(File.Exists)
                    .Where(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0 && FileSafety.IsReShade(p)).ToArray();
                if (proxies.Length != 0 || includeWithoutReShade)
                {
                    var exes = exactExecutable ? new[] { input } : Directory.GetFiles(current.Path, "*.exe");
                    foreach (var exe in exes.Where(p => !UtilityName().IsMatch(Path.GetFileNameWithoutExtension(p))))
                        if ((File.GetAttributes(exe) & FileAttributes.ReparsePoint) == 0 && FileSafety.IsX64(exe))
                            targets.Add(new GameTarget(root, current.Path, exe, proxies));
                }
                if (exactExecutable) break;
                var subdirs = Directory.GetDirectories(current.Path).Where(p =>
                    !ExcludedDirectories.Contains(Path.GetFileName(p)) &&

                    !Path.GetFileName(p).StartsWith("DLSS-Nexus-Backups", StringComparison.OrdinalIgnoreCase) &&
                    (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0).Order(StringComparer.OrdinalIgnoreCase).ToArray();
                if (current.Depth >= 12 && subdirs.Length != 0) notices.Add($"Search depth reached in {current.Path}. Choose a closer folder if needed.");
                else foreach (var subdir in subdirs) pending.Enqueue((subdir, current.Depth + 1));
            }
            catch (UnauthorizedAccessException) { notices.Add($"Access unavailable: {current.Path}"); }
            catch (IOException) { notices.Add($"Could not read: {current.Path}"); }
        }
        if (targets.Count == 0) notices.Add(includeWithoutReShade
            ? "No 64-bit game executable found. Select the actual game executable, not its launcher."
            : "No supported ReShade installation found. Install the 64-bit ReShade add-on build for Direct3D 10/11/12, then search again.");
        if (targets.Count > 1) notices.Add("Multiple game executables found. Select the one you play; no folder will be chosen automatically.");
        return new(targets.OrderBy(t => t.Directory).ThenBy(t => t.Executable).ToArray(), notices, count);
    }
}