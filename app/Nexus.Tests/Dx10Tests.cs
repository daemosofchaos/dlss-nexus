using System.Text;
using System.Text.Json;
using Nexus.Core;

internal static class Dx10Tests
{
    public static void Run(string root, BundleCatalog catalog, InstallerEngine engine, Action<bool, string> check)
    {
        void Reject(Action action, string name)
        { try { action(); } catch (Exception) { check(true, name); return; } check(false, name); }
        string Folder(string name) { string path = Path.Combine(root, "dx10", name); Directory.CreateDirectory(path); return path; }
        GameTarget Game(string name)
        {
            string path = Folder(name); string exe = Path.Combine(path, "ClassicGame.exe");
            File.WriteAllBytes(exe, ImportedPe("d3d10.dll"));
            File.WriteAllBytes(Path.Combine(path, "dxgi.dll"), Pe("ReShade fixture"));
            return new GameDiscovery().Scan(path).Targets.Single();
        }
        void HostProxy(string folder)
        { Directory.CreateDirectory(folder); File.WriteAllBytes(Path.Combine(folder, "dxgi.dll"), Pe("ReShade host fixture")); }
        var options = new InstallOptions(GraphicsApi: GraphicsApi.Direct3D10);
        var target = Game("new"); string game = target.Directory, host = Path.Combine(game, "host64");
        check(target.DetectedApi == GraphicsApi.Direct3D10, "DX10 game is detected from PE imports");
        string mixed = Path.Combine(Folder("imports"), "Mixed.exe");
        File.WriteAllBytes(mixed, ImportedPe("d3d10.dll", "d3d11.dll"));
        check(GraphicsApiDetection.Detect(mixed) == GraphicsApi.Unknown, "Mixed DX10/DX11 imports require an explicit API choice");
        File.WriteAllBytes(mixed, ImportedPe("d3d10.dll", "d3d10_1.dll"));
        check(GraphicsApiDetection.Detect(mixed) == GraphicsApi.Direct3D10, "DX10 and DX10.1 imports select the same helper route");
        check(InstallerEngine.NeedsReShadePackage(target, options), "Existing game ReShade does not satisfy missing DX10 host ReShade");
        Reject(() => engine.BuildPlan(target, options), "DX10 host ReShade cannot be silently omitted");
        HostProxy(host);
        check(!InstallerEngine.NeedsReShadePackage(target, options), "Verified game and host ReShade do not need a new download");
        File.WriteAllBytes(Path.Combine(game, "nvngx_dlssnr.dll"), Pe("unrelated existing game NR"));
        string rootRuntime = FileSafety.Hash(Path.Combine(game, "nvngx_dlssnr.dll"));
        var before = Snapshot(game);
        var plan = engine.BuildPlan(target, options);
        check(plan.CanInstall && plan.UsesDx10Helper && plan.Family == "Generic", "DX10 defaults to the Feeder helper and Generic consumer");
        check(plan.Files.Single(f => f.Component == "renodx-dlss5.addon64").ExpectedHash == catalog.GetComponent("dx10/renodx-dlss5.addon64").Sha256,
            "DX10 uses its newer dedicated Generic build rather than the old Legacy payload");
        check(plan.Files.Any(f => f.Component == "dlss5-feed-helper.addon64" && f.Destination == Path.Combine(game, f.Component)) &&
            !plan.Files.Any(f => f.Component == "dlss5-feed.addon64"), "DX10 installs the helper instead of the regular 64-bit Feeder");
        check(plan.Files.Where(f => f.Component is "nvngx_dlss.dll" or "nvngx_dlssnr.dll" or "renodx-dlss5.addon64" or "dlss5-feed-host64.exe")
            .All(f => FileSafety.IsInside(host, f.Destination)), "DX10 host owns the neural consumer, helper executable and runtimes");
        check(plan.Files.Any(f => f.Component == "ReShade (host64/dxgi.dll)" && f.Action == Operation.Keep), "Existing host ReShade is reviewed and preserved");
        check(plan.Files.Any(f => f.Component == "host64/ReShade.ini") && plan.Warnings.Any(w => w.Contains("beta")), "DX10 review includes host configuration and beta limitations");
        check(Same(before, Snapshot(game)), "DX10 planning changes no fixture files");
        check(plan.UsesBundledNeuralRuntime && plan.Files.Single(f => f.Component == "nvngx_dlssnr.dll").Action == Operation.Install,
            "An existing game runtime does not prevent installing the bundled runtime into the new host");
        var native = engine.BuildPlan(target, options with { Route = SetupRoute.NativeDlss, NativeDlssConfirmed = true });
        check(!native.CanInstall && native.Errors.Any(e => e.Contains("Feeder helper")), "DX10 cannot be installed as native DLSS");
        Reject(() => engine.BuildPlan(target, options with { Profile = "experimental" }), "DX10 rejects unsupported custom consumer layouts");
        string custom = Path.Combine(Folder("custom"), "Generic.addon64"); File.WriteAllBytes(custom, Pe("NRGlobalTone\0 custom Generic"));
        var customPlan = engine.BuildPlan(target, options with { Profile = "experimental", ExperimentalAddon = custom });
        check(customPlan.CanInstall && customPlan.Files.Single(f => f.Component == "renodx-dlss5.addon64").ExpectedHash == FileSafety.Hash(custom),
            "DX10 permits an explicitly selected compatible Generic custom add-on");
        File.WriteAllBytes(custom, Pe("RenoDX DLSS controller"));
        Reject(() => engine.BuildPlan(target, options with { Profile = "experimental", ExperimentalAddon = custom }), "DX10 custom setup rejects in-process ShortFuse controllers");
        check(engine.BuildPlan(target, options with { Profile = "legacy" }).Warnings.Any(w => w.Contains("616.64")), "DX10 Legacy review discloses known newer-driver failures");
        var result = engine.Install(plan);
        check(result.FilesChanged > 0 && FileSafety.Hash(Path.Combine(game, "nvngx_dlssnr.dll")) == rootRuntime, "DX10 deployment keeps existing game runtimes untouched");
        check(!File.Exists(Path.Combine(game, "renodx-dlss5.addon64")) && File.Exists(Path.Combine(host, "renodx-dlss5.addon64")), "Only the helper loads the DX10 neural consumer");
        check(new GameDiscovery().Scan(game).Targets.Count == 1 && new GameDiscovery().Scan(Path.Combine(host, "dlss5-feed-host64.exe"), includeWithoutReShade: true).Targets.Count == 0,
            "The Feeder host is never offered as a game executable");
        var receipt = JsonSerializer.Deserialize<InstallReceipt>(File.ReadAllText(Path.Combine(result.BackupDirectory!, "installation.json")), JsonSettings.Options)!;
        check(receipt.GraphicsApi == GraphicsApi.Direct3D10 && receipt.Files.Any(f => f.Destination == Path.Combine(host, "dlss5-feed-host64.exe")), "Recovery receipt records the DX10 API and host files");
        var repeated = engine.BuildPlan(target, options); before = Snapshot(game);
        check(repeated.CanInstall && engine.Install(repeated).FilesChanged == 0 && Same(before, Snapshot(game)), "Repeated DX10 setup verifies without reinstalling or rewriting settings");
        var inventory = new GameInventory(catalog); var snapshot = inventory.Inspect(target);
        check(snapshot.Complete && snapshot.Has("dlss5-feed-helper.addon64") && snapshot.Has("dlss5-feed-host64.exe") &&
            snapshot.Components.Count(c => c.Name == "ReShade") == 2, "Inventory finds both ReShade instances and the DX10 helper components");
        var store = new GameLibraryStore(Path.Combine(root, "dx10-state", "library.json"));
        store.Record(target, "Classic", "Manual", snapshot, SetupRoute.Feeder, installed: true, graphicsApi: GraphicsApi.Direct3D10);
        store.ReportFeature(target.Executable, "nr", ReportedFeatureState.Enabled);
        File.AppendAllText(Path.Combine(host, "ReShade.ini"), "\n[DX10Fixture]\nChanged=1\n");
        var tracked = store.Record(target, "Classic", "Manual", inventory.Inspect(target));
        check(store.Read().Single().GraphicsApi == GraphicsApi.Direct3D10 && GameFeatures.Describe(tracked).Single(f => f.Key == "nr").InGame == "Recheck needed",
            "The DX10 override survives status refresh and host configuration changes invalidate reports");
        plan = engine.BuildPlan(target, options); before = Snapshot(game);
        File.WriteAllBytes(Path.Combine(host, "version.dll"), Pe("unrelated loader"));
        Reject(() => engine.Install(plan), "A host loader appearing after review blocks deployment"); File.Delete(Path.Combine(host, "version.dll"));
        check(Same(before, Snapshot(game)), "Rejected host loader deployment changes no existing files");
        byte[] proxy = File.ReadAllBytes(Path.Combine(host, "dxgi.dll"));
        File.WriteAllBytes(Path.Combine(host, "dxgi.dll"), Pe("unrelated DLL"));
        Reject(() => engine.BuildPlan(target, options), "An unrelated host graphics DLL cannot be overwritten or chained");
        File.WriteAllBytes(Path.Combine(host, "dxgi.dll"), proxy);
        string configPath = Path.Combine(host, "ReShade.ini"), config = File.ReadAllText(configPath);
        File.WriteAllText(configPath, "[GENERAL]\nAddonPath=..\\\n");
        Reject(() => engine.BuildPlan(target, options), "Host add-on search paths cannot escape host64"); File.WriteAllText(configPath, config);
        File.WriteAllText(configPath, "[GENERAL]\nAddonPath=.\\subfolder\\\n");
        check(!engine.BuildPlan(target, options).CanInstall, "Host add-on paths must include the consumer beside the helper executable"); File.WriteAllText(configPath, config);
        File.WriteAllText(Path.Combine(game, "ReShade.ini"), "[GENERAL]\nAddonPath=.\\**\n");
        check(!engine.BuildPlan(target, options).CanInstall, "A recursive game add-on path cannot load the host's neural add-on");
        File.Delete(Path.Combine(game, "ReShade.ini"));

        var separate = Game("custom-addon-path"); string addonDirectory = Path.Combine(separate.Directory, "addons");
        File.WriteAllText(Path.Combine(separate.Directory, "ReShade.ini"), "[GENERAL]\nAddonPath=.\\addons\\\n");
        HostProxy(Path.Combine(addonDirectory, "host64"));
        var separatePlan = engine.BuildPlan(separate, options);
        check(separatePlan.CanInstall && separatePlan.Files.Single(f => f.Component == "dlss5-feed-helper.addon64").Destination == Path.Combine(addonDirectory, "dlss5-feed-helper.addon64") &&
            separatePlan.Files.Single(f => f.Component == "dlss5-feed-host64.exe").Destination == Path.Combine(addonDirectory, "host64", "dlss5-feed-host64.exe"),
            "DX10 host follows Feeder's module-relative path under a custom local AddonPath");

        var migration = Game("migration"); engine.Install(engine.BuildPlan(migration, new()));
        HostProxy(Path.Combine(migration.Directory, "host64"));
        check(!engine.BuildPlan(migration, options).CanInstall, "Switching existing in-process Feeder to DX10 requires replacement consent");
        engine.Install(engine.BuildPlan(migration, options with { ReplaceExisting = true }));
        check(!File.Exists(Path.Combine(migration.Directory, "dlss5-feed.addon64")) && !File.Exists(Path.Combine(migration.Directory, "renodx-dlss.addon64")) &&
            File.Exists(Path.Combine(migration.Directory, "dlss5-feed-helper.addon64")), "DX10 migration backs up and retires in-process Feeder and neural add-on");
        var normal = engine.BuildPlan(migration, new(GraphicsApi: GraphicsApi.Direct3D11, ReplaceExisting: true)); engine.Install(normal);
        check(!normal.UsesDx10Helper && File.Exists(Path.Combine(migration.Directory, "dlss5-feed.addon64")) && !File.Exists(Path.Combine(migration.Directory, "dlss5-feed-helper.addon64")),
            "Returning to DX11 retires the helper without deleting dormant host files");
    }
    private static Dictionary<string, string> Snapshot(string path) => Directory.GetFiles(path, "*", SearchOption.AllDirectories).ToDictionary(p => p, FileSafety.Hash);
    private static bool Same(Dictionary<string, string> a, Dictionary<string, string> b) => a.Count == b.Count && a.All(p => b.GetValueOrDefault(p.Key) == p.Value);
    private static byte[] Pe(string marker)
    {
        byte[] b = new byte[1024]; b[0] = 0x4d; b[1] = 0x5a; BitConverter.GetBytes(128).CopyTo(b, 60);
        BitConverter.GetBytes(0x00004550u).CopyTo(b, 128); BitConverter.GetBytes((ushort)0x8664).CopyTo(b, 132);
        Encoding.ASCII.GetBytes(marker).CopyTo(b, 160); return b;
    }
    private static byte[] ImportedPe(params string[] names)
    {
        byte[] b = Pe(""); BitConverter.GetBytes((ushort)1).CopyTo(b, 134); BitConverter.GetBytes((ushort)240).CopyTo(b, 148);
        BitConverter.GetBytes((ushort)0x20b).CopyTo(b, 152); BitConverter.GetBytes(16u).CopyTo(b, 260); BitConverter.GetBytes(0x1000u).CopyTo(b, 272);
        BitConverter.GetBytes(0x1000u).CopyTo(b, 404); BitConverter.GetBytes(512u).CopyTo(b, 408); BitConverter.GetBytes(512u).CopyTo(b, 412);
        for (int i = 0; i < names.Length; i++)
        { BitConverter.GetBytes(0x1100u + (uint)i * 40).CopyTo(b, 524 + i * 20); Encoding.ASCII.GetBytes(names[i]).CopyTo(b, 768 + i * 40); }
        return b;
    }
}
