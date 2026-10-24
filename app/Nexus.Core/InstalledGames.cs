using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.Win32;

namespace Nexus.Core;

public sealed record InstalledGame(string Name, string Root, string Source, string? Executable = null)
{
    public string Detail => $"{Source}  |  {Root}";
}
public sealed record GameLibraryResult(IReadOnlyList<InstalledGame> Games, IReadOnlyList<string> Notices);

public sealed class InstalledGames
{
    // Inputs are explicit so launcher parsing can be regression-tested without the host registry.
    public static GameLibraryResult Read(IEnumerable<string> steamRoots, IEnumerable<string> epicManifests,
        IEnumerable<InstalledGame>? registered = null, IEnumerable<string>? localContainers = null,
        CancellationToken cancellation = default)
    {
        List<InstalledGame> games = []; List<string> notices = [];
        void Add(InstalledGame game)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(game.Name) || !Directory.Exists(game.Root)) return;
                string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(game.Root));
                FileSafety.EnsureNoLinks(root);
                if (root == Path.GetPathRoot(root)) return;
                string? exe = game.Executable;
                if (exe != null && (!File.Exists(exe) || !FileSafety.IsInside(root, exe))) exe = null;
                games.Add(game with { Root = root, Executable = exe });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { }
        }
        void ReadSafely(string source, Action action)
        {
            cancellation.ThrowIfCancellationRequested();
            try { action(); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
            { notices.Add($"Skipped unreadable install record: {Path.GetFileName(source)}"); }
        }
        HashSet<string> libraries = new(StringComparer.OrdinalIgnoreCase);
        foreach (string steam in steamRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ReadSafely(steam, () =>
            {
                FileSafety.EnsureNoLinks(steam);
                libraries.Add(Path.GetFullPath(steam));
                string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                    foreach (var folder in VdfNode.Parse(ReadSmallFile(vdf)).Descendants())
                        if ((folder.Key.Equals("path", StringComparison.OrdinalIgnoreCase) ||
                             (folder.Key.All(char.IsDigit) && Path.IsPathRooted(folder.Value))) && folder.Value.Length != 0)
                            libraries.Add(Path.GetFullPath(folder.Value));
            });
        }
        foreach (string library in libraries)
        {
            string apps = Path.Combine(library, "steamapps");
            ReadSafely(apps, () =>
            {
                if (!Directory.Exists(apps)) return;
                FileSafety.EnsureNoLinks(apps);
                foreach (string manifest in Directory.EnumerateFiles(apps, "appmanifest_*.acf").Take(5000))
                    ReadSafely(manifest, () =>
                    {
                        var values = VdfNode.Parse(ReadSmallFile(manifest)).Children.FirstOrDefault()?.Children;
                        string Value(string key) => values?.FirstOrDefault(n => n.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value ?? "";
                        string install = Value("installdir"), name = Value("name");
                        if (int.TryParse(Value("StateFlags"), out int state) && (state & 4) == 0) return;
                        if (string.IsNullOrEmpty(install) || name.Contains("Redistributables", StringComparison.OrdinalIgnoreCase)) return;
                        Add(new(name, FileSafety.SafePath(Path.Combine(apps, "common"), install), "Steam"));
                    });
            });
        }
        foreach (string manifests in epicManifests.Distinct(StringComparer.OrdinalIgnoreCase))
            ReadSafely(manifests, () =>
            {
                if (!Directory.Exists(manifests)) return;
                FileSafety.EnsureNoLinks(manifests);
                foreach (string item in Directory.EnumerateFiles(manifests, "*.item").Take(5000))
                    ReadSafely(item, () =>
                    {
                        using var document = JsonDocument.Parse(ReadSmallFile(item)); var record = document.RootElement;
                        string Get(string key) => record.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
                        if (record.TryGetProperty("bIsIncompleteInstall", out var incomplete) && incomplete.ValueKind == JsonValueKind.True) return;
                        string root = Get("InstallLocation"), launch = Get("LaunchExecutable");
                        if (string.IsNullOrWhiteSpace(root)) return;
                        string? exe = string.IsNullOrWhiteSpace(launch) ? null : FileSafety.SafePath(root, launch);
                        Add(new(Get("DisplayName"), root, "Epic Games", exe));
                    });
            });
        foreach (var game in registered ?? []) { cancellation.ThrowIfCancellationRequested(); Add(game); }
        foreach (string container in localContainers ?? [])
            ReadSafely(container, () =>
            {
                if (!Directory.Exists(container)) return;
                FileSafety.EnsureNoLinks(container);
                foreach (string root in Directory.EnumerateDirectories(container).Take(1000))
                    ReadSafely(root, () =>
                    {
                        FileSafety.EnsureNoLinks(root);
                        // Only short bounded probing of common folders; never recursively scan a whole drive.
                        Queue<(string Directory, int Depth)> pending = new(); pending.Enqueue((root, 0)); int count = 0;
                        while (pending.TryDequeue(out var current) && ++count <= 64)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            var exe = Directory.EnumerateFiles(current.Directory, "*.exe").FirstOrDefault(FileSafety.IsX64);
                            if (exe != null) { Add(new(Path.GetFileName(root), root, "Local folder")); break; }
                            if (current.Depth >= 3) continue;
                            foreach (string child in Directory.EnumerateDirectories(current.Directory).Take(64))
                                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0 &&
                                    !Path.GetFileName(child).Equals("Content", StringComparison.OrdinalIgnoreCase)) pending.Enqueue((child, current.Depth + 1));
                        }
                    });
            });
        var unique = games.GroupBy(g => g.Root, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.FirstOrDefault(x => x.Source != "Local folder") ?? g.First())
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        return new(unique, notices);
    }

    [SupportedOSPlatform("windows")]
    public static GameLibraryResult Discover(CancellationToken cancellation = default)
    {
        List<string> steam = [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam")];
        List<InstalledGame> registered = [];
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                using var registry = RegistryKey.OpenBaseKey(hive, view);
                using (var key = registry.OpenSubKey(@"SOFTWARE\Valve\Steam"))
                    if ((key?.GetValue("SteamPath") ?? key?.GetValue("InstallPath")) is string path) steam.Add(path);
                using (var gog = registry.OpenSubKey(@"SOFTWARE\GOG.com\Games"))
                    foreach (string name in gog?.GetSubKeyNames() ?? [])
                    {
                        using var key = gog!.OpenSubKey(name);
                        if (key?.GetValue("path") is string path)
                            registered.Add(new(key.GetValue("gameName") as string ?? Path.GetFileName(path), path, "GOG", key.GetValue("exe") as string));
                    }
                using (var ubisoft = registry.OpenSubKey(@"SOFTWARE\Ubisoft\Launcher\Installs"))
                    foreach (string name in ubisoft?.GetSubKeyNames() ?? [])
                    {
                        using var key = ubisoft!.OpenSubKey(name);
                        if (key?.GetValue("InstallDir") is string path) registered.Add(new(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), path, "Ubisoft"));
                    }
                using (var uninstall = registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                    foreach (string name in uninstall?.GetSubKeyNames() ?? [])
                    {
                        using var key = uninstall!.OpenSubKey(name);
                        string title = key?.GetValue("DisplayName") as string ?? "";
                        string publisher = key?.GetValue("Publisher") as string ?? "";
                        bool gameRecord = name.StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase) ||
                            publisher.Contains("GOG", StringComparison.OrdinalIgnoreCase) ||
                            publisher.Contains("Ubisoft", StringComparison.OrdinalIgnoreCase) ||
                            publisher.Contains("Electronic Arts", StringComparison.OrdinalIgnoreCase);
                        if (gameRecord && !title.Contains("Launcher", StringComparison.OrdinalIgnoreCase) &&
                            !title.Contains("Ubisoft Connect", StringComparison.OrdinalIgnoreCase) &&
                            !title.Equals("EA app", StringComparison.OrdinalIgnoreCase) && !title.Contains("EA Desktop", StringComparison.OrdinalIgnoreCase) &&
                            key?.GetValue("InstallLocation") is string path && !string.IsNullOrWhiteSpace(path))
                            registered.Add(new(title, path, "Windows install record"));
                    }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
        }
        List<string> local = [];
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            steam.Add(Path.Combine(drive.RootDirectory.FullName, "SteamLibrary"));
            local.Add(Path.Combine(drive.RootDirectory.FullName, "Games"));
        }
        local.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic Games"));
        return Read(steam, [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests")], registered, local, cancellation);
    }

    private static string ReadSmallFile(string path)
    {
        FileSafety.EnsureNoLinks(path);
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Install record is too large.");
        return File.ReadAllText(path);
    }
}

