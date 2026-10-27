using System.Reflection;
using System.Text.Json;

namespace Nexus.Core;

public sealed partial class BundleCatalog
{
    private readonly Func<string, Stream> open;
    public BundleManifest Manifest { get; }
    public BundleCatalog(BundleManifest manifest, Func<string, Stream> openSource) { Manifest = manifest; open = openSource; }
    public static BundleCatalog FromEmbedded(Assembly assembly)
    {
        var names = assembly.GetManifestResourceNames().ToDictionary(n => n.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
        Stream Open(string path) => names.TryGetValue(path, out var name)
            ? assembly.GetManifestResourceStream(name)! : throw new InvalidOperationException($"The package is missing {path}.");
        using var json = Open("catalog.json");
        var manifest = JsonSerializer.Deserialize<BundleManifest>(json, JsonSettings.Options)
            ?? throw new InvalidOperationException("The package catalog is invalid.");
        return new(manifest, Open);
    }
    public Stream OpenSource(string source) => open(source);
    public Component GetComponent(string payloadPath)
    {
        var entries = Manifest.Components.Where(c => c.PayloadPath.Equals(payloadPath, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (entries.Length != 1) throw new InvalidOperationException($"Invalid package entry: {payloadPath}.");
        return entries[0];
    }
    public void Verify(string source, string expectedHash)
    {
        using var stream = OpenSource(source);
        if (!FileSafety.Hash(stream).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Package integrity check failed: {source}. Obtain a fresh copy of the installer.");
    }
    public string VerifyPreset()
    {
        var component = GetComponent("templates/DLSS-Nexus.ini");
        Verify(component.PayloadPath, component.Sha256);
        return component.Sha256;
    }
    public int VerifyAll()
    {
        ValidateVersionCatalog();
        foreach (var c in Manifest.Components)
            Verify(c.PayloadPath.StartsWith("templates/") ? c.PayloadPath : "payload/" + c.PayloadPath, c.Sha256);
        foreach (var build in Manifest.AddonBuilds)
        {
            using var stream = OpenSource("payload/" + build.PayloadPath);
            if (FileSafety.AddonFamily(stream) != build.Family)
                throw new InvalidOperationException($"The package add-on family is incorrect: {build.Id}.");
        }
        foreach (var build in Manifest.NeuralRuntimeBuilds)
        {
            using var stream = OpenSource("payload/" + build.PayloadPath);
            if (!FileSafety.IsX64(stream))
                throw new InvalidOperationException($"The package neural runtime is not 64-bit: {build.Id}.");
        }
        return Manifest.Components.Count;
    }
}