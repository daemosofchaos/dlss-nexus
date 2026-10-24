using System.Text;

namespace Nexus.Core;

internal sealed record PreparedReShadeConfiguration(string Text, string? OriginalHash);

internal static class ReShadeSetup
{
    internal const string NativePreset = "Techniques=\nTechniqueSorting=Lumenite_Kernel@lumenite_Kernel.fx,DLSS5_Feed@DLSS5_Feed.fx\n\n[DLSS5_Feed.fx]\nPreprocessorDefinitions=DLSS5_MV_PROVIDER=3\n";

    internal static PreparedReShadeConfiguration Prepare(GameTarget target, SetupRoute route)
    {
        string path = FileSafety.SafePath(target.Directory, "ReShade.ini");
        if (Directory.Exists(path)) throw new InvalidOperationException("A folder occupies ReShade.ini.");
        string? hash = null;
        string original = "";
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("ReShade.ini is too large.");
            byte[] bytes = File.ReadAllBytes(path);
            using var memory = new MemoryStream(bytes);
            hash = FileSafety.Hash(memory);
            original = new UTF8Encoding(false, true).GetString(bytes);
        }
        string newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        bool bom = original.StartsWith('\uFEFF');
        var lines = (hash == null ? "[GENERAL]\n\n[INPUT]\nKeyOverlay=36,0,0,0\n" : original.TrimStart('\uFEFF'))
            .Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        bool changed = hash == null;

        string? Value(string key)
        {
            string section = ""; string? result = null;
            foreach (string line in lines)
            {
                string text = line.Trim();
                if (text.StartsWith(';') || text.StartsWith('#')) continue;
                if (text.StartsWith('[') && text.EndsWith(']')) { section = text[1..^1]; continue; }
                int equal = text.IndexOf('=');
                if (equal > 0 && section.Equals("GENERAL", StringComparison.OrdinalIgnoreCase) && text[..equal].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    result = text[(equal + 1)..].Trim();
            }
            return result;
        }
        void Set(string key, string value)
        {
            string section = ""; int insert = -1; bool found = false;
            for (int i = 0; i < lines.Count; i++)
            {
                string text = lines[i].Trim();
                if (text.StartsWith(';') || text.StartsWith('#')) continue;
                if (text.StartsWith('[') && text.EndsWith(']'))
                {
                    section = text[1..^1];
                    if (section.Equals("GENERAL", StringComparison.OrdinalIgnoreCase)) insert = i + 1;
                    continue;
                }
                if (!section.Equals("GENERAL", StringComparison.OrdinalIgnoreCase)) continue;
                insert = i + 1;
                int equal = lines[i].IndexOf('=');
                if (equal > 0 && lines[i][..equal].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    string replacement = lines[i][..(equal + 1)] + value;
                    changed |= replacement != lines[i]; lines[i] = replacement; found = true;
                }
            }
            if (found) return;
            if (insert < 0)
            {
                if (lines.Count != 0 && lines[^1].Length != 0) lines.Add("");
                lines.Add("[GENERAL]"); insert = lines.Count;
            }
            lines.Insert(insert, key + "=" + value); changed = true;
        }
        string? LocalFolder(string entry)
        {
            string clean = entry.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar);
            if (clean == "**") clean = ".";
            else if (clean.EndsWith("\\**", StringComparison.Ordinal)) clean = clean[..^3];
            if (clean.Contains('*') || clean.Contains('?') || clean.Contains('%')) return null;
            try
            {
                string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(target.Directory, clean)));
                return FileSafety.IsInside(target.Root, folder) ? folder : null;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return null; }
        }
        void SearchPath(string key, string fallback)
        {
            string? value = Value(key);
            if (string.IsNullOrWhiteSpace(value)) { Set(key, fallback); return; }
            string? firstLocal = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(LocalFolder).FirstOrDefault(p => p != null);
            if (firstLocal?.Equals(Path.TrimEndingDirectorySeparator(target.Directory), StringComparison.OrdinalIgnoreCase) == true ||
                (firstLocal == null && route == SetupRoute.NativeDlss))
                Set(key, fallback + "," + value);
        }
        if (string.IsNullOrWhiteSpace(Value("AddonPath"))) Set("AddonPath", ".\\");
        SearchPath("EffectSearchPaths", ".\\reshade-shaders\\Shaders\\**");
        SearchPath("TextureSearchPaths", ".\\reshade-shaders\\Textures\\**");
        string? preset = Value("PresetPath");
        bool defaultMissing = false;
        if (!string.IsNullOrWhiteSpace(preset))
        {
            string candidate = Path.GetFullPath(Path.Combine(target.Directory, preset.Trim('"').Replace('/', Path.DirectorySeparatorChar)));
            if (candidate.Equals(Path.Combine(target.Directory, "ReShadePreset.ini"), StringComparison.OrdinalIgnoreCase))
            {
                FileSafety.EnsureNoLinks(candidate);
                defaultMissing = !File.Exists(candidate) && !Directory.Exists(candidate);
            }
        }
        if (string.IsNullOrWhiteSpace(preset) || defaultMissing) Set("PresetPath", ".\\" + InstallerEngine.PresetName);
        return new(changed ? (bom ? "\uFEFF" : "") + string.Join(newline, lines) : original, hash);
    }
}
