using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nexus.Core;

var suite = new Suite(args.Length == 0 ? Path.Combine(Path.GetTempPath(), "Nexus-tests-" + Guid.NewGuid().ToString("N")) : args[0], args.Length > 1 ? args[1] : null);
suite.Run();

sealed class Suite(string root, string? officialSetup)
{
    private int assertions;
    private readonly Dictionary<string, byte[]> assets = new(StringComparer.OrdinalIgnoreCase);
    private BundleCatalog catalog = null!;
    private InstallerEngine engine = null!;
    private readonly Dictionary<string, string> results = [];
    private void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        assertions++; Console.WriteLine("PASS: " + name); results[name] = "passed";
    }
    private void Throws(Action action, string name)
    {
        try { action(); } catch (Exception) { Check(true, name); return; }
        Check(false, name);
    }
    private static byte[] Pe(string marker, ushort architecture = 0x8664)
    {
        byte[] bytes = new byte[512]; bytes[0] = 0x4d; bytes[1] = 0x5a;
        BitConverter.GetBytes(128).CopyTo(bytes, 60);
        BitConverter.GetBytes(0x00004550u).CopyTo(bytes, 128);
        BitConverter.GetBytes(architecture).CopyTo(bytes, 132);
        Encoding.ASCII.GetBytes(marker).CopyTo(bytes, 160);
        return bytes;
    }
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    private string Game(string name, string? subdirectory = null, string exe = "Game.exe")
    {
        string game = Path.Combine(root, name); string leaf = subdirectory == null ? game : Path.Combine(game, subdirectory);
        Directory.CreateDirectory(leaf);
        File.WriteAllBytes(Path.Combine(leaf, exe), Pe("Game"));
        File.WriteAllBytes(Path.Combine(leaf, "dxgi.dll"), Pe("ReShade 6.8.1"));
        return game;
    }
    private GameTarget Target(string path) => new GameDiscovery().Scan(path).Targets.Single();
    private static Dictionary<string, string> Snapshot(string path) => Directory.GetFiles(path, "*", SearchOption.AllDirectories)
        .ToDictionary(p => Path.GetRelativePath(path, p), FileSafety.Hash);
    private static bool Same(Dictionary<string, string> a, Dictionary<string, string> b) => a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var value) && value == p.Value);
    private void MakeCatalog()
    {
        var manifest = new BundleManifest
        {
            BundleVersion = "test",
            ConsumerSelections = new()
            {
                ["default"] = new() { PayloadPath = "renodx-dlss.addon64", TargetPath = "renodx-dlss.addon64", Family = "Controller" },
                ["experimental"] = new() { PayloadPath = "experimental/renodx-dlss.addon64", TargetPath = "renodx-dlss.addon64", Family = "Controller" },
                ["legacy"] = new() { PayloadPath = "legacy/renodx-dlss5.addon64", TargetPath = "renodx-dlss5.addon64", Family = "Generic" },
                ["dx10"] = new() { PayloadPath = "dx10/renodx-dlss5.addon64", TargetPath = "renodx-dlss5.addon64", Family = "Generic" }
            }
        };
        var payload = new Dictionary<string, byte[]>
        {
            ["dlss5-feed.addon64"] = Pe("DLSS5 Feed 1.19.0"),
            ["dx10/dlss5-feed-helper.addon64"] = Pe("DLSS5 Feed helper 1.19.0"),
            ["dx10/host64/dlss5-feed-host64.exe"] = Pe("DLSS5 Feed host 1.19.0"),
            ["dx10/renodx-dlss5.addon64"] = Pe("NRGlobalTone\0 DX10 Generic 7"),
            ["renodx-dlss.addon64"] = Pe("RenoDX DLSS controller"),
            ["experimental/renodx-dlss.addon64"] = Pe("RenoDX DLSS controller experimental"),
            ["legacy/renodx-dlss5.addon64"] = Pe("NRGlobalTone\0"),
            ["nvngx_dlss.dll"] = Pe("SR runtime"), ["nvngx_dlssnr.dll"] = Pe("NR runtime"),
            ["dlss5-feed.cfg"] = Encoding.UTF8.GetBytes("enabled=1\nmode=2\n"),
            ["reshade-shaders/Shaders/DLSS5_Feed.fx"] = Encoding.UTF8.GetBytes("// feed shader"),
            ["reshade-shaders/Shaders/include/lumenite_Compute.fxh"] = Encoding.UTF8.GetBytes("// compute"),
            ["reshade-shaders/Textures/lumenite_bluenoise256.png"] = [1, 2, 3, 4]
        };
        foreach (var item in payload)
        {
            assets["payload/" + item.Key] = item.Value;
            manifest.Components.Add(new() { PayloadPath = item.Key, TargetPath = item.Key, Sha256 = Hash(item.Value) });
        }
        byte[] preset = Encoding.UTF8.GetBytes("Techniques=Lumenite_Kernel@lumenite_Kernel.fx,DLSS5_Feed@DLSS5_Feed.fx\n");
        assets["templates/" + InstallerEngine.PresetName] = preset;
        manifest.Components.Add(new() { PayloadPath = "templates/" + InstallerEngine.PresetName, TargetPath = InstallerEngine.PresetName, Sha256 = Hash(preset) });
        catalog = new(manifest, key => new MemoryStream(assets[key], false)); engine = new(catalog);
    }
    public void Run()
    {
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root); MakeCatalog();
        Check(catalog.VerifyAll() == 14, "All catalog sources and preset hashes verified");
        string nested = Game("nested", "Chronos/Binaries/Win64", "Chronos-Win64-Shipping.exe");
        File.WriteAllBytes(Path.Combine(nested, "Launcher.exe"), Pe("launcher"));
        File.WriteAllBytes(Path.Combine(nested, "dxgi.dll"), Pe("ReShade launcher"));
        var discovered = new GameDiscovery().Scan(nested);
        Check(discovered.Targets.Count == 1, "Nested shipping executable discovered without selecting launcher");
        var target = discovered.Targets.Single();
        Check(target.Directory.EndsWith(Path.Combine("Binaries", "Win64")), "Destination resolves to binary folder, not game root");
        Check(new GameDiscovery().Scan(target.Executable).Targets.Count == 1, "Executable selection resolves adjacent ReShade");
        string ambiguous = Game("ambiguous", "One"); Game("ambiguous", "Two", "Second.exe");
        Check(new GameDiscovery().Scan(ambiguous).Targets.Count == 2, "Multiple valid installations remain explicit choices");
        File.WriteAllBytes(Path.Combine(target.Directory, "Another.exe"), Pe("another game"));
        Check(new GameDiscovery().Scan(nested).Targets.Count == 2, "Multiple executables in one ReShade folder remain explicit choices");
        File.Delete(Path.Combine(target.Directory, "Another.exe"));
        string backupLeaf = Path.Combine(nested, "DLSS-Nexus-Backups", "old"); Directory.CreateDirectory(backupLeaf);
        File.WriteAllBytes(Path.Combine(backupLeaf, "Old.exe"), Pe("game")); File.WriteAllBytes(Path.Combine(backupLeaf, "dxgi.dll"), Pe("ReShade"));
        Check(new GameDiscovery().Scan(nested).Targets.Count == 1, "Backup trees are excluded from discovery");
        string fake = Game("fake-proxy"); File.WriteAllBytes(Path.Combine(fake, "dxgi.dll"), Pe("unrelated proxy"));
        Check(new GameDiscovery().Scan(fake).Targets.Count == 0, "An arbitrary dxgi DLL is not accepted as ReShade");
        string x86 = Game("x86"); File.WriteAllBytes(Path.Combine(x86, "Game.exe"), Pe("game", 0x014c));
        Check(new GameDiscovery().Scan(x86).Targets.Count == 0, "32-bit game is excluded");
        string proxy86 = Game("proxy-x86"); File.WriteAllBytes(Path.Combine(proxy86, "dxgi.dll"), Pe("ReShade", 0x014c));
        Check(new GameDiscovery().Scan(proxy86).Targets.Count == 0, "32-bit ReShade is excluded");
        using (var cancel = new CancellationTokenSource()) { cancel.Cancel(); Throws(() => new GameDiscovery().Scan(nested, cancel.Token), "Discovery respects cancellation"); }
        Throws(() => new GameDiscovery().Scan(Path.GetPathRoot(root)!), "Whole-drive discovery is rejected");
        Throws(() => FileSafety.SafePath(nested, "../outside.dll"), "Path traversal is rejected");
        Throws(() => FileSafety.SafePath(nested, Path.GetFullPath(root)), "Absolute package paths are rejected");
        string link = Path.Combine(root, "linked-game");
        try { Directory.CreateSymbolicLink(link, nested); Throws(() => new GameDiscovery().Scan(link), "Linked game roots are rejected"); }
        catch (UnauthorizedAccessException) { Console.WriteLine("SKIP: link test requires symbolic-link permission"); }

        var before = Snapshot(nested);
        var plan = engine.BuildPlan(target, new());
        Check(plan.CanInstall && plan.UsesBundledNeuralRuntime, "New installation includes the bundled runtime without a separate approval step");
        Check(Same(before, Snapshot(nested)), "Preview makes no file or backup changes");
        Check(!plan.Warnings.Any(w => w.Contains("unsigned", StringComparison.OrdinalIgnoreCase)), "Default review has no runtime acceptance warning");
        var installed = engine.Install(plan);
        Check(installed.FilesChanged == 10, "Native engine installs all selected components and ReShade configuration");
        Check(installed.BackupDirectory != null && File.Exists(Path.Combine(installed.BackupDirectory, "installation.json")), "Native install writes a recovery receipt");
        Check(!File.Exists(Path.Combine(nested, "dlss5-feed.addon64")), "Game root is not used as the binary destination");
        Check(File.Exists(Path.Combine(target.Directory, "reshade-shaders", "Shaders", "DLSS5_Feed.fx")), "Shader is installed in resolved ReShade shader folder");
        var repeated = engine.BuildPlan(target, new());
        Check(repeated.CanInstall && repeated.Files.All(f => f.Action == Operation.Current), "Repeated installation is idempotent");
        var after = Snapshot(nested);
        Check(engine.Install(repeated).FilesChanged == 0 && Same(after, Snapshot(nested)), "Up-to-date verification writes no backups");

        string preserve = Game("preserve"); var preserveTarget = Target(preserve);
        File.WriteAllBytes(Path.Combine(preserve, "nvngx_dlss.dll"), Pe("existing SR"));
        File.WriteAllBytes(Path.Combine(preserve, "nvngx_dlssnr.dll"), Pe("existing NR"));
        File.WriteAllText(Path.Combine(preserve, "dlss5-feed.cfg"), "custom settings");
        File.WriteAllText(Path.Combine(preserve, "ReShade.ini"), "[GENERAL]\nPresetPath=Custom.ini\n");
        string sr = FileSafety.Hash(Path.Combine(preserve, "nvngx_dlss.dll"));
        string nr = FileSafety.Hash(Path.Combine(preserve, "nvngx_dlssnr.dll"));
        string cfg = FileSafety.Hash(Path.Combine(preserve, "dlss5-feed.cfg"));
        var preserving = engine.BuildPlan(preserveTarget, new(ReplaceExisting: true));
        Check(preserving.CanInstall && !preserving.UsesBundledNeuralRuntime, "Existing differing NR is preserved by default");
        engine.Install(preserving);
        Check(FileSafety.Hash(Path.Combine(preserve, "nvngx_dlss.dll")) == sr, "SR is protected even with replacement enabled");
        Check(FileSafety.Hash(Path.Combine(preserve, "nvngx_dlssnr.dll")) == nr, "NR is protected by default");
        Check(FileSafety.Hash(Path.Combine(preserve, "dlss5-feed.cfg")) == cfg && File.ReadAllText(Path.Combine(preserve, "ReShade.ini")).Contains("PresetPath=Custom.ini"), "Existing preset selection and Feeder configuration are preserved while missing shader paths are added");
        var nrPlan = engine.BuildPlan(preserveTarget, new(ReplaceExisting: true, ReplaceNeuralRuntime: true));
        Check(nrPlan.CanInstall && nrPlan.UsesBundledNeuralRuntime, "Explicit NR replacement is installable without a separate runtime approval step");
        var nrResult = engine.Install(nrPlan);
        Check(nrResult.FilesChanged == 1, "Explicit NR update changes only NR");
        Check(FileSafety.Hash(Path.Combine(preserve, "nvngx_dlss.dll")) == sr, "Explicit NR update never replaces SR");
        var receipt = JsonSerializer.Deserialize<InstallReceipt>(File.ReadAllText(Path.Combine(nrResult.BackupDirectory!, "installation.json")), JsonSettings.Options)!;
        Check(FileSafety.Hash(Path.Combine(nrResult.BackupDirectory!, receipt.Files.Single().BackupFile!)) == nr, "Original NR backup is byte-verified");

        var legacy = engine.BuildPlan(target, new(Profile: "legacy", ReplaceExisting: true));
        Check(legacy.CanInstall && legacy.Files.Any(f => f.Action == Operation.Retire), "Legacy migration backs up the alternate canonical add-on");
        engine.Install(legacy);
        Check(File.Exists(Path.Combine(target.Directory, "renodx-dlss5.addon64")) && !File.Exists(Path.Combine(target.Directory, "renodx-dlss.addon64")), "Legacy migration leaves exactly one neural add-on");
        Check(!engine.BuildPlan(target, new()).CanInstall, "Family migration without replacement approval is blocked");
        engine.Install(engine.BuildPlan(target, new(ReplaceExisting: true)));
        Check(File.Exists(Path.Combine(target.Directory, "renodx-dlss.addon64")) && !File.Exists(Path.Combine(target.Directory, "renodx-dlss5.addon64")), "Return to recommended profile leaves one neural add-on");
        string custom = Path.Combine(root, "custom.addon64"); File.WriteAllBytes(custom, Pe("RenoDX DLSS controller future"));
        var experimental = engine.BuildPlan(target, new(Profile: "experimental", ReplaceExisting: true, ExperimentalAddon: custom));
        Check(experimental.CanInstall && experimental.Files.Single(f => f.Component == "renodx-dlss.addon64").ExpectedHash == FileSafety.Hash(custom), "Custom experimental add-on is selected without scripts");
        engine.Install(experimental);
        Check(assets["payload/renodx-dlss.addon64"].SequenceEqual(Pe("RenoDX DLSS controller")), "Experimental installation never changes embedded default");
        engine.Install(engine.BuildPlan(target, new(ReplaceExisting: true)));
        File.WriteAllBytes(custom, Pe("NRGlobalTone\0 future"));
        Check(engine.BuildPlan(target, new(Profile: "experimental", ReplaceExisting: true, ExperimentalAddon: custom)).Family == "Generic", "Custom Generic experiment uses the correct family");
        File.WriteAllBytes(custom, Pe("unknown"));
        Throws(() => engine.BuildPlan(target, new(Profile: "experimental", ExperimentalAddon: custom)), "Unrecognized experimental add-on is rejected");
        byte[] saved = assets["payload/experimental/renodx-dlss.addon64"]; assets["payload/experimental/renodx-dlss.addon64"] = [0];
        Check(engine.BuildPlan(target, new()).CanInstall, "Unselected experimental corruption does not block recommended profile");
        Throws(() => engine.BuildPlan(target, new(Profile: "experimental")), "Selected corrupted experimental payload fails integrity check");
        assets["payload/experimental/renodx-dlss.addon64"] = saved;
        saved = assets["payload/dlss5-feed.addon64"]; assets["payload/dlss5-feed.addon64"] = [0];
        Throws(() => engine.BuildPlan(target, new()), "Corrupt bundled component blocks installation before writes"); assets["payload/dlss5-feed.addon64"] = saved;
        File.WriteAllBytes(Path.Combine(target.Directory, "renodx-dlss-v9.addon64"), Pe("duplicate"));
        Check(!engine.BuildPlan(target, new()).CanInstall, "Versioned neural duplicates are blocked"); File.Delete(Path.Combine(target.Directory, "renodx-dlss-v9.addon64"));
        File.WriteAllBytes(Path.Combine(target.Directory, "alexs-toolkit.addon64"), Pe("conflict"));
        Check(!engine.BuildPlan(target, new()).CanInstall, "Conflicting neural routes are blocked"); File.Delete(Path.Combine(target.Directory, "alexs-toolkit.addon64"));
        var stale = engine.BuildPlan(target, new()); File.WriteAllText(Path.Combine(target.Directory, "dlss5-feed.cfg"), "changed after preview");
        var staleSnapshot = Snapshot(nested); Throws(() => engine.Install(stale), "Changed target after preview requires a new review");
        Check(Same(staleSnapshot, Snapshot(nested)), "Stale-plan rejection makes no game changes");
        stale = engine.BuildPlan(target, new()); File.WriteAllBytes(Path.Combine(target.Directory, "renodx-dlss-duplicate.addon64"), Pe("duplicate"));
        Throws(() => engine.Install(stale), "A duplicate added after preview blocks installation"); File.Delete(Path.Combine(target.Directory, "renodx-dlss-duplicate.addon64"));
        stale = engine.BuildPlan(target, new()); File.WriteAllText(Path.Combine(target.Directory, "ReShade.ini"), "[GENERAL]\nPresetPath=new.ini\n");
        Throws(() => engine.Install(stale), "Configuration changes after preview are rejected");

        string configured = Game("configured", "Binaries/Win64"); var configuredTarget = Target(configured);
        File.WriteAllText(Path.Combine(configuredTarget.Directory, "ReShade.ini"), "[GENERAL]\nEffectSearchPaths=..\\..\\Shared\\Shaders\\**\nTextureSearchPaths=..\\..\\Shared\\Textures\\**\nAddonPath=.\\addons\n");
        var configuredPlan = engine.BuildPlan(configuredTarget, new());
        Check(configuredPlan.Files.Single(f => f.Component == "DLSS5_Feed.fx").Destination == Path.Combine(configured, "Shared", "Shaders", "DLSS5_Feed.fx"), "Configured shader search path resolves relative to ReShade, within game root");
        Check(configuredPlan.Files.Single(f => f.Component == "lumenite_bluenoise256.png").Destination == Path.Combine(configured, "Shared", "Textures", "lumenite_bluenoise256.png"), "Configured texture path is honored");
        Check(configuredPlan.Files.Single(f => f.Component == "dlss5-feed.addon64").Destination == Path.Combine(configuredTarget.Directory, "addons", "dlss5-feed.addon64"), "Custom local add-on path is honored");
        Check(configuredPlan.Files.Single(f => f.Component == "dlss5-feed.cfg").Destination == Path.Combine(configuredTarget.Directory, "addons", "dlss5-feed.cfg"), "Feeder configuration stays beside the add-on in a custom add-on folder");
        engine.Install(configuredPlan);
        File.WriteAllText(Path.Combine(configuredTarget.Directory, "ReShade.ini"), "[GENERAL]\nEffectSearchPaths=" + Path.Combine(root, "external-shaders") + "\n");
        Throws(() => engine.BuildPlan(configuredTarget, new()), "External shader destinations are not written silently");
        File.WriteAllText(Path.Combine(configuredTarget.Directory, "ReShade.ini"), "[GENERAL]\nEffectSearchPaths=.\\*.Shaders\n");
        Throws(() => engine.BuildPlan(configuredTarget, new()), "Unsupported search-path wildcards are rejected");

        string failed = Game("rollback"); var failedTarget = Target(failed);
        File.WriteAllText(Path.Combine(failed, InstallerEngine.PresetName), "original custom preset");
        var failedPlan = engine.BuildPlan(failedTarget, new(ReplaceExisting: true));
        string originalPreset = FileSafety.Hash(Path.Combine(failed, InstallerEngine.PresetName));
        using (var locked = new FileStream(Path.Combine(failed, InstallerEngine.PresetName), FileMode.Open, FileAccess.Read, FileShare.Read))
            Throws(() => engine.Install(failedPlan), "Locked destination triggers transactional rollback");
        Check(FileSafety.Hash(Path.Combine(failed, InstallerEngine.PresetName)) == originalPreset, "Failed installation preserves the original preset");
        Check(!File.Exists(Path.Combine(failed, "dlss5-feed.addon64")) && !File.Exists(Path.Combine(failed, "nvngx_dlssnr.dll")), "Files created before a failure are rolled back");
        var failedReceipt = Directory.GetFiles(Path.Combine(failed, "DLSS-Nexus-Backups"), "installation.json", SearchOption.AllDirectories).Single();
        Check(JsonSerializer.Deserialize<InstallReceipt>(File.ReadAllText(failedReceipt), JsonSettings.Options)!.Status == "Rolled back", "Rollback result is recorded in the recovery receipt");

        Throws(() => FileSafety.EnsureGameClosed(Environment.ProcessPath!), "Running-game guard detects an active executable");
        string recursive = Game("recursive-addons"); var recursiveTarget = Target(recursive);
        File.WriteAllText(Path.Combine(recursive, "ReShade.ini"), "[GENERAL]\nAddonPath=.\\addons\\**\n");
        string deepAddon = Path.Combine(recursive, "addons", "deeper", "renodx-dlss-v99.addon64");
        Directory.CreateDirectory(Path.GetDirectoryName(deepAddon)!); File.WriteAllBytes(deepAddon, Pe("duplicate"));
        Check(!engine.BuildPlan(recursiveTarget, new()).CanInstall, "Recursive add-on paths are checked for nested neural duplicates");
        File.Delete(deepAddon);
        engine.Install(engine.BuildPlan(recursiveTarget, new()));
        engine.Install(engine.BuildPlan(recursiveTarget, new(Profile: "legacy", ReplaceExisting: true)));
        Check(engine.BuildPlan(recursiveTarget, new(Profile: "legacy")).CanInstall, "Native backups cannot be loaded as add-ons through recursive paths");
        Check(Directory.GetFiles(Path.Combine(recursive, "DLSS-Nexus-Backups"), "*.addon64", SearchOption.AllDirectories).Length == 0, "Backed-up add-ons have non-loadable backup extensions");
        string oldBackup = Path.Combine(recursive, "addons", "backups", "renodx-dlss-vold.addon64");
        Directory.CreateDirectory(Path.GetDirectoryName(oldBackup)!); File.WriteAllBytes(oldBackup, Pe("old backup duplicate"));
        Check(!engine.BuildPlan(recursiveTarget, new(Profile: "legacy")).CanInstall, "Loadable files in old backup folders are detected under recursive add-on paths");
        File.Delete(oldBackup);
        string retireOnly = Game("retire-only"); var retireTarget = Target(retireOnly);
        engine.Install(engine.BuildPlan(retireTarget, new()));
        File.WriteAllBytes(Path.Combine(retireOnly, "renodx-dlss5.addon64"), Pe("NRGlobalTone\0"));
        var retireResult = engine.Install(engine.BuildPlan(retireTarget, new(ReplaceExisting: true)));
        Check(retireResult.FilesChanged == 1 && !File.Exists(Path.Combine(retireOnly, "renodx-dlss5.addon64")), "Retire-only transaction does not require staged payloads");
        string duringStage = Game("changed-during-stage"); var stageTarget = Target(duringStage);
        var stagePlan = engine.BuildPlan(stageTarget, new());
        var mutation = new InlineProgress(text =>
        {
            if (text == "Preparing " + InstallerEngine.PresetName)
                File.WriteAllBytes(Path.Combine(duringStage, "renodx-dlss-unreviewed.addon64"), Pe("duplicate"));
        });
        Throws(() => engine.Install(stagePlan, mutation), "A duplicate added during staging blocks all game-file writes");
        Check(!File.Exists(Path.Combine(duringStage, "dlss5-feed.addon64")), "Preparation-time conflict stops before deployment");

        FeatureTests.Run(root, engine, Check, officialSetup);
        ReShadeSetupTests.Run(root, catalog, engine, Check);
        NativeMigrationTests.Run(root, catalog, engine, Check);
        InventoryTests.Run(root, catalog, engine, Check);
        Dx10Tests.Run(root, catalog, engine, Check);
        VersionSelectionTests.Run(root, catalog, Check);
        File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { assertions, results, fixtures = root, officialReShadeIntegration = officialSetup != null, scope = "Synthetic isolated files; no real games or GPU rendering tested" }, JsonSettings.Options));
        Console.WriteLine($"\n{assertions} native C# assertions passed. Fixtures: {root}");
    }
}

sealed class InlineProgress(Action<string> action) : IProgress<string>
{
    public void Report(string value) => action(value);
}
