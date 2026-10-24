namespace Nexus.Core;

public sealed record ReShadeLocations(string Addons, string Shaders, string Textures, string[] AddonSearchDirectories)
{
    public string[] RecursiveAddonRoots { get; init; } = [];
}
public static class ReShadePaths
{
    public static ReShadeLocations Resolve(GameTarget target, List<string> warnings, bool includeShaders = true, string? configurationText = null)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string ini = Path.Combine(target.Directory, "ReShade.ini");
        if (configurationText != null || File.Exists(ini))
        {
            if (configurationText == null)
            {
                FileSafety.EnsureNoLinks(ini);
                if (new FileInfo(ini).Length > 1024 * 1024) throw new InvalidDataException("ReShade.ini is too large.");
            }
            string section = "";
            foreach (string line in configurationText == null ? File.ReadLines(ini) : configurationText.TrimStart('\uFEFF').Split('\n'))
            {
                string text = line.Trim();
                if (text.StartsWith(';') || text.StartsWith('#')) continue;
                if (text.StartsWith('[') && text.EndsWith(']')) { section = text[1..^1]; continue; }
                int equals = text.IndexOf('=');
                if (equals > 0 && section.Equals("GENERAL", StringComparison.OrdinalIgnoreCase))
                    values[text[..equals].Trim()] = text[(equals + 1)..].Trim();
            }
        }
        HashSet<string> recursiveAddonRoots = new(StringComparer.OrdinalIgnoreCase);
        string[] Paths(string key, string fallback)
        {
            if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                return [Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(target.Directory, fallback)))];
            List<string> result = [];
            foreach (string entry in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string clean = entry.Trim('"').Replace('/', Path.DirectorySeparatorChar);
                bool recursive = clean.EndsWith("\\**", StringComparison.Ordinal) || clean == "**";
                if (clean == "**") clean = ".";
                else if (recursive) clean = clean[..^3];
                if (clean.Contains('*') || clean.Contains('?') || clean.Contains('%')) continue;
                string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(target.Directory, clean)));
                if (FileSafety.IsInside(target.Root, path))
                {
                    FileSafety.EnsureNoLinks(path); result.Add(path);
                    if (recursive && key == "AddonPath") recursiveAddonRoots.Add(path);
                }
            }
            if (result.Count == 0) throw new InvalidOperationException($"{key} points outside the selected game folder or uses unsupported wildcards. Choose a broader game root or configure a local ReShade path.");
            if (result.Count > 1) warnings.Add($"{key}: files will be installed to the first local search folder. Other search folders are preserved.");
            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        var addons = Paths("AddonPath", ".");
        var shaders = includeShaders ? Paths("EffectSearchPaths", "reshade-shaders/Shaders") : [target.Directory];
        var textures = includeShaders ? Paths("TextureSearchPaths", "reshade-shaders/Textures") : [target.Directory];
        foreach (var path in addons.Concat(shaders).Concat(textures))
        {
            if (!FileSafety.IsInside(target.Root, path)) throw new InvalidOperationException("A ReShade folder is outside the selected game root.");
            FileSafety.EnsureNoLinks(path);
        }
        var addonSearch = new HashSet<string>(addons, StringComparer.OrdinalIgnoreCase);
        foreach (var root in recursiveAddonRoots)
        {
            Queue<string> pending = new(); if (Directory.Exists(root)) pending.Enqueue(root);
            while (pending.TryDequeue(out var directory))
            {
                addonSearch.Add(directory);
                if (addonSearch.Count > 10000) throw new InvalidOperationException("The recursive add-on path is too broad. Configure a dedicated local add-on folder.");
                foreach (var child in Directory.GetDirectories(directory))
                {
                    // ReShade can load add-ons even from backup-named folders when ** is configured.
                    // Inspect every active search directory; our own backups use non-loadable .backup names.
                    FileSafety.EnsureNoLinks(child); pending.Enqueue(child);
                }
            }
        }
        return new(addons[0], shaders[0], textures[0], addonSearch.Order(StringComparer.OrdinalIgnoreCase).ToArray())
            { RecursiveAddonRoots = recursiveAddonRoots.ToArray() };
    }
}
