using System.Text;
using System.Text.Json;
using Nexus.Core;

internal static class InventoryTests
{
    public static void Run(string root, BundleCatalog catalog, InstallerEngine engine, Action<bool, string> check)
    {
        string game = Path.Combine(root, "inventory"); Directory.CreateDirectory(game);
        string exe = Path.Combine(game, "Game.exe"); File.WriteAllBytes(exe, Pe("game"));
        File.WriteAllBytes(Path.Combine(game, "dxgi.dll"), Pe("ReShade"));
        var target = new GameDiscovery().Scan(game).Targets.Single();
        var inventory = new GameInventory(catalog); var store = new GameLibraryStore(Path.Combine(root, "inventory-state", "library.json"));
        var initial = inventory.Inspect(target);
        check(initial.Complete && initial.Components.Count == 1, "Inventory identifies only installed components");
        var tracked = store.Record(target, "Test Game", "Manual", initial);
        check(GameFeatures.Describe(tracked).All(f => f.InGame == "Not checked"), "DLL inspection does not claim that features are enabled");
        var native = engine.BuildPlan(target, new(Route: SetupRoute.NativeDlss, GraphicsApi: GraphicsApi.Direct3D12));
        check(!native.CanInstall, "Native route requires native-DLSS confirmation");
        native = engine.BuildPlan(target, new(Route: SetupRoute.NativeDlss, NativeDlssConfirmed: true,
            GraphicsApi: GraphicsApi.Direct3D12));
        check(native.CanInstall && native.Files.Any(f => f.Component == InstallerEngine.PresetName) &&
            native.Files.Any(f => f.Component.EndsWith(".fx")) && !native.Files.Any(f => f.Component is "dlss5-feed.addon64" or "dlss5-feed.cfg" or "nvngx_dlss.dll"),
            "Native setup includes shader support and an inactive preset without Feeder or Super Resolution replacement");
        check(!engine.BuildPlan(target, new(Profile: "legacy", Route: SetupRoute.NativeDlss, NativeDlssConfirmed: true,
            GraphicsApi: GraphicsApi.Direct3D12)).CanInstall, "Native setup rejects the unvalidated Generic consumer");
        check(!engine.BuildPlan(target, new(Route: SetupRoute.NativeDlss, NativeDlssConfirmed: true)).CanInstall, "Native setup does not guess the graphics API");
        check(!engine.BuildPlan(target, new(GraphicsApi: GraphicsApi.Unsupported)).CanInstall,
            "Feeder setup rejects an explicitly unsupported graphics API");
        engine.Install(native);
        tracked = store.Record(target, "Test Game", "Manual", inventory.Inspect(target), SetupRoute.NativeDlss, installed: true);
        check(tracked.Snapshot!.Has("nvngx_dlssnr.dll") && GameFeatures.Describe(tracked).Single(f => f.Key == "nr").InGame == "Not checked", "Installation records components without enabling an in-game feature flag");
        check(store.Read().Single().InstalledAt != null && store.Read().Single().Route == SetupRoute.NativeDlss, "Game route and installation time persist locally");
        tracked = store.ReportFeature(exe, "nr", ReportedFeatureState.Enabled);
        check(GameFeatures.Describe(tracked).Single(f => f.Key == "nr").InGame == "Enabled · reported", "Enabled state is explicitly attributed to the user's report");
        File.WriteAllBytes(Path.Combine(game, "nvngx_dlssnr.dll"), Pe("changed runtime"));
        tracked = store.Record(target, "Test Game", "Manual", inventory.Inspect(target));
        check(GameFeatures.Describe(tracked).Single(f => f.Key == "nr").InGame == "Recheck needed", "Component changes invalidate previously reported settings");
        check(tracked.Snapshot!.Components.Single(c => c.FileName == "nvngx_dlssnr.dll").Version == "Unknown", "Unrecognized runtime versions are not guessed");
        File.WriteAllBytes(Path.Combine(game, "nvngx_dlss.dll"), Pe("native SR"));
        var srHash = FileSafety.Hash(Path.Combine(game, "nvngx_dlss.dll"));
        var nativeAgain = engine.BuildPlan(target, new(Route: SetupRoute.NativeDlss, NativeDlssConfirmed: true,
            GraphicsApi: GraphicsApi.Direct3D12, ReplaceExisting: true));
        engine.Install(nativeAgain);
        check(FileSafety.Hash(Path.Combine(game, "nvngx_dlss.dll")) == srHash, "Native deployment leaves original Super Resolution files intact");
        string plugin = Path.Combine(game, "Game_Data", "Plugins", "x86_64"); Directory.CreateDirectory(plugin);
        File.WriteAllBytes(Path.Combine(plugin, "nvngx_dlssg.dll"), Pe("FG"));
        tracked = store.Record(target, "Test Game", "Manual", inventory.Inspect(target));
        check(tracked.Snapshot!.Has("nvngx_dlssg.dll") && GameFeatures.Describe(tracked).Single(f => f.Key == "fg").InGame == "Not checked", "Nested plugin inventory finds FG without claiming it is active");
        string backup = Path.Combine(game, "DLSS-Nexus-Backups"); Directory.CreateDirectory(backup);
        File.WriteAllBytes(Path.Combine(backup, "nvngx_dlssd.dll"), Pe("old RR"));
        check(!inventory.Inspect(target).Has("nvngx_dlssd.dll"), "Backup components do not appear as installed features");
        File.WriteAllText(Path.Combine(game, "ReShade.ini"), "[GENERAL]\nEffectSearchPaths=Z:\\external\nAddonPath=.\\\n");
        check(engine.BuildPlan(target, new(Route: SetupRoute.NativeDlss, NativeDlssConfirmed: true,
            GraphicsApi: GraphicsApi.Direct3D12)).CanInstall, "Native setup adds a local shader path without writing to an external search folder");
        var missing = inventory.Inspect(target with { Root = Path.Combine(root, "gone"), Directory = Path.Combine(root, "gone") });
        check(!missing.Complete, "Unavailable game folders are explicitly marked incomplete");
        store.Record(target, "Test Game", "Manual", missing);
        bool failed = false; try { store.ReportFeature(exe, "nr", ReportedFeatureState.Enabled); } catch (InvalidOperationException) { failed = true; }
        check(failed, "Feature settings cannot be confirmed against an incomplete scan");
        string corrupt = Path.Combine(root, "corrupt-library.json"); File.WriteAllText(corrupt, "invalid json");
        var bad = new GameLibraryStore(corrupt); failed = false;
        try { bad.Save(tracked); } catch (JsonException) { failed = true; }
        check(failed && File.ReadAllText(corrupt) == "invalid json", "Unreadable library is not silently discarded or overwritten");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); failed = false;
        try { inventory.Inspect(target, cancellation.Token); } catch (OperationCanceledException) { failed = true; }
        check(failed, "Inventory respects cancellation");
        check(store.Read().Count == 1, "Refreshing a game does not create duplicate library entries");
        File.WriteAllText(Path.Combine(game, "ReShade.ini"), "[GENERAL]\nPresetPath=.\\Test.ini\n");
        string preset = Path.Combine(game, "Test.ini"); File.WriteAllText(preset, "Techniques=DLSS5_Feed@DLSS5_Feed.fx\n");
        tracked = store.Record(target, "Test Game", "Manual", inventory.Inspect(target));
        check(tracked.Snapshot!.Configuration!.FeederEffectEnabled == true, "Inventory reads configured Feeder effect without claiming live activity");
        tracked = store.ReportFeature(exe, "nr", ReportedFeatureState.Enabled);
        File.WriteAllText(preset, "Techniques=\n");
        tracked = store.Record(target, "Test Game", "Manual", inventory.Inspect(target));
        check(GameFeatures.Describe(tracked).Single(f => f.Key == "nr").InGame == "Recheck needed", "Preset changes invalidate reported feature settings");
        string transition = Path.Combine(root, "route-transition"); Directory.CreateDirectory(transition);
        File.WriteAllBytes(Path.Combine(transition, "Game.exe"), Pe("game")); File.WriteAllBytes(Path.Combine(transition, "dxgi.dll"), Pe("ReShade"));
        var transitionTarget = new GameDiscovery().Scan(transition).Targets.Single();
        engine.Install(engine.BuildPlan(transitionTarget, new()));
        File.WriteAllText(Path.Combine(transition, "ReShade.ini"), "[GENERAL]\nPresetPath=.\\" + InstallerEngine.PresetName + "\n");
        var options = new InstallOptions(Route: SetupRoute.NativeDlss, NativeDlssConfirmed: true, GraphicsApi: GraphicsApi.Direct3D12, ReplaceExisting: true);
        check(!engine.BuildPlan(transitionTarget, options).CanInstall, "Switching to native blocks an active Feeder effect");
        File.WriteAllText(Path.Combine(transition, InstallerEngine.PresetName), "Techniques=\n");
        var transitioning = engine.BuildPlan(transitionTarget, options);
        check(transitioning.CanInstall && transitioning.Files.Single(f => f.Component == "dlss5-feed.addon64").Action == Operation.Retire, "Native switch retires Feeder with replacement approval");
        var transitionResult = engine.Install(transitioning);
        check(!File.Exists(Path.Combine(transition, "dlss5-feed.addon64")) && File.Exists(Path.Combine(transition, InstallerEngine.PresetName)) && transitionResult.BackupDirectory != null,
            "Native switch retains preset and backed-up add-on while disabling Feeder loading");
        Parallel.Invoke(() => store.Record(target, "Test Game", "Manual", inventory.Inspect(target)),
            () => new GameLibraryStore(Path.Combine(root, "inventory-state", "library.json")).Record(transitionTarget, "Second Game", "Manual", inventory.Inspect(transitionTarget)));
        check(store.Read().Count == 2, "Concurrent library instances preserve both game records");
        string malformed = Path.Combine(root, "invalid-library.json"); File.WriteAllText(malformed, "{\"schema\":1,\"games\":[{\"name\":\"Invalid\",\"root\":\"relative\",\"executable\":\"relative/Game.exe\"}]}");
        failed = false; try { new GameLibraryStore(malformed).Read(); } catch (InvalidDataException) { failed = true; }
        check(failed, "Malformed saved paths are rejected before use");
    }
    private static byte[] Pe(string marker)
    {
        byte[] b = new byte[512]; b[0] = 0x4d; b[1] = 0x5a;
        BitConverter.GetBytes(128).CopyTo(b, 60); BitConverter.GetBytes(0x00004550u).CopyTo(b, 128);
        BitConverter.GetBytes((ushort)0x8664).CopyTo(b, 132); Encoding.ASCII.GetBytes(marker).CopyTo(b, 160); return b;
    }
}
