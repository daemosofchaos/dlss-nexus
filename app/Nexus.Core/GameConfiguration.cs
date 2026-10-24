using System.Security.Cryptography;
using System.Text;

namespace Nexus.Core;

public sealed record ConfigurationSnapshot(string Fingerprint, bool? FeederEffectEnabled, Dictionary<string, string?> Guards)
{
    public string? PresetPath { get; init; }
    public string Description => FeederEffectEnabled switch
    {
        true => "Feeder effect enabled in preset", false => "Feeder effect disabled in preset", _ => "Preset status unknown"
    };
}
public static class GameConfiguration
{
    public static ConfigurationSnapshot Read(GameTarget target, IEnumerable<string> addonDirectories, string? preparedConfiguration = null)
    {
        Dictionary<string, string?> guards = new(StringComparer.OrdinalIgnoreCase);
        string? Read(string path)
        {
            FileSafety.EnsureNoLinks(path);
            if (!FileSafety.IsInside(target.Root, path)) throw new InvalidOperationException("The ReShade preset is outside the game folder.");
            if (!File.Exists(path)) { guards[path] = null; return null; }
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("A configuration file exceeds the size limit.");
            byte[] bytes = File.ReadAllBytes(path);
            guards[path] = Convert.ToHexString(SHA256.HashData(bytes));
            return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
        }
        static string? Value(string? text, string section, string key)
        {
            string current = ""; string? result = null;
            foreach (string line in (text ?? "").Split('\n'))
            {
                string item = line.Trim();
                if (item.StartsWith(';') || item.StartsWith('#')) continue;
                if (item.StartsWith('[') && item.EndsWith(']')) { current = item[1..^1]; continue; }
                int equal = item.IndexOf('=');
                if (equal > 0 && current.Equals(section, StringComparison.OrdinalIgnoreCase) && item[..equal].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    result = item[(equal + 1)..].Trim();
            }
            return result;
        }
        string? originalIni = Read(Path.Combine(target.Directory, "ReShade.ini"));
        string? ini = preparedConfiguration ?? originalIni;
        foreach (string directory in addonDirectories.Distinct(StringComparer.OrdinalIgnoreCase).Take(400))
        {
            Read(Path.Combine(directory, "dlss5-feed.cfg"));
            if (File.Exists(Path.Combine(directory, InstallerEngine.HelperAddon)))
                Read(Path.Combine(directory, "host64", "ReShade.ini"));
        }
        string? preset = Value(ini, "GENERAL", "PresetPath"), presetPath = null; bool? enabled = null;
        string? originalPreset = Value(originalIni, "GENERAL", "PresetPath");
        if (!string.IsNullOrWhiteSpace(originalPreset) && originalPreset != preset)
            Read(Path.GetFullPath(Path.Combine(target.Directory, originalPreset.Trim('"').Replace('/', Path.DirectorySeparatorChar))));
        if (!string.IsNullOrWhiteSpace(preset))
        {
            string path = Path.GetFullPath(Path.Combine(target.Directory, preset.Trim('"').Replace('/', Path.DirectorySeparatorChar)));
            presetPath = path;
            string? text = Read(path);
            if (text != null)
                enabled = NativePresetMigration.FeederEnabled(text);
        }
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",
            guards.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.Key.ToUpperInvariant() + ":" + (g.Value ?? "missing"))))));
        return new(fingerprint, enabled, guards) { PresetPath = presetPath };
    }
}
