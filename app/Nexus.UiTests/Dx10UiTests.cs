using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Nexus;
using Nexus.Core;

internal static partial class Program
{
    private static void CheckDx10Setup(string output, BundleCatalog catalog, string? archive)
    {
        string game = Path.Combine(output, "dx10-game"); Directory.CreateDirectory(game);
        string exe = Path.Combine(game, "ClassicGame.exe"), host = Path.Combine(game, "host64");
        byte[] pe = new byte[1024]; SyntheticCatalog.Pe("").CopyTo(pe, 0);
        BitConverter.GetBytes((ushort)1).CopyTo(pe, 134); BitConverter.GetBytes((ushort)240).CopyTo(pe, 148);
        BitConverter.GetBytes((ushort)0x20b).CopyTo(pe, 152); BitConverter.GetBytes(16u).CopyTo(pe, 260); BitConverter.GetBytes(0x1000u).CopyTo(pe, 272);
        BitConverter.GetBytes(0x1000u).CopyTo(pe, 404); BitConverter.GetBytes(512u).CopyTo(pe, 408); BitConverter.GetBytes(512u).CopyTo(pe, 412);
        BitConverter.GetBytes(0x1100u).CopyTo(pe, 524); Encoding.ASCII.GetBytes("d3d10_1.dll").CopyTo(pe, 768); File.WriteAllBytes(exe, pe);
        File.WriteAllBytes(Path.Combine(game, "dxgi.dll"), SyntheticCatalog.Pe("ReShade game fixture"));
        File.WriteAllBytes(Path.Combine(game, "nvngx_dlssnr.dll"), SyntheticCatalog.Pe("existing game NR"));
        string gameNr = FileSafety.Hash(Path.Combine(game, "nvngx_dlssnr.dll"));
        if (archive == null)
        { Directory.CreateDirectory(host); File.WriteAllBytes(Path.Combine(host, "dxgi.dll"), SyntheticCatalog.Pe("ReShade host fixture")); }
        var downloads = new FixtureDownloads(archive == null ? null : File.ReadAllBytes(archive));
        string cache = Path.Combine(output, "dx10-cache");
        var store = new GameLibraryStore(Path.Combine(output, "dx10-state", "library.json"));
        var launches = new List<ProcessStartInfo>();
        var window = new MainWindow(catalog, new ReShadeService(cache, new HttpClient(downloads)),
            _ => new GameLibraryResult([], []), store, launches.Add);
        T C<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        var next = C<Button>("WizardNextButton"); var back = C<Button>("WizardBackButton");
        var route = C<ComboBox>("RouteBox"); var api = C<ComboBox>("ApiBox"); var grid = C<DataGrid>("PlanGrid");
        void Done() => Wait(() => C<ProgressBar>("BusyBar").Visibility == Visibility.Collapsed);
        Check(api.Items.Cast<ComboBoxItem>().Any(i => i.Tag?.ToString() == "Direct3D10" && i.Content.ToString()!.Contains("beta")), "DX10 is an explicitly labelled setup API option");
        Click(C<Button>("ManualModeButton")); C<TextBox>("GamePathBox").Text = exe; Click(next); Done();
        Check(window.CurrentStep == WizardStep.Components && C<TextBlock>("Dx10SetupText").Visibility == Visibility.Visible && api.SelectedIndex == 0,
            "DX10 auto detection shows helper guidance without forcing an API override");
        route.SelectedIndex = 1; Click(next); Done();
        Check(window.CurrentStep == WizardStep.Review && !C<Button>("InstallButton").IsEnabled && C<TextBlock>("ReviewErrorsText").Text.Contains("Feeder helper") &&
            C<Border>("ReviewBlockersCard").Visibility == Visibility.Visible && C<Button>("DisableFeederButton").Visibility == Visibility.Collapsed,
            "Native DX10 review is blocked with the correct route explanation");
        Click(back); route.SelectedIndex = 2;
        Check(C<TextBlock>("RuntimeKeptText").Visibility == Visibility.Collapsed && ((ComboBoxItem)C<ComboBox>("ProfileBox").Items[0]).Content.ToString()!.Contains("Generic"),
            "DX10 setup names its Generic consumer and does not mistake a game runtime for a host runtime");
        var before = Snapshot(game);
        Click(next); Done();
        var files = ((IEnumerable<PlannedFile>)grid.ItemsSource).ToArray();
        Check(C<Button>("InstallButton").IsEnabled && C<TextBlock>("ReviewSummaryText").Text.Contains("DirectX 10 Feeder helper (beta)"), "DX10 has its own readable review summary");
        Check(files.Any(f => f.Component == "dlss5-feed-helper.addon64") && files.Any(f => f.Component == "dlss5-feed-host64.exe") && !files.Any(f => f.Component == "dlss5-feed.addon64"),
            "DX10 review contains both helper binaries, not the normal Feeder");
        Check(files.Where(f => f.Component is "nvngx_dlssnr.dll" or "nvngx_dlss.dll" or "renodx-dlss5.addon64").All(f => FileSafety.IsInside(host, f.Destination)),
            "DX10 review keeps the consumer and runtimes inside host64");
        Check(Same(before, Snapshot(game)) && (archive == null || downloads.BinaryDownloads == 1), "DX10 review only prepares the cache and does not change the game");
        var target = (GameTarget)C<ListBox>("TargetsList").SelectedItem;
        var engine = new InstallerEngine(catalog);
        var plan = engine.BuildPlan(target, new(GraphicsApi: GraphicsApi.Direct3D10,
            ReShade: archive == null ? null : new ReShadeService(cache).FindCached()));
        var result = engine.Install(plan); var finish = window.FinishInstallationAsync(plan, result); Wait(() => finish.IsCompleted); finish.GetAwaiter().GetResult();
        Check(C<TextBlock>("CompletionStepsText").Text.Contains("helper's RenoDX Generic") && C<TextBlock>("CompletionWarningText").Text.Contains("beta"),
            "DX10 completion describes the helper panel and retains the beta caveat");
        Check(store.Read().Single().GraphicsApi == GraphicsApi.Direct3D10 && FileSafety.Hash(Path.Combine(game, "nvngx_dlssnr.dll")) == gameNr,
            "DX10 completion records the API while preserving the game's existing runtime");
        Click(C<Button>("LaunchCompletedGameButton"));
        Check(launches.Count == 1 && launches[0].FileName == exe && launches[0].WorkingDirectory == game, "DX10 Launch game requests the game executable, never the helper");
        File.WriteAllBytes(exe, SyntheticCatalog.Pe("API selected at runtime"));
        Click(C<Button>("AnotherGameButton")); Click(C<Button>("ManualModeButton")); C<TextBox>("GamePathBox").Text = exe; Click(next); Done();
        Check(((ComboBoxItem)api.SelectedItem).Tag?.ToString() == "Direct3D10" && route.SelectedIndex == 2,
            "Saved DX10 setup is restored even when static imports no longer identify the API");
        Check(C<TextBlock>("RuntimeKeptText").Visibility == Visibility.Visible, "Repeated DX10 setup identifies the preserved host runtime");
        api.SelectedItem = api.Items.Cast<ComboBoxItem>().Single(i => i.Tag?.ToString() == "Direct3D11");
        Check(C<TextBlock>("Dx10SetupText").Visibility == Visibility.Collapsed && grid.Items.Count == 0 &&
            ((ComboBoxItem)C<ComboBox>("ProfileBox").Items[0]).Content.ToString()!.Contains("ShortFuse"), "Changing to DX11 clears the helper preview and restores the normal add-on choice");
        Check(!window.IsVisible, "DX10 interface tests never show a desktop window"); window.Close();
    }
}
