namespace Nexus.Core;

public sealed partial class InstallerEngine
{
    internal const string HelperAddon = "dlss5-feed-helper.addon64";
    internal const string HelperExecutable = "dlss5-feed-host64.exe";
    private static string[] HostProxyNames => [.. GraphicsProxyNames, "winmm.dll", "version.dll"];
    public static bool UsesDx10Helper(InstallOptions options) =>
        options.Route == SetupRoute.Feeder && options.GraphicsApi == GraphicsApi.Direct3D10;

    public static bool NeedsReShadePackage(GameTarget target, InstallOptions options) =>
        target.Proxies.Length == 0 || UsesDx10Helper(options) && HostNeedsReShade(target);
    public static string NeuralRuntimeDirectory(GameTarget target, InstallOptions options) =>
        UsesDx10Helper(options) ? Dx10HostTarget(target).Directory : target.Directory;

    // Feeder resolves host64 relative to its own add-on, not the game executable.
    private static GameTarget Dx10HostTarget(GameTarget target)
    {
        var paths = ReShadePaths.Resolve(target, [], includeShaders: false);
        string host = FileSafety.SafePath(paths.Addons, "host64");
        if (File.Exists(host)) throw new InvalidOperationException("A file occupies the DX10 host64 folder.");
        string proxy = FileSafety.SafePath(host, "dxgi.dll");
        return new(host, host, FileSafety.SafePath(host, HelperExecutable), File.Exists(proxy) ? [proxy] : []);
    }

    private static bool HostNeedsReShade(GameTarget target)
    {
        var host = Dx10HostTarget(target);
        foreach (string name in HostProxyNames)
        {
            string path = FileSafety.SafePath(host.Directory, name);
            if (Directory.Exists(path)) throw new InvalidOperationException($"A folder occupies host64/{name}.");
            if (!File.Exists(path)) continue;
            if (name != "dxgi.dll" || !FileSafety.IsReShade(path))
                throw new InvalidOperationException($"host64/{name} already exists and is not the required ReShade proxy. Nexus will not overwrite or chain a graphics or loader DLL.");
        }
        return host.Proxies.Length == 0;
    }

    private delegate void AddComponent(string label, string destination, string source, string hash,
        bool runtime = false, bool config = false);
    private void AddDx10Host(InstallPlan plan, ReShadeLocations paths, GameTarget host, bool installReShade, AddComponent add)
    {
        foreach (var item in new[]
        {
            (Payload: "dx10/" + HelperAddon, Destination: FileSafety.SafePath(paths.Addons, HelperAddon)),
            (Payload: "dx10/host64/" + HelperExecutable, Destination: host.Executable)
        })
        {
            var component = catalog.GetComponent(item.Payload);
            using var bytes = catalog.OpenSource("payload/" + item.Payload);
            if (!FileSafety.IsX64(bytes)) throw new InvalidOperationException("The DX10 helper package must contain valid 64-bit binaries.");
            add(Path.GetFileName(item.Destination), item.Destination, "payload/" + item.Payload, component.Sha256);
        }
        string proxy = FileSafety.SafePath(host.Directory, "dxgi.dll");
        if (installReShade)
            add("ReShade (host64/dxgi.dll)", proxy, "custom:" + plan.Options.ReShade!.ModulePath, plan.Options.ReShade.ModuleHash);
        else
        {
            string hash = FileSafety.Hash(proxy);
            plan.Files.Add(new("ReShade (host64/dxgi.dll)", proxy, Operation.Keep, null, hash, hash, "Existing helper ReShade preserved"));
        }
        byte[] config = System.Text.Encoding.UTF8.GetBytes("[GENERAL]\nAddonPath=.\\\n\n[INPUT]\nKeyOverlay=36,0,0,0\n");
        using var memory = new MemoryStream(config);
        add("host64/ReShade.ini", FileSafety.SafePath(host.Directory, "ReShade.ini"), "generated:" + Convert.ToBase64String(config), FileSafety.Hash(memory), config: true);
    }
}
