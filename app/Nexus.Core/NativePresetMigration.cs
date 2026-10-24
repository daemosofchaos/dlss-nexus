using System.Text;

namespace Nexus.Core;

internal static class NativePresetMigration
{
    private static bool IsFeeder(string token) => token.Trim().Equals("DLSS5_Feed", StringComparison.OrdinalIgnoreCase) ||
        token.Trim().StartsWith("DLSS5_Feed@", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<(int Index, int Equal, string Key)> RootTechniqueLines(string[] lines)
    {
        bool root = true;
        for (int i = 0; i < lines.Length; i++)
        {
            string text = lines[i].Trim().TrimStart('\uFEFF');
            if (text.StartsWith(';') || text.StartsWith('#')) continue;
            if (text.StartsWith('[') && text.EndsWith(']')) { root = text == "[]"; continue; }
            int equal = lines[i].IndexOf('=');
            if (!root || equal <= 0) continue;
            string key = lines[i][..equal].Trim().TrimStart('\uFEFF');
            if (key.Equals("Techniques", StringComparison.OrdinalIgnoreCase) || key.Equals("TechniqueSorting", StringComparison.OrdinalIgnoreCase))
                yield return (i, equal, key);
        }
    }

    internal static bool? FeederEnabled(string text)
    {
        string[] lines = text.Split('\n'); bool found = false;
        foreach (var line in RootTechniqueLines(lines).Where(l => l.Key.Equals("Techniques", StringComparison.OrdinalIgnoreCase)))
        {
            found = true;
            if (lines[line.Index][(line.Equal + 1)..].Split(',').Any(IsFeeder)) return true;
        }
        return found ? false : null;
    }

    internal static PlannedFile Prepare(GameTarget target, ConfigurationSnapshot configuration)
    {
        string path = configuration.PresetPath ?? throw new InvalidOperationException("The active ReShade preset could not be located.");
        FileSafety.EnsureNoLinks(path);
        if (!FileSafety.IsInside(target.Root, path)) throw new InvalidOperationException("The ReShade preset is outside the game folder.");
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("The ReShade preset exceeds the size limit.");
        byte[] bytes = File.ReadAllBytes(path);
        using var original = new MemoryStream(bytes);
        string originalHash = FileSafety.Hash(original);
        if (!configuration.Guards.TryGetValue(path, out string? reviewedHash) || reviewedHash != originalHash)
            throw new IOException("The active preset changed while preparing the review. Recheck the setup.");
        string text = new UTF8Encoding(false, true).GetString(bytes);
        // Split only on LF: retaining each line's CR preserves BOM, line endings and all untouched settings.
        string[] lines = text.Split('\n');
        foreach (var line in RootTechniqueLines(lines))
        {
            string value = lines[line.Index][(line.Equal + 1)..];
            string ending = value.EndsWith('\r') ? "\r" : "";
            string[] tokens = value.TrimEnd('\r').Split(',');
            if (!tokens.Any(IsFeeder)) continue;
            lines[line.Index] = lines[line.Index][..(line.Equal + 1)] + string.Join(",", tokens.Where(t => !IsFeeder(t))) + ending;
        }
        byte[] prepared = Encoding.UTF8.GetBytes(string.Join("\n", lines));
        using var result = new MemoryStream(prepared);
        string hash = FileSafety.Hash(result);
        if (FeederEnabled(Encoding.UTF8.GetString(prepared)) != false)
            throw new InvalidDataException("Feeder could not be disabled in the active preset. Turn it off in ReShade, then recheck.");
        return new("Active preset (Feeder off)", path, hash == originalHash ? Operation.Current : Operation.Replace,
            "generated:" + Convert.ToBase64String(prepared), hash, originalHash,
            "Turn off Feeder; back up the original preset and retain other effects and settings");
    }
}