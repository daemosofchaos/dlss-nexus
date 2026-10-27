using System.Text;
using System.Text.Json;
using Nexus.Core;

internal static class VersionSelectionTests
{
    public static void Run(string root, BundleCatalog original, Action<bool, string> check)
    {
        string work = Path.Combine(root, "version-selection"); Directory.CreateDirectory(work);
        var manifest = JsonSerializer.Deserialize<BundleManifest>(JsonSerializer.Serialize(original.Manifest, JsonSettings.Options), JsonSettings.Options)!;
        var bytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in manifest.Components)
        {
            string source = component.PayloadPath.StartsWith("templates/") ? component.PayloadPath : "payload/" + component.PayloadPath;
            using var input = original.OpenSource(source); using var output = new MemoryStream(); input.CopyTo(output); bytes[source] = output.ToArray();
        }
        foreach (var selection in manifest.ConsumerSelections.Values)
            manifest.Components.Single(c => c.PayloadPath == selection.PayloadPath).TargetPath = selection.TargetPath;
        void Component(string path, string target, byte[] data, string version)
        {
            bytes["payload/" + path] = data;
            manifest.Components.Add(new() { PayloadPath = path, TargetPath = target, Sha256 = Hash(data), Version = version });
        }
        Component("versions/old-addon.addon64", "renodx-dlss.addon64", Pe("RenoDX DLSS controller previous"), "old-addon");
        Component("versions/old-nr.dll", "nvngx_dlssnr.dll", Pe("old NR"), "old-nr");
        manifest.AddonBuilds = [
            new() { Id = "addon-current", Profile = "default", Label = "Current ShortFuse", PayloadPath = "renodx-dlss.addon64", TargetPath = "renodx-dlss.addon64", Family = "Controller" },
            new() { Id = "addon-old", Profile = "default", Label = "Previous ShortFuse", Notes = "An alternative, not a proven image-quality improvement.", PayloadPath = "versions/old-addon.addon64", TargetPath = "renodx-dlss.addon64", Family = "Controller" },
            new() { Id = "generic-dx10", Profile = "dx10", Label = "DX10 Generic", PayloadPath = "dx10/renodx-dlss5.addon64", TargetPath = "renodx-dlss5.addon64", Family = "Generic" },
            new() { Id = "generic-legacy", Profile = "legacy", Label = "Legacy Generic", PayloadPath = "legacy/renodx-dlss5.addon64", TargetPath = "renodx-dlss5.addon64", Family = "Generic" }
        ];
        manifest.NeuralRuntimeBuilds = [
            new() { Id = "nr-current", Label = "Current patched FP16 NR", PayloadPath = "nvngx_dlssnr.dll" },
            new() { Id = "nr-old", Label = "Previous original NR", Notes = "Original NVIDIA runtime; GPU and driver support may differ.", PayloadPath = "versions/old-nr.dll" }
        ];
        manifest.DefaultNeuralRuntimeBuild = "nr-current";
        var catalog = new BundleCatalog(manifest, key => new MemoryStream(bytes[key], false)); var engine = new InstallerEngine(catalog);
        var native = new InstallOptions(Route: SetupRoute.NativeDlss, NativeDlssConfirmed: true, GraphicsApi: GraphicsApi.Direct3D12);
        string H(string payload) => catalog.GetComponent(payload).Sha256;
        GameTarget Game(string name)
        {
            string directory = Path.Combine(work, name); Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "Game.exe"), Pe("game"));
            File.WriteAllBytes(Path.Combine(directory, "dxgi.dll"), Pe("ReShade fixture"));
            File.WriteAllText(Path.Combine(directory, "ReShade.ini"), "[GENERAL]\nAddonPath=.\\\nEffectSearchPaths=.\\reshade-shaders\\Shaders\\**\nTextureSearchPaths=.\\reshade-shaders\\Textures\\**\nPresetPath=.\\Active.ini\n");
            File.WriteAllText(Path.Combine(directory, "Active.ini"), "Techniques=\n[Custom.fx]\nStrength=0.75\n");
            return new(directory, directory, Path.Combine(directory, "Game.exe"), [Path.Combine(directory, "dxgi.dll")]);
        }
        void Reject(Action action, string name)
        { try { action(); } catch (Exception) { check(true, name); return; } check(false, name); }
        check(catalog.VerifyAll() == original.Manifest.Components.Count + 2, "Version catalog verifies every current and previous source, not only the default");
        var defaultTarget = Game("defaults"); var defaults = engine.BuildPlan(defaultTarget, native);
        check(defaults.AddonBuildId == "addon-current" && defaults.NeuralRuntimeBuildId == "nr-current", "Catalog defaults select the current add-on and runtime independently");
        foreach (var route in new[] { SetupRoute.NativeDlss, SetupRoute.Feeder })
        foreach (string addon in new[] { "addon-current", "addon-old" })
        foreach (string nr in new[] { "nr-current", "nr-old" })
        {
            string label = route + "/" + addon + "/" + nr;
            var target = Game(label.Replace('/', '-')); var before = Snapshot(target.Root);
            var options = native with { Route = route, AddonBuild = addon, NeuralRuntimeBuild = nr };
            var plan = engine.BuildPlan(target, options);
            var a = manifest.AddonBuilds.Single(b => b.Id == addon); var n = manifest.NeuralRuntimeBuilds.Single(b => b.Id == nr);
            check(plan.CanInstall && plan.Family == "Controller" && plan.AddonLabel == a.Label && plan.NeuralRuntimeLabel == n.Label, label + ": compatible independently selected versions are reviewed by name");
            check(plan.Files.Count(f => f.Component == "nvngx_dlssnr.dll") == 1 && plan.Files.Count(f => f.Component == "renodx-dlss.addon64") == 1 &&
                plan.Files.Select(f => f.Destination).Distinct(StringComparer.OrdinalIgnoreCase).Count() == plan.Files.Count, label + ": exactly one runtime and add-on destination");
            check(Same(before, Snapshot(target.Root)), label + ": changing version choices and preview do not write game files");
            var result = engine.Install(plan);
            check(FileSafety.Hash(Path.Combine(target.Directory, "nvngx_dlssnr.dll")) == H(n.PayloadPath) &&
                FileSafety.Hash(Path.Combine(target.Directory, "renodx-dlss.addon64")) == H(a.PayloadPath) && !Directory.Exists(Path.Combine(target.Directory, "versions")), label + ": installs only the chosen bytes at canonical names");
            var receipt = JsonSerializer.Deserialize<InstallReceipt>(File.ReadAllText(Path.Combine(result.BackupDirectory!, "installation.json")), JsonSettings.Options)!;
            check(receipt.AddonBuildId == addon && receipt.NeuralRuntimeBuildId == nr, label + ": receipt records the reviewed version IDs");
            var after = Snapshot(target.Root);
            check(engine.Install(engine.BuildPlan(target, options)).FilesChanged == 0 && Same(after, Snapshot(target.Root)), label + ": selected-version verification is idempotent");
        }
        engine.Install(defaults);
        var old = native with { AddonBuild = "addon-old", NeuralRuntimeBuild = "nr-old" };
        var unauthorized = engine.BuildPlan(defaultTarget, old);
        check(!unauthorized.CanInstall && unauthorized.Files.Single(f => f.Component == "renodx-dlss.addon64").Action == Operation.Blocked &&
            unauthorized.Files.Single(f => f.Component == "nvngx_dlssnr.dll").Action == Operation.Keep, "Version selection alone never grants replacement permission");
        check(!engine.BuildPlan(defaultTarget, old with { ReplaceNeuralRuntime = true }).CanInstall, "Runtime downgrade still needs add-on replacement consent");
        var keep = engine.BuildPlan(defaultTarget, native with { ReplaceExisting = true, NeuralRuntimeBuild = "nr-old" }); var keepBefore = Snapshot(defaultTarget.Root);
        check(keep.CanInstall && !keep.UsesBundledNeuralRuntime && keep.Warnings.Any(w => w.Contains("will not be installed")), "Preserved runtime review clearly says the selected alternative will not be installed");
        check(engine.Install(keep).FilesChanged == 0 && Same(keepBefore, Snapshot(defaultTarget.Root)), "Keeping the existing NR writes neither a downgrade nor a new backup");
        old = old with { ReplaceExisting = true, ReplaceNeuralRuntime = true };
        var downgrade = engine.BuildPlan(defaultTarget, old); var downResult = engine.Install(downgrade);
        check(downResult.FilesChanged == 2, "Approved downgrade replaces only the selected NR and add-on");
        var downReceipt = JsonSerializer.Deserialize<InstallReceipt>(File.ReadAllText(Path.Combine(downResult.BackupDirectory!, "installation.json")), JsonSettings.Options)!;
        check(downReceipt.Files.Count == 2 && downReceipt.Files.All(f => f.BackupFile != null && FileSafety.Hash(Path.Combine(downResult.BackupDirectory!, f.BackupFile)) == f.OriginalHash), "Downgrade preserves exact current-version originals in verified backups");
        check(File.ReadAllText(Path.Combine(defaultTarget.Directory, "Active.ini")).Contains("Strength=0.75"), "Version downgrade retains active preset settings");
        var upResult = engine.Install(engine.BuildPlan(defaultTarget, native with { ReplaceExisting = true, ReplaceNeuralRuntime = true }));
        check(upResult.FilesChanged == 2 && FileSafety.Hash(Path.Combine(defaultTarget.Directory, "nvngx_dlssnr.dll")) == H("nvngx_dlssnr.dll"), "Approved return to current versions uses the same guarded transaction");
        var misnamed = Game("misnamed-controller"); string oldName = Path.Combine(misnamed.Directory, "renodx-dlss5.addon64");
        File.WriteAllBytes(oldName, bytes["payload/versions/old-addon.addon64"]);
        var oldInventory = new GameInventory(catalog).Inspect(misnamed);
        check(oldInventory.Components.Single(c => c.FileName == "renodx-dlss5.addon64").Name == "ShortFuse", "Inventory identifies the previous Controller by its markers even under a Generic-style filename");
        var canonical = engine.BuildPlan(misnamed, old);
        check(canonical.CanInstall && canonical.Files.Any(f => f.Destination == oldName && f.Action == Operation.Retire), "Previous Controller filename migration is reviewed as a backed-up retirement");
        engine.Install(canonical);
        check(!File.Exists(oldName) && FileSafety.Hash(Path.Combine(misnamed.Directory, "renodx-dlss.addon64")) == H("versions/old-addon.addon64"), "Previous Controller bytes are installed under the canonical ShortFuse name without a duplicate");
        foreach (string id in new[] { "missing", "", "../outside.dll" })
        {
            Reject(() => engine.BuildPlan(defaultTarget, native with { AddonBuild = id }), "Unknown/empty/path-shaped add-on ID rejected: " + id);
            Reject(() => engine.BuildPlan(defaultTarget, native with { NeuralRuntimeBuild = id }), "Unknown/empty/path-shaped NR ID rejected: " + id);
        }
        Reject(() => engine.BuildPlan(defaultTarget, native with { Profile = "legacy", AddonBuild = "addon-old" }), "An add-on version cannot bypass its profile/family restrictions");
        string custom = Path.Combine(work, "custom.addon64"); File.WriteAllBytes(custom, Pe("RenoDX DLSS controller custom"));
        var customPlan = engine.BuildPlan(Game("custom"), native with { Profile = "experimental", ExperimentalAddon = custom, NeuralRuntimeBuild = "nr-old" });
        check(customPlan.CanInstall && customPlan.AddonBuildId == null && customPlan.NeuralRuntimeBuildId == "nr-old", "Custom add-on testing remains independent from NR version selection");
        Reject(() => engine.BuildPlan(Game("custom-conflict"), native with { Profile = "experimental", ExperimentalAddon = custom, AddonBuild = "addon-old" }), "Custom file and incompatible bundled version cannot silently compete");
        var dx = Game("dx10"); string host = Path.Combine(dx.Directory, "host64"); Directory.CreateDirectory(host);
        File.WriteAllBytes(Path.Combine(host, "dxgi.dll"), Pe("ReShade host"));
        var dxOptions = new InstallOptions(GraphicsApi: GraphicsApi.Direct3D10, Route: SetupRoute.Feeder, AddonBuild: "generic-dx10", NeuralRuntimeBuild: "nr-old");
        var dxPlan = engine.BuildPlan(dx, dxOptions);
        check(dxPlan.CanInstall && dxPlan.Family == "Generic" && dxPlan.Files.Single(f => f.Component == "nvngx_dlssnr.dll").Destination == Path.Combine(host, "nvngx_dlssnr.dll"), "DX10 places the selected NR only in its helper and retains Generic family constraints");
        Reject(() => engine.BuildPlan(dx, dxOptions with { AddonBuild = "addon-old" }), "Previous ShortFuse version cannot bypass DX10 Generic-only routing");
        var rollback = Game("rollback"); engine.Install(engine.BuildPlan(rollback, native)); var rollbackBefore = Snapshot(rollback.Root);
        using (var locked = new FileStream(Path.Combine(rollback.Directory, "renodx-dlss.addon64"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Reject(() => engine.Install(engine.BuildPlan(rollback, old)), "Late add-on replacement failure rolls back an already-applied NR downgrade");
        check(Same(rollbackBefore, Snapshot(rollback.Root)), "Version-switch rollback restores all original active bytes");
        var stale = Game("stale"); engine.Install(engine.BuildPlan(stale, native)); var stalePlan = engine.BuildPlan(stale, old);
        File.WriteAllBytes(Path.Combine(stale.Directory, "nvngx_dlssnr.dll"), Pe("NR edited after review")); var staleBefore = Snapshot(stale.Root);
        Reject(() => engine.Install(stalePlan), "Editing an installed NR after version review invalidates the plan");
        check(Same(staleBefore, Snapshot(stale.Root)), "Stale version review changes no active files");
        byte[] originalOldNr = bytes["payload/versions/old-nr.dll"];
        var stage = Game("stage"); engine.Install(engine.BuildPlan(stage, native)); var stagePlan = engine.BuildPlan(stage, old); var stageBefore = Snapshot(stage.Root);
        Reject(() => engine.Install(stagePlan, new InlineProgress(t => { if (t == "Preparing nvngx_dlssnr.dll") bytes["payload/versions/old-nr.dll"] = Pe("altered payload"); })), "Selected alternative changing during staging fails its hash guard");
        check(Same(stageBefore, Snapshot(stage.Root)), "Alternative staging corruption stops before active-file replacement");
        Reject(() => catalog.VerifyAll(), "Package-wide verification catches corruption in an alternative runtime");
        bytes["payload/versions/old-nr.dll"] = originalOldNr;
        var savedBuild = manifest.AddonBuilds[1];
        manifest.AddonBuilds.Add(savedBuild); Reject(catalog.ValidateVersionCatalog, "Duplicate add-on version IDs are rejected"); manifest.AddonBuilds.RemoveAt(manifest.AddonBuilds.Count - 1);
        manifest.DefaultNeuralRuntimeBuild = "unknown"; Reject(catalog.ValidateVersionCatalog, "An unresolved default NR ID is rejected"); manifest.DefaultNeuralRuntimeBuild = "nr-current";
        savedBuild.PayloadPath = "../escape"; Reject(catalog.ValidateVersionCatalog, "Alternative payload traversal is rejected"); savedBuild.PayloadPath = "versions/old-addon.addon64";
        savedBuild.Family = "Generic"; Reject(catalog.ValidateVersionCatalog, "Alternative family/canonical-destination mismatch is rejected"); savedBuild.Family = "Controller";
        manifest.NeuralRuntimeBuilds.Add(new() { Id = "nr-bad-target", Label = "Bad NR", PayloadPath = "nvngx_dlss.dll" }); Reject(catalog.ValidateVersionCatalog, "Runtime selection cannot target Super Resolution"); manifest.NeuralRuntimeBuilds.RemoveAt(manifest.NeuralRuntimeBuilds.Count - 1);
        byte[] originalOldAddon = bytes["payload/versions/old-addon.addon64"]; bytes["payload/versions/old-addon.addon64"] = Pe("NRGlobalTone\0 wrong family");
        Reject(() => engine.BuildPlan(defaultTarget, old), "Selected add-on bytes must match the declared family, not just the filename"); bytes["payload/versions/old-addon.addon64"] = originalOldAddon;
        bytes["payload/versions/old-nr.dll"] = Pe("32-bit runtime", 0x014c);
        Reject(() => engine.BuildPlan(defaultTarget, old), "Selected NR alternative must be a 64-bit binary"); bytes["payload/versions/old-nr.dll"] = originalOldNr;
        Component("versions/future-addon.addon64", "renodx-dlss.addon64", Pe("RenoDX DLSS controller future"), "future-addon");
        Component("versions/future-nr.dll", "nvngx_dlssnr.dll", Pe("future NR"), "future-nr");
        manifest.AddonBuilds.Add(new() { Id = "addon-future", Profile = "default", Label = "Future ShortFuse", PayloadPath = "versions/future-addon.addon64", TargetPath = "renodx-dlss.addon64", Family = "Controller" });
        manifest.NeuralRuntimeBuilds.Add(new() { Id = "nr-future", Label = "Future NR", PayloadPath = "versions/future-nr.dll" });
        var future = engine.BuildPlan(Game("future"), native with { AddonBuild = "addon-future", NeuralRuntimeBuild = "nr-future" });
        check(future.CanInstall && future.AddonBuildId == "addon-future" && future.NeuralRuntimeBuildId == "nr-future", "A third add-on and NR can be added using only catalog entries, with no new installer branch");
        manifest.ConsumerSelections["default"].PayloadPath = "versions/future-addon.addon64"; manifest.DefaultNeuralRuntimeBuild = "nr-future";
        var futureDefault = engine.BuildPlan(Game("future-default"), native);
        check(futureDefault.AddonBuildId == "addon-future" && futureDefault.NeuralRuntimeBuildId == "nr-future" && catalog.VerifyAll() == original.Manifest.Components.Count + 4, "Future defaults and complete package verification are catalog-driven");
    }
    private static byte[] Pe(string marker, ushort architecture = 0x8664)
    {
        byte[] data = new byte[512]; data[0] = 0x4d; data[1] = 0x5a; BitConverter.GetBytes(128).CopyTo(data, 60);
        BitConverter.GetBytes(0x00004550u).CopyTo(data, 128); BitConverter.GetBytes(architecture).CopyTo(data, 132);
        Encoding.ASCII.GetBytes(marker).CopyTo(data, 160); return data;
    }
    private static string Hash(byte[] data) { using var stream = new MemoryStream(data); return FileSafety.Hash(stream); }
    private static Dictionary<string, string> Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Where(p => !Path.GetRelativePath(root, p).Split(Path.DirectorySeparatorChar).Contains("DLSS-Nexus-Backups"))
        .ToDictionary(p => Path.GetRelativePath(root, p), FileSafety.Hash);
    private static bool Same(Dictionary<string, string> a, Dictionary<string, string> b) => a.Count == b.Count && a.All(p => b.GetValueOrDefault(p.Key) == p.Value);
}
