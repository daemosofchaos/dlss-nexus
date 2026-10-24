using System.Net;
using System.Text;
using System.Text.Json;
using Nexus.Core;

internal static class FeatureTests
{
    public static void Run(string root, InstallerEngine engine, Action<bool, string> check, string? officialSetup)
    {
        void Reject(Action action, string name)
        { try { action(); } catch (Exception) { check(true, name); return; } check(false, name); }
        string Folder(string relative) { string p = Path.GetFullPath(Path.Combine(root, "features", relative)); Directory.CreateDirectory(p); return p; }
        static string Quote(string value) => "\"" + value.Replace("\\", "\\\\") + "\"";
        string steam = Folder("Steam"), second = Folder("SecondLibrary"), epic = Folder("EpicManifests");
        string steamGame = Folder("SecondLibrary/steamapps/common/ExampleGame");
        File.WriteAllText(Path.Combine(steam, "steamapps.tmp"), "not a record");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        File.WriteAllText(Path.Combine(steam, "steamapps/libraryfolders.vdf"), "\"libraryfolders\" { \"0\" { \"path\" " + Quote(steam) + " } \"1\" { \"path\" " + Quote(second) + " \"apps\" { \"123\" \"456\" } } }");
        File.WriteAllText(Path.Combine(second, "steamapps/appmanifest_123.acf"), "\"AppState\" { \"appid\" \"123\" \"name\" \"Example Game\" \"installdir\" \"ExampleGame\" \"StateFlags\" \"4\" }");
        File.WriteAllText(Path.Combine(second, "steamapps/appmanifest_124.acf"), "\"AppState\" { \"name\" \"Not installed\" \"installdir\" \"ExampleGame\" \"StateFlags\" \"2\" }");
        File.WriteAllText(Path.Combine(second, "steamapps/appmanifest_125.acf"), "\"AppState\" { \"name\" \"Traversal\" \"installdir\" \"../ExampleGame\" }");
        string epicRoot = Folder("EpicGame"); string epicExe = Path.Combine(epicRoot, "Epic.exe"); File.WriteAllBytes(epicExe, Pe("game"));
        File.WriteAllText(Path.Combine(epic, "game.item"), JsonSerializer.Serialize(new { DisplayName = "Epic Example", InstallLocation = epicRoot, LaunchExecutable = "Epic.exe", bIsIncompleteInstall = false }));
        File.WriteAllText(Path.Combine(epic, "incomplete.item"), JsonSerializer.Serialize(new { DisplayName = "Incomplete", InstallLocation = epicRoot, bIsIncompleteInstall = true }));
        File.WriteAllText(Path.Combine(epic, "broken.item"), "not json");
        var library = InstalledGames.Read([steam], [epic], [new("Duplicate", steamGame, "GOG"), new("Gone", Path.Combine(root, "gone"), "GOG")]);
        check(library.Games.Count == 2, "Steam multi-library and Epic installed records discovered; duplicates and missing installs excluded");
        check(library.Games.Single(g => g.Source == "Steam").Name == "Example Game", "Steam game title comes from its manifest, not directory name");
        check(library.Games.Single(g => g.Source == "Epic Games").Executable == epicExe, "Epic launch executable stays within its recorded root");
        check(library.Notices.Count >= 2, "Malformed and unsafe install records are skipped without losing valid games");
        Reject(() => VdfNode.Parse("\"bad\" { \"name\""), "Incomplete VDF is rejected");
        using (var canceled = new CancellationTokenSource())
        { canceled.Cancel(); Reject(() => InstalledGames.Read([steam], [], cancellation: canceled.Token), "Installed-game discovery respects cancellation"); }
        string local = Folder("Games/LocalTitle"); File.WriteAllBytes(Path.Combine(local, "Game.exe"), Pe("game"));
        check(InstalledGames.Read([], [], localContainers: [Path.GetDirectoryName(local)!]).Games.Single().Source == "Local folder", "Unregistered games in a common local container are discovered");
        string missing = Folder("NoReShade/Binaries/Win64"); string exe = Path.Combine(missing, "NewGame.exe"); File.WriteAllBytes(exe, Pe("game"));
        var target = new GameDiscovery().Scan(Path.GetDirectoryName(Path.GetDirectoryName(missing)!)!, includeWithoutReShade: true).Targets.Single();
        check(target.Directory == missing && target.Proxies.Length == 0, "Missing ReShade no longer prevents nested executable selection");
        check(new GameDiscovery().Scan(exe, includeWithoutReShade: true).Targets.Count == 1, "Manual executable selection works without ReShade");
        check(GraphicsApiDetection.Detect(exe) == GraphicsApi.Unknown, "Unknown graphics API is not guessed");
        foreach (var pair in new[] { ("d3d10.dll", GraphicsApi.Direct3D10), ("D3D10_1.DLL", GraphicsApi.Direct3D10), ("d3d11.dll", GraphicsApi.Direct3D11), ("d3d12.dll", GraphicsApi.Direct3D12), ("vulkan-1.dll", GraphicsApi.Unsupported) })
        {
            string path = Path.Combine(missing, "api.exe"); File.WriteAllBytes(path, ImportedPe(pair.Item1));
            check(GraphicsApiDetection.Detect(path) == pair.Item2, "PE imports identify " + pair.Item1); File.Delete(path);
        }
        check(ReShadeService.ParseRelease("<a href='/downloads/ReShade_Setup_6.8.0.exe'>standard</a><a href='/downloads/ReShade_Setup_6.8.0_Addon.exe'>full</a>").Version == "6.8.0", "Official release parsing selects the full add-on build");
        Reject(() => ReShadeService.ParseRelease("/downloads/ReShade_Setup_6.7.0_Addon.exe"), "Unsupported older ReShade is rejected");
        Reject(() => ReShadeService.ValidateRelease(new("6.8.0", "https://example.com/ReShade_Setup_6.8.0_Addon.exe")), "Third-party ReShade mirrors are rejected");
        Reject(() => ReShadeService.ExtractModule([1, 2, 3], "6.8.0"), "Corrupt ReShade downloads cannot be extracted");
        Reject(() => engine.BuildPlan(target, new()), "Missing ReShade requires a validated download before planning");
        if (officialSetup == null)
        { Console.WriteLine("NOTE: official archive integration tests skipped; pass the official add-on EXE as the second argument to enable them."); return; }
        byte[] setup = File.ReadAllBytes(officialSetup);
        string version = "6.8.0";
        string page = "/downloads/ReShade_Setup_" + version + "_Addon.exe";
        var handler = new DownloadHandler(page, setup); var service = new ReShadeService(Folder("Cache"), new HttpClient(handler));
        var acquired = service.AcquireAsync().GetAwaiter().GetResult(); var package = acquired.Package;
        check(handler.Downloads == 1 && package.Version == version && FileSafety.IsReShade(package.ModulePath), "Official archive is downloaded, extracted and version/hash-validated into the cache");
        var again = service.AcquireAsync().GetAwaiter().GetResult();
        check(handler.Downloads == 1 && again.Package.ModulePath == package.ModulePath && handler.PageChecks == 2, "Repeated acquisition checks for updates but reuses the cached binary");
        handler.Offline = true;
        check(service.AcquireAsync().GetAwaiter().GetResult().Package == package, "Offline acquisition falls back to verified cached ReShade");
        handler.Offline = false; handler.Page = "/downloads/ReShade_Setup_6.8.1_Addon.exe"; handler.FailDownload = true;
        check(service.AcquireAsync().GetAwaiter().GetResult().Status.Contains("download failed"), "Failed newer-release download retains the verified older cache with an explicit status");
        var options = new InstallOptions(ReShade: package, GraphicsApi: GraphicsApi.Direct3D11);
        Reject(() => engine.BuildPlan(target, options with { GraphicsApi = GraphicsApi.Unknown }), "ReShade setup requires a supported explicit or detected graphics API");
        var before = Snapshot(missing); var plan = engine.BuildPlan(target, options);
        check(plan.Files.Any(f => f.Component == "ReShade (dxgi.dll)" && f.Action == Operation.Install) && plan.Files.Any(f => f.Component == "ReShade.ini"), "ReShade DLL and configuration are included in the same file preview");
        check(before.SequenceEqual(Snapshot(missing)), "ReShade preview changes no game files");
        string conflict = Path.Combine(missing, "dxgi.dll"); File.WriteAllBytes(conflict, Pe("other graphics proxy"));
        Reject(() => engine.BuildPlan(target, options), "Automatic ReShade setup never overwrites an unrelated graphics proxy");
        Reject(() => engine.Install(plan), "A proxy appearing after preview invalidates ReShade installation"); File.Delete(conflict);
        var result = engine.Install(plan);
        check(FileSafety.Hash(conflict) == package.ModuleHash && result.FilesVerified == plan.Files.Count, "ReShade and Feeder install together and completion reports verified file counts");
        check(File.ReadAllText(Path.Combine(missing, "ReShade.ini")).Contains("PresetPath=.\\" + InstallerEngine.PresetName), "New ReShade config selects the installed preset and local shader/add-on paths");
        var existingTarget = new GameDiscovery().Scan(exe).Targets.Single(); string original = FileSafety.Hash(conflict);
        check(!engine.BuildPlan(existingTarget, new()).Files.Any(f => f.Component.StartsWith("ReShade (")), "Existing ReShade is preserved rather than updated implicitly");
        check(engine.Install(engine.BuildPlan(existingTarget, new())).FilesChanged == 0 && FileSafety.Hash(conflict) == original, "Repeated setup verifies without reinstalling ReShade");
        string rollback = Folder("ReShadeRollback"); string rollbackExe = Path.Combine(rollback, "Rollback.exe"); File.WriteAllBytes(rollbackExe, Pe("game"));
        File.WriteAllText(Path.Combine(rollback, InstallerEngine.PresetName), "original preset");
        var rollbackTarget = new GameDiscovery().Scan(rollbackExe, includeWithoutReShade: true).Targets.Single();
        var rollbackPlan = engine.BuildPlan(rollbackTarget, options with { ReplaceExisting = true });
        using (var locked = new FileStream(Path.Combine(rollback, InstallerEngine.PresetName), FileMode.Open, FileAccess.Read, FileShare.Read))
            Reject(() => engine.Install(rollbackPlan), "A late deployment failure triggers rollback of automatic ReShade setup");
        check(!File.Exists(Path.Combine(rollback, "dxgi.dll")) && !File.Exists(Path.Combine(rollback, "ReShade.ini")) && File.ReadAllText(Path.Combine(rollback, InstallerEngine.PresetName)) == "original preset", "Failed combined installation removes new ReShade files and preserves original preset");
        string dx10 = Folder("DX10Automatic"); string dx10Exe = Path.Combine(dx10, "ClassicGame.exe"); File.WriteAllBytes(dx10Exe, ImportedPe("d3d10.dll"));
        var dx10Target = new GameDiscovery().Scan(dx10Exe, includeWithoutReShade: true).Targets.Single();
        var dx10Options = options with { GraphicsApi = GraphicsApi.Direct3D10 };
        var dx10Plan = engine.BuildPlan(dx10Target, dx10Options);
        check(dx10Plan.CanInstall && dx10Plan.Files.Count(f => f.Component.StartsWith("ReShade (") && f.Action == Operation.Install) == 2,
            "DX10 automatic setup reviews game and host ReShade together");
        engine.Install(dx10Plan);
        check(FileSafety.Hash(Path.Combine(dx10, "dxgi.dll")) == package.ModuleHash && FileSafety.Hash(Path.Combine(dx10, "host64", "dxgi.dll")) == package.ModuleHash,
            "DX10 deploys the same verified official x64 ReShade module to game and host");
        check(File.ReadAllText(Path.Combine(dx10, "host64", "ReShade.ini")).Contains("AddonPath=.\\") && !File.ReadAllText(Path.Combine(dx10, "host64", "ReShade.ini")).Contains("PresetPath"),
            "DX10 host configuration loads only its local neural add-on, not the game shader preset");
        var installedDx10 = new GameDiscovery().Scan(dx10Exe).Targets.Single();
        check(!InstallerEngine.NeedsReShadePackage(installedDx10, dx10Options) && engine.Install(engine.BuildPlan(installedDx10,
            new(GraphicsApi: GraphicsApi.Direct3D10))).FilesChanged == 0, "DX10 repeat setup preserves both ReShade instances without another download");
        string dx10Rollback = Folder("DX10Rollback"); string dx10RollbackExe = Path.Combine(dx10Rollback, "ClassicGame.exe"); File.WriteAllBytes(dx10RollbackExe, ImportedPe("d3d10.dll"));
        File.WriteAllText(Path.Combine(dx10Rollback, InstallerEngine.PresetName), "original DX10 preset");
        var dx10RollbackPlan = engine.BuildPlan(new GameDiscovery().Scan(dx10RollbackExe, includeWithoutReShade: true).Targets.Single(), dx10Options with { ReplaceExisting = true });
        using (var locked = new FileStream(Path.Combine(dx10Rollback, InstallerEngine.PresetName), FileMode.Open, FileAccess.Read, FileShare.Read))
            Reject(() => engine.Install(dx10RollbackPlan), "A late DX10 deployment failure triggers a combined rollback");
        check(!File.Exists(Path.Combine(dx10Rollback, "dxgi.dll")) && !File.Exists(Path.Combine(dx10Rollback, "host64", "dxgi.dll")) &&
            !File.Exists(Path.Combine(dx10Rollback, "host64", "dlss5-feed-host64.exe")) && File.ReadAllText(Path.Combine(dx10Rollback, InstallerEngine.PresetName)) == "original DX10 preset",
            "DX10 rollback removes new game and host components and restores the original preset");
        byte[] module = File.ReadAllBytes(package.ModulePath); File.WriteAllBytes(package.ModulePath, Pe("tampered ReShade"));
        check(service.FindCached() == null, "Tampered cached DLLs are not reused");
        handler.Offline = true;
        Reject(() => service.AcquireAsync().GetAwaiter().GetResult(), "Offline mode refuses a corrupted cache");
        File.WriteAllBytes(package.ModulePath, module);
        check(service.FindCached() == package, "Restored valid cache still passes complete archive/hash/version verification");
    }
    private static Dictionary<string, string> Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order().ToDictionary(p => p, FileSafety.Hash);
    private static byte[] Pe(string marker)
    {
        byte[] bytes = new byte[1024]; bytes[0] = 0x4d; bytes[1] = 0x5a;
        BitConverter.GetBytes(128).CopyTo(bytes, 60); BitConverter.GetBytes(0x00004550u).CopyTo(bytes, 128);
        BitConverter.GetBytes((ushort)0x8664).CopyTo(bytes, 132); Encoding.ASCII.GetBytes(marker).CopyTo(bytes, 160); return bytes;
    }
    private static byte[] ImportedPe(string name)
    {
        byte[] b = Pe("");
        BitConverter.GetBytes((ushort)1).CopyTo(b, 134); BitConverter.GetBytes((ushort)240).CopyTo(b, 148);
        BitConverter.GetBytes((ushort)0x20b).CopyTo(b, 152); BitConverter.GetBytes(16u).CopyTo(b, 260);
        BitConverter.GetBytes(0x1000u).CopyTo(b, 272);
        BitConverter.GetBytes(0x1000u).CopyTo(b, 404); BitConverter.GetBytes(512u).CopyTo(b, 408); BitConverter.GetBytes(512u).CopyTo(b, 412);
        BitConverter.GetBytes(0x1040u).CopyTo(b, 524); Encoding.ASCII.GetBytes(name).CopyTo(b, 576); return b;
    }
    private sealed class DownloadHandler(string page, byte[] setup) : HttpMessageHandler
    {
        public string Page = page;
        public bool Offline, FailDownload;
        public int Downloads, PageChecks;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Offline) throw new HttpRequestException("offline fixture");
            bool html = request.RequestUri!.AbsolutePath == "/";
            if (html) PageChecks++; else Downloads++;
            return Task.FromResult(new HttpResponseMessage(!html && FailDownload ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { RequestMessage = request, Content = new ByteArrayContent(html ? Encoding.UTF8.GetBytes(Page) : setup) });
        }
    }
}
