using System.Text.RegularExpressions;

namespace Nexus.Core;

public sealed partial class BundleCatalog
{
    // IDs are catalog keys, never filesystem paths. Defaults and labels live in the catalog,
    // so another reviewed build can be added without adding a profile or installer branch.
    public void ValidateVersionCatalog()
    {
        static void Entries(IEnumerable<(string Id, string Label, string Notes)> entries)
        {
            var rows = entries.ToArray();
            if (rows.Length > 64 || rows.Any(r => !Regex.IsMatch(r.Id, "^[a-z0-9][a-z0-9.-]{0,79}$") ||
                    string.IsNullOrWhiteSpace(r.Label) || r.Label.Length > 160 || r.Notes.Length > 1000) ||
                rows.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rows.Length)
                throw new InvalidOperationException("The package version catalog is invalid.");
        }
        Entries(Manifest.AddonBuilds.Select(b => (b.Id, b.Label, b.Notes)));
        Entries(Manifest.NeuralRuntimeBuilds.Select(b => (b.Id, b.Label, b.Notes)));
        static void Payload(string path)
        {
            if (string.IsNullOrEmpty(path) || path.StartsWith('/') || path.Contains('\\') || path.Contains(':') ||
                path.Split('/').Any(p => p is "" or "." or "..") || path.StartsWith("templates/", StringComparison.Ordinal))
                throw new InvalidOperationException("A package version has an invalid source path.");
        }
        foreach (var build in Manifest.AddonBuilds)
        {
            Payload(build.PayloadPath);
            if (!Manifest.ConsumerSelections.ContainsKey(build.Profile) || build.Family is not ("Controller" or "Generic") ||
                build.TargetPath != (build.Family == "Controller" ? "renodx-dlss.addon64" : "renodx-dlss5.addon64") ||
                GetComponent(build.PayloadPath).TargetPath != build.TargetPath)
                throw new InvalidOperationException("A package add-on version is configured incorrectly.");
        }
        foreach (string profile in Manifest.AddonBuilds.Select(b => b.Profile).Distinct())
        {
            var baseline = Manifest.ConsumerSelections[profile];
            if (Manifest.AddonBuilds.Count(b => b.Profile == profile && b.PayloadPath == baseline.PayloadPath &&
                    b.TargetPath == baseline.TargetPath && b.Family == baseline.Family) != 1)
                throw new InvalidOperationException("A package profile has no unique default add-on version.");
        }
        foreach (var build in Manifest.NeuralRuntimeBuilds)
        {
            Payload(build.PayloadPath);
            if (GetComponent(build.PayloadPath).TargetPath != "nvngx_dlssnr.dll")
                throw new InvalidOperationException("A package neural runtime has an invalid destination.");
        }
        if ((Manifest.NeuralRuntimeBuilds.Count != 0 &&
            Manifest.NeuralRuntimeBuilds.Count(b => b.Id == Manifest.DefaultNeuralRuntimeBuild) != 1) ||
            (Manifest.NeuralRuntimeBuilds.Count == 0 && Manifest.DefaultNeuralRuntimeBuild != null))
            throw new InvalidOperationException("The package has no unique default neural runtime.");
    }

    public AddonBuild? ResolveAddonBuild(string profile, string? id = null)
    {
        ValidateVersionCatalog();
        if (!Manifest.ConsumerSelections.TryGetValue(profile, out var baseline))
            throw new InvalidOperationException("Choose a supported installation profile.");
        if (id == null)
            return Manifest.AddonBuilds.SingleOrDefault(b => b.Profile == profile && b.PayloadPath == baseline.PayloadPath);
        return Manifest.AddonBuilds.SingleOrDefault(b => b.Id == id && b.Profile == profile)
            ?? throw new InvalidOperationException("Choose an add-on version available for this setup.");
    }

    public NeuralRuntimeBuild? ResolveNeuralRuntimeBuild(string? id = null)
    {
        ValidateVersionCatalog();
        if (id == null && Manifest.NeuralRuntimeBuilds.Count == 0) return null;
        return Manifest.NeuralRuntimeBuilds.SingleOrDefault(b => b.Id == (id ?? Manifest.DefaultNeuralRuntimeBuild))
            ?? throw new InvalidOperationException("Choose a neural runtime version included in this package.");
    }
}