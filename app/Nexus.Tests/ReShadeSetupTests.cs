using System.Text;
using System.Text.Json;
using Nexus.Core;

internal static class ReShadeSetupTests
{
    public static void Run(string root, BundleCatalog catalog, InstallerEngine engine, Action<bool, string> check)
    {
        string Folder(string name) { string path = Path.Combine(root, "reshade-setup", name); Directory.CreateDirectory(path); return path; }
        GameTarget Game(string name, string? config = null)
        {
            string path = Folder(name);
            File.WriteAllBytes(Path.Combine(path, "Game.exe"), Pe("game"));
            File.WriteAllBytes(Path.Combine(path, "dxgi.dll"), Pe("ReShade fixture"));
            if (config != null) File.WriteAllText(Path.Combine(path, "ReShade.ini"), config, new UTF8Encoding(false));
            return new GameDiscovery().Scan(path).Targets.Single();
        }
        void Reject(Action action, string label)
        {
            try { action(); } catch (Exception) { check(true, label); return; }
            check(false, label);
        }
        var shaderComponents = catalog.Manifest.Components.Where(c => c.TargetPath.StartsWith("reshade-shaders/")).ToArray();
        var native = new InstallOptions(Route: SetupRoute.NativeDlss, NativeDlssConfirmed: true, GraphicsApi: GraphicsApi.Direct3D12);
        foreach (var api in new[] { GraphicsApi.Direct3D11, GraphicsApi.Direct3D12 })
        foreach (var route in new[] { SetupRoute.NativeDlss, SetupRoute.Feeder })
        {
            string label = $"{route}-{api}"; var target = Game(label);
            var options = native with { GraphicsApi = api, Route = route };
            var before = Snapshot(target.Directory);
            var plan = engine.BuildPlan(target, options);
            check(plan.CanInstall && plan.Files.Any(f => f.Component == "ReShade.ini") && plan.Files.Any(f => f.Component == InstallerEngine.PresetName), label + ": review includes both configuration and preset");
            check(shaderComponents.All(c => plan.Files.Any(f => f.Destination == Path.Combine(target.Directory, c.TargetPath.Replace('/', Path.DirectorySeparatorChar)) && f.ExpectedHash == c.Sha256)), label + ": review includes every bundled shader, include and texture");
            check(Same(before, Snapshot(target.Directory)), label + ": preview creates no directories or files");
            engine.Install(plan);
            check(Directory.Exists(Path.Combine(target.Directory, "reshade-shaders", "Shaders")) && Directory.Exists(Path.Combine(target.Directory, "reshade-shaders", "Textures")), label + ": shader and texture folders are created");
            check(shaderComponents.All(c => FileSafety.Hash(Path.Combine(target.Directory, c.TargetPath.Replace('/', Path.DirectorySeparatorChar))) == c.Sha256), label + ": all shader, include and texture bytes match the catalog");
            string config = File.ReadAllText(Path.Combine(target.Directory, "ReShade.ini"));
            check(config.Contains("EffectSearchPaths=.\\reshade-shaders\\Shaders\\**") && config.Contains("TextureSearchPaths=.\\reshade-shaders\\Textures\\**") && config.Contains("PresetPath=.\\" + InstallerEngine.PresetName), label + ": emitted configuration selects deployed folders and preset");
            check(GameConfiguration.Read(target, [target.Directory]).FeederEffectEnabled == (route == SetupRoute.Feeder), label + ": only the Feeder route enables Feeder effects");
            if (route == SetupRoute.NativeDlss)
                check(!File.Exists(Path.Combine(target.Directory, "dlss5-feed.addon64")) && !File.Exists(Path.Combine(target.Directory, "dlss5-feed.cfg")) && !File.Exists(Path.Combine(target.Directory, "nvngx_dlss.dll")), label + ": shader preparation does not add Feeder or replace native Super Resolution");
            before = Snapshot(target.Directory);
            check(engine.Install(engine.BuildPlan(target, options)).FilesChanged == 0 && Same(before, Snapshot(target.Directory)), label + ": repeated setup is byte-preserving and idempotent");
        }

        string reportedConfig = "\uFEFF[GENERAL]\r\nAddonPath=.\\\r\nEffectSearchPaths=.\\\r\nTextureSearchPaths=.\\\r\nPresetPath=.\\ReShadePreset.ini\r\nPerformanceMode=0\r\n\r\n[INPUT]\r\nKeyOverlay=36,0,0,0\r\n\r\n[RENODX-DLSS]\r\nDLSSAutoExposure=0\r\nDLSSQualityMode=0\r\n\r\n[OVERLAY]\r\nLanguage=日本語\r\nTutorialProgress=4\r\n";
        var repair = Game("reported-incomplete-native", reportedConfig);
        string configPath = Path.Combine(repair.Directory, "ReShade.ini"), originalHash = FileSafety.Hash(configPath);
        var repairPlan = engine.BuildPlan(repair, native);
        check(repairPlan.CanInstall && repairPlan.Files.Single(f => f.Component == "ReShade.ini").Action == Operation.Replace, "Incomplete native setup reviews the minimal configuration repair without runtime replacement approval");
        var repairResult = engine.Install(repairPlan);
        string repaired = Encoding.UTF8.GetString(File.ReadAllBytes(configPath));
        check(repaired.StartsWith('\uFEFF') && repaired.Contains("\r\n") && repaired.Contains("EffectSearchPaths=.\\reshade-shaders\\Shaders\\**,.\\") && repaired.Contains("TextureSearchPaths=.\\reshade-shaders\\Textures\\**,.\\"), "Repair keeps BOM, CRLF and existing search entries while adding the deployed shader folders");
        check(repaired.Contains("[RENODX-DLSS]\r\nDLSSAutoExposure=0\r\nDLSSQualityMode=0") && repaired.Contains("Language=日本語\r\nTutorialProgress=4") && repaired.Contains("PerformanceMode=0"), "Repair preserves add-on, overlay, Unicode and performance settings");
        check(repaired.Contains("PresetPath=.\\" + InstallerEngine.PresetName) && GameConfiguration.Read(repair, [repair.Directory]).FeederEffectEnabled == false, "Missing default ReShade preset is replaced by the inactive Nexus preset selection");
        var receipt = JsonSerializer.Deserialize<InstallReceipt>(File.ReadAllText(Path.Combine(repairResult.BackupDirectory!, "installation.json")), JsonSettings.Options)!;
        var entry = receipt.Files.Single(f => f.Destination == configPath);
        check(entry.OriginalHash == originalHash && FileSafety.Hash(Path.Combine(repairResult.BackupDirectory!, entry.BackupFile!)) == originalHash, "Original ReShade configuration is backed up and checksum-verified before repair");
        var afterRepair = Snapshot(repair.Directory);
        check(engine.Install(engine.BuildPlan(repair, native)).FilesChanged == 0 && Same(afterRepair, Snapshot(repair.Directory)), "Repair does not duplicate search paths or rewrite settings on a repeat install");

        var custom = Game("custom-settings", "[GENERAL]\nAddonPath=.\\addons\\\nEffectSearchPaths=.\\custom-shaders\\**\nTextureSearchPaths=.\\custom-textures\\**\nPresetPath=.\\Custom.ini\nStartupPresetPath=.\\Custom.ini\n\n[INPUT]\nKeyOverlay=35,0,0,0\n");
        File.WriteAllText(Path.Combine(custom.Directory, "Custom.ini"), "Techniques=CustomEffect@Custom.fx\n");
        File.WriteAllText(Path.Combine(custom.Directory, InstallerEngine.PresetName), "Techniques=CustomEffect@Custom.fx\n");
        string customConfigHash = FileSafety.Hash(Path.Combine(custom.Directory, "ReShade.ini")), customPresetHash = FileSafety.Hash(Path.Combine(custom.Directory, InstallerEngine.PresetName));
        var customPlan = engine.BuildPlan(custom, native); engine.Install(customPlan);
        check(FileSafety.Hash(Path.Combine(custom.Directory, "ReShade.ini")) == customConfigHash && FileSafety.Hash(Path.Combine(custom.Directory, InstallerEngine.PresetName)) == customPresetHash, "Complete custom ReShade settings, selected preset and existing Nexus preset remain byte-identical");
        check(customPlan.Files.Where(f => f.Component.EndsWith(".fx")).All(f => FileSafety.IsInside(Path.Combine(custom.Directory, "custom-shaders"), f.Destination)), "Explicit local shader and texture folders are honored rather than replaced by defaults");

        var existingDefault = Game("existing-default-preset", "[GENERAL]\nPresetPath=.\\ReShadePreset.ini\n");
        File.WriteAllText(Path.Combine(existingDefault.Directory, "ReShadePreset.ini"), "Techniques=CustomEffect@Custom.fx\n");
        engine.Install(engine.BuildPlan(existingDefault, native));
        check(File.ReadAllText(Path.Combine(existingDefault.Directory, "ReShade.ini")).Contains("PresetPath=.\\ReShadePreset.ini"), "An existing default preset is not replaced or deselected");
        var active = Game("active-preset-after-repair");
        File.WriteAllText(Path.Combine(active.Directory, InstallerEngine.PresetName), "Techniques=DLSS5_Feed@DLSS5_Feed.fx\n");
        check(!engine.BuildPlan(active, native).CanInstall, "Native setup cannot select an existing active Feeder preset while repairing missing configuration");

        var stale = Game("stale-default-preset", "[GENERAL]\nPresetPath=.\\ReShadePreset.ini\nEffectSearchPaths=.\\\nTextureSearchPaths=.\\\n");
        var stalePlan = engine.BuildPlan(stale, native);
        File.WriteAllText(Path.Combine(stale.Directory, "ReShadePreset.ini"), "Techniques=\n");
        var beforeStale = Snapshot(stale.Directory);
        Reject(() => engine.Install(stalePlan), "A previously missing default preset appearing after review invalidates the configuration repair");
        check(Same(beforeStale, Snapshot(stale.Directory)), "Stale configuration repair makes no file changes");

        var rollback = Game("rollback-configuration", "[GENERAL]\nEffectSearchPaths=.\\\nTextureSearchPaths=.\\\nPresetPath=.\\ReShadePreset.ini\n\n[RENODX-DLSS]\nDLSSQualityMode=2\n");
        string consumer = Path.Combine(rollback.Directory, "renodx-dlss.addon64"); File.WriteAllBytes(consumer, Pe("RenoDX DLSS previous"));
        var rollbackBefore = Snapshot(rollback.Directory);
        var rollbackPlan = engine.BuildPlan(rollback, native with { ReplaceExisting = true });
        using (var locked = new FileStream(consumer, FileMode.Open, FileAccess.Read, FileShare.Read))
            Reject(() => engine.Install(rollbackPlan), "A late add-on replacement failure rolls back shader files and the configuration repair");
        check(Same(rollbackBefore, Snapshot(rollback.Directory)), "Rollback restores exact original settings and leaves no newly installed shader, texture or preset files");
        var folderConflict = Game("configuration-folder-conflict"); Directory.CreateDirectory(Path.Combine(folderConflict.Directory, "ReShade.ini"));
        Reject(() => engine.BuildPlan(folderConflict, native), "A directory occupying ReShade.ini is rejected before deployment");
    }
    private static Dictionary<string, string> Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Where(p => !FileSafety.IsInside(Path.Combine(root, "DLSS-Nexus-Backups"), p)).ToDictionary(p => p, FileSafety.Hash);
    private static bool Same(Dictionary<string, string> before, Dictionary<string, string> after) => before.Count == after.Count && before.All(p => after.GetValueOrDefault(p.Key) == p.Value);
    private static byte[] Pe(string marker)
    {
        byte[] bytes = new byte[1024]; bytes[0] = 0x4d; bytes[1] = 0x5a;
        BitConverter.GetBytes(128).CopyTo(bytes, 60); BitConverter.GetBytes(0x00004550u).CopyTo(bytes, 128);
        BitConverter.GetBytes((ushort)0x8664).CopyTo(bytes, 132); Encoding.ASCII.GetBytes(marker).CopyTo(bytes, 160); return bytes;
    }
}