public sealed partial class VdfNode(string key = "", string value = "")
{
    public string Key { get; } = key;
    public string Value { get; } = value;
    public List<VdfNode> Children { get; } = [];
    public IEnumerable<VdfNode> Descendants() => Children.SelectMany(c => new[] { c }.Concat(c.Descendants()));
    [GeneratedRegex("\"((?:\\\\.|[^\"\\\\])*)\"|[{}]|//[^\\r\\n]*", RegexOptions.CultureInvariant)]
    private static partial Regex Tokens();
    public static VdfNode Parse(string text)
    {
        var root = new VdfNode(); var stack = new Stack<VdfNode>(); stack.Push(root);
        string? pending = null;
        foreach (Match token in Tokens().Matches(text))
        {
            if (token.Value.StartsWith("//", StringComparison.Ordinal)) continue;
            if (token.Value == "{")
            {
                if (pending == null || stack.Count >= 32) throw new InvalidDataException("Invalid VDF nesting.");
                var child = new VdfNode(pending); stack.Peek().Children.Add(child); stack.Push(child); pending = null;
            }
            else if (token.Value == "}")
            { if (stack.Count <= 1 || pending != null) throw new InvalidDataException("Invalid VDF."); stack.Pop(); }
            else
            {
                string value = token.Groups[1].Value.Replace("\\\\", "\\").Replace("\\\"", "\"");
                if (pending == null) pending = value;
                else { stack.Peek().Children.Add(new(pending, value)); pending = null; }
            }
        }
        if (stack.Count != 1 || pending != null) throw new InvalidDataException("Incomplete VDF.");
        return root;
    }
}
