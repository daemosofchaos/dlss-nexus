using System.Text;
using System.Text.Json;
using Nexus.Core;

internal static class NativeMigrationTests
{
    public static void Run(string root, BundleCatalog catalog, InstallerEngine engine, Action<bool, string> check)
    {
        const string preset = "\uFEFF; Keep 日本語 and comments\r\n Techniques =Lumenite_Kernel@lumenite_Kernel.fx,DLSS5_Feed@DLSS5_Feed.fx,CustomEffect@Custom.fx\r\nTechniqueSorting=DLSS5_Feed@DLSS5_Feed.fx,CustomEffect@Custom.fx\r\nTechniques=CustomEffect@Custom.fx,dlss5_feed\r\n\r\n[DLSS5_Feed.fx]\r\nPreprocessorDefinitions=DLSS5_MV_PROVIDER=3\r\n\r\n[Custom.fx]\r\nStrength=0.75\r\nTechniques=DLSS5_Feed@DLSS5_Feed.fx\r\n";
        var expected = preset.Replace(",DLSS5_Feed@DLSS5_Feed.fx", "").Replace("TechniqueSorting=DLSS5_Feed@DLSS5_Feed.fx,", "TechniqueSorting=").Replace(",dlss5_feed", "");
        var native = new InstallOptions(Route: SetupRoute.NativeDlss, NativeDlssConfirmed: true, GraphicsApi: GraphicsApi.Direct3D12, ReplaceExisting: true, DisableFeederEffects: true);
        GameTarget Game(string name, string selected = "Existing-Feeder.ini", string text = preset)
        {
            string game = Path.Combine(root, "native-migration", name), leaf = Path.Combine(game, "Project", "Binaries", "Win64"); Directory.CreateDirectory(leaf);
            File.WriteAllBytes(Path.Combine(leaf, "Game-Win64-Shipping.exe"), Pe("game"));
            File.WriteAllBytes(Path.Combine(leaf, "dxgi.dll"), Pe("ReShade fixture"));
            File.WriteAllText(Path.Combine(leaf, "ReShade.ini"), "[GENERAL]\r\nAddonPath=.\\\r\nEffectSearchPaths=.\\reshade-shaders\\Shaders\\**\r\nTextureSearchPaths=.\\reshade-shaders\\Textures\\**\r\nPresetPath=.\\" + selected + "\r\n\r\n[RENODX-DLSS]\r\nDLSSQualityMode=2\r\n", new UTF8Encoding(false));
            File.WriteAllBytes(Path.Combine(leaf, selected), Encoding.UTF8.GetBytes(text));
            foreach (var item in new[] { ("dlss5-feed.addon64", "dlss5-feed.addon64"), ("renodx-dlss5.addon64", "legacy/renodx-dlss5.addon64") })
            { using var source = catalog.OpenSource("payload/" + item.Item2); using var file = File.Create(Path.Combine(leaf, item.Item1)); source.CopyTo(file); }
            return new(game, leaf, Path.Combine(leaf, "Game-Win64-Shipping.exe"), [Path.Combine(leaf, "dxgi.dll")]);
        }
        void Reject(Action action, string label)
        { try { action(); } catch (Exception) { check(true, label); return; } check(false, label); }
        foreach (string name in new[] { "Existing-Feeder.ini", InstallerEngine.PresetName })
        {
            var target = Game(name, name); string path = Path.Combine(target.Directory, name), config = Path.Combine(target.Directory, "ReShade.ini");
            var before = Snapshot(target.Root); string hash = FileSafety.Hash(path), configHash = FileSafety.Hash(config);
            var blocked = engine.BuildPlan(target, native with { DisableFeederEffects = false });
            check(!blocked.CanInstall && blocked.CanDisableFeederEffects && blocked.Errors.Any(e => e.Contains("Feeder")), name + ": active Feeder remains a resolvable hard blocker without consent");
            Reject(() => engine.Install(blocked), name + ": Install cannot bypass the unresolved Feeder blocker");
            var plan = engine.BuildPlan(target, native);
            check(plan.CanInstall && !plan.CanDisableFeederEffects && plan.Files.Single(f => f.Destination == path).Action == Operation.Replace, name + ": consent reviews one preset replacement, including when Nexus is selected");
            check(plan.Files.Select(f => f.Destination).Distinct(StringComparer.OrdinalIgnoreCase).Count() == plan.Files.Count && plan.Files.Count(f => f.Action == Operation.Retire) == 2, name + ": destinations stay unique and both previous add-ons are retired");
            check(Same(before, Snapshot(target.Root)), name + ": blocked and resolved reviews are read-only");
            var result = engine.Install(plan);
            check(File.ReadAllBytes(path).SequenceEqual(Encoding.UTF8.GetBytes(expected)), name + ": migration changes only root Feeder tokens; BOM, CRLF, Unicode, comments and effect settings remain exact");
            check(FileSafety.Hash(config) == configHash && GameConfiguration.Read(target, [target.Directory]).FeederEffectEnabled == false, name + ": selected preset and all ReShade/add-on settings are retained with Feeder disabled");
            var receipt = JsonSerializer.Deserialize<InstallReceipt>(File.ReadAllText(Path.Combine(result.BackupDirectory!, "installation.json")), JsonSettings.Options)!;
            var entry = receipt.Files.Single(f => f.Destination == path);
            check(entry.OriginalHash == hash && FileSafety.Hash(Path.Combine(result.BackupDirectory!, entry.BackupFile!)) == hash, name + ": original selected preset has a verified recoverable backup");
            before = Snapshot(target.Root);
            check(engine.Install(engine.BuildPlan(target, native)).FilesChanged == 0 && Same(before, Snapshot(target.Root)), name + ": repeat native setup is byte-preserving without another backup");
        }
        var guards = Game("other-guards");
        check(!engine.BuildPlan(guards, native with { ReplaceExisting = false }).CanInstall, "Preset consent does not bypass add-on replacement consent");
        check(!engine.BuildPlan(guards, native with { GraphicsApi = GraphicsApi.Direct3D10 }).CanInstall &&
            !engine.BuildPlan(guards, native with { NativeDlssConfirmed = false }).CanInstall && !engine.BuildPlan(guards, native with { Profile = "legacy" }).CanInstall,
            "Preset consent does not bypass native API, DLSS confirmation or consumer-family guards");
        string duplicate = Path.Combine(guards.Directory, "renodx-dlss-duplicate.addon64"); File.WriteAllBytes(duplicate, Pe("duplicate"));
        check(!engine.BuildPlan(guards, native).CanInstall, "Preset consent does not bypass conflicting add-on detection"); File.Delete(duplicate);
        var feeder = engine.BuildPlan(guards, native with { Route = SetupRoute.Feeder });
        check(feeder.CanInstall && !feeder.Files.Any(f => f.Component == "Active preset (Feeder off)"), "Native preset consent never disables Feeder on the Feeder route");
        var stale = Game("stale"); var stalePlan = engine.BuildPlan(stale, native);
        File.AppendAllText(Path.Combine(stale.Directory, "Existing-Feeder.ini"), "; edited after review\r\n"); var staleBefore = Snapshot(stale.Root);
        Reject(() => engine.Install(stalePlan), "Selected preset edits after review invalidate migration");
        check(Same(staleBefore, Snapshot(stale.Root)), "Stale migration rejection makes no game-file changes");
        var changed = Game("changed-during-stage"); var stagePlan = engine.BuildPlan(changed, native);
        Reject(() => engine.Install(stagePlan, new InlineProgress(t => { if (t == "Preparing Active preset (Feeder off)") File.AppendAllText(Path.Combine(changed.Directory, "Existing-Feeder.ini"), "; stage edit\r\n"); })), "A selected preset changing during backup preparation stops deployment");
        check(File.Exists(Path.Combine(changed.Directory, "dlss5-feed.addon64")) && !File.Exists(Path.Combine(changed.Directory, "renodx-dlss.addon64")), "Preparation-time preset edits leave active add-ons untouched");
        var rollback = Game("rollback"); var rollbackBefore = Snapshot(rollback.Root); var rollbackPlan = engine.BuildPlan(rollback, native);
        using (var locked = new FileStream(Path.Combine(rollback.Directory, "dlss5-feed.addon64"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Reject(() => engine.Install(rollbackPlan), "A late Feeder-retirement failure rolls back the already-applied preset migration");
        check(Same(rollbackBefore, Snapshot(rollback.Root)), "Migration rollback restores exact original preset, add-ons and settings");
        var bounded = Game("bounded"); File.WriteAllText(Path.Combine(bounded.Directory, "Existing-Feeder.ini"), new string('x', 1024 * 1024 + 1));
        Reject(() => engine.BuildPlan(bounded, native), "Oversized selected presets are rejected before migration");
        var invalid = Game("invalid-utf8"); File.WriteAllBytes(Path.Combine(invalid.Directory, "Existing-Feeder.ini"), [0xff, 0xfe, 0x01]);
        Reject(() => engine.BuildPlan(invalid, native), "Invalid UTF-8 presets are rejected rather than lossy-rewritten");
        var external = Game("external"); string outside = Path.Combine(root, "outside-preset.ini"); File.WriteAllText(outside, "Techniques=DLSS5_Feed\n");
        File.WriteAllText(Path.Combine(external.Directory, "ReShade.ini"), "[GENERAL]\nPresetPath=" + outside + "\n");
        Reject(() => engine.BuildPlan(external, native), "External selected presets are not modified by native migration");
        var linked = Game("linked"); string link = Path.Combine(linked.Directory, "Existing-Feeder.ini"); File.Delete(link);
        try { File.CreateSymbolicLink(link, outside); Reject(() => engine.BuildPlan(linked, native), "Linked selected presets are rejected before migration"); }
        catch (UnauthorizedAccessException) { Console.WriteLine("SKIP: preset link test requires symbolic-link permission"); }
        var last = Game("last-preset-selection"); File.WriteAllText(Path.Combine(last.Directory, "Inactive.ini"), "Techniques=\n");
        File.WriteAllText(Path.Combine(last.Directory, "ReShade.ini"), "[GENERAL]\nPresetPath=Inactive.ini\nPresetPath=Existing-Feeder.ini\n");
        check(!engine.BuildPlan(last, native with { DisableFeederEffects = false }).CanInstall, "Duplicate PresetPath entries use the effective last selection for the blocker");
    }
    private static Dictionary<string, string> Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Where(p => !p.Contains(Path.DirectorySeparatorChar + "DLSS-Nexus-Backups" + Path.DirectorySeparatorChar)).ToDictionary(p => p, FileSafety.Hash);
    private static bool Same(Dictionary<string, string> a, Dictionary<string, string> b) => a.Count == b.Count && a.All(p => b.GetValueOrDefault(p.Key) == p.Value);
    private static byte[] Pe(string marker)
    {
        byte[] b = new byte[1024]; b[0] = 0x4d; b[1] = 0x5a; BitConverter.GetBytes(128).CopyTo(b, 60);
        BitConverter.GetBytes(0x00004550u).CopyTo(b, 128); BitConverter.GetBytes((ushort)0x8664).CopyTo(b, 132); Encoding.ASCII.GetBytes(marker).CopyTo(b, 160); return b;
    }
}