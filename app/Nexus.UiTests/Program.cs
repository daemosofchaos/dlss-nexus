using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Nexus;
using Nexus.Core;

internal static partial class Program
{
    private static readonly List<string> passed = [];
    [STAThread]
    public static int Main(string[] args)
    {
        string output = Path.GetFullPath(args[0]);
        if (Directory.Exists(output)) throw new IOException("Use a new output directory.");
        Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/DLSS-Nexus;component/Themes/Cyberpunk.xaml") });
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        if (args.Contains("--desktop-smoke")) return RunDesktopSmoke(output, app);
        CheckChromeGeometry();
        string game = Path.Combine(output, "fixture-game"), binary = Path.Combine(game, "Project", "Binaries", "Win64"); Directory.CreateDirectory(binary);
        File.WriteAllBytes(Path.Combine(binary, "FixtureGame-Win64-Shipping.exe"), SyntheticCatalog.Pe("game"));
        File.WriteAllBytes(Path.Combine(binary, "dxgi.dll"), SyntheticCatalog.Pe("ReShade 6.8.1"));
        string other = Path.Combine(output, "other-game"); Directory.CreateDirectory(other);
        string otherExe = Path.Combine(other, "OtherGame.exe"); File.WriteAllBytes(otherExe, SyntheticCatalog.Pe("other game"));
        var launches = new List<ProcessStartInfo>(); Exception? launchError = null;
        bool embedded = args.Contains("--embedded");
        var catalog = embedded ? BundleCatalog.FromEmbedded(typeof(MainWindow).Assembly) : SyntheticCatalog.Create();
        string? archive = args.Skip(1).FirstOrDefault(a => a != "--embedded");
        var downloads = new FixtureDownloads(archive == null ? null : File.ReadAllBytes(archive));
        string libraryPath = Path.Combine(output, "state", "library.json");
        var store = new GameLibraryStore(libraryPath);
        var window = new MainWindow(catalog, new ReShadeService(Path.Combine(output, "cache"), new HttpClient(downloads)),
            _ => new GameLibraryResult([new("Fixture Game", game, "Steam"), new("Other Game", other, "Epic Games", otherExe)], []), store,
            start => { if (launchError != null) throw launchError; launches.Add(start); });
        T C<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        var root = (FrameworkElement)window.Content; var grid = C<DataGrid>("PlanGrid"); var install = C<Button>("InstallButton");
        var next = C<Button>("WizardNextButton"); var back = C<Button>("WizardBackButton"); var route = C<ComboBox>("RouteBox");
        var launch = C<Button>("LaunchGameButton"); var launchCompleted = C<Button>("LaunchCompletedGameButton");
        void Busy(bool value) => typeof(MainWindow).GetMethod("SetBusy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(window, [value, value ? "Fixture busy" : "Game selected"]);
        var profile = C<ComboBox>("ProfileBox");
        var replace = C<CheckBox>("ReplaceBox"); var nr = C<CheckBox>("ReplaceNrBox");
        var animations = C<CheckBox>("AnimateTransitionsBox"); animations.IsChecked = false; Click(animations);
        void Page(WizardStep step) => Check(window.CurrentStep == step && new[] { "GamePage", "ComponentsPage", "ReviewPage", "CompletePage" }.Count(n => C<Border>(n).Visibility == Visibility.Visible) == 1, step + ": one page visible");
        void Done() => Wait(() => C<ProgressBar>("BusyBar").Visibility == Visibility.Collapsed);
        void Capture(string name, double w = 1120, double h = 820, double scale = 1)
        {
            root.Width = w; root.Height = h; root.Measure(new Size(w, h)); root.Arrange(new Rect(0, 0, w, h)); root.UpdateLayout(); Pump();
            var image = new RenderTargetBitmap((int)(w * scale), (int)(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32); image.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file);
            double action = window.CurrentStep switch { WizardStep.Review => install.ActualWidth, WizardStep.Complete => C<Button>("WizardFinishButton").ActualWidth, _ => next.ActualWidth };
            Check(action >= 150 && C<TextBlock>("StatusText").ActualWidth >= 300, name + ": usable fixed footer");
            Check(C<Grid>("PageHost").ActualHeight >= 280, name + ": usable viewport");
        }
        Check(catalog.VerifyAll() >= 11, "Catalog sources verify");
        Page(WizardStep.Game); Capture("games"); Capture("games-minimum", 980, 680);
        Check(C<TextBlock>("GameSearchHint").Visibility == Visibility.Visible, "Empty search field has a visible purpose");
        CheckFocusTemplate(C<Button>("BrowseExeButton"), "Secondary button");
        CheckFocusTemplate(C<Button>("ScanButton"), "Primary button");
        Check(window.Title == "DLSS Nexus" && window.Icon is BitmapFrame, "Nexus identity and icon are embedded");
        Check(window.FindName("UseGameButton") == null, "No redundant Use selected game action");
        Check(window.FindName("UnsignedBox") == null && window.FindName("RuntimeTrustCard") == null, "Setup has no runtime acceptance control or warning card");
        Check(!next.IsEnabled && !back.IsEnabled && !install.IsEnabled, "Initial setup cannot be skipped");
        Check(!launch.IsEnabled && !launchCompleted.IsEnabled, "Launch actions require a selected or completed game");
        Click(launch); Click(launchCompleted); Check(launches.Count == 0, "Empty and hidden launch actions are inert");
        CheckLaunchValidation(output, game, Path.Combine(binary, "FixtureGame-Win64-Shipping.exe"));
        Check(!C<Expander>("AdvancedExpander").IsExpanded && !C<Expander>("FileDetailsExpander").IsExpanded, "Advanced settings and file details start collapsed");
        Check(!app.Windows.Cast<Window>().Any(w => w.IsVisible), "Harness never opens the desktop UI");
        foreach (string key in new[] { "Accent", "Violet", "Lime", "Muted", "Ink", "Warning" })
            Check(Contrast(((SolidColorBrush)app.Resources[key]).Color, ((SolidColorBrush)app.Resources["Surface"]).Color) >= 4.5, key + ": accessible text contrast");
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); Wait(() => C<Button>("RefreshGamesButton").IsEnabled && C<Button>("CheckReShadeButton").IsEnabled);
        Check(C<ListBox>("InstalledGamesList").Items.Count == 2, "Launcher games load automatically");
        C<ListBox>("InstalledGamesList").SelectedIndex = 1;
        Check(launch.IsEnabled && C<ListBox>("TargetsList").Items.Count == 0, "Detected game with an exact executable can launch without setup");
        var otherBefore = Snapshot(other); Click(launch);
        Check(launches.Count == 1 && launches[^1].FileName == otherExe && launches[^1].WorkingDirectory == other, "Detected-game launch requests the exact executable and its working directory");
        Check(store.Read().Count == 0 && Same(otherBefore, Snapshot(other)), "Launching a detected game does not inspect, install or create a library record");
        Busy(true); Check(!launch.IsEnabled, "Game launch is disabled during an operation");
        Click(launch); Click(launchCompleted); Check(launches.Count == 1, "Busy launch handlers cannot start a process"); Busy(false);
        C<ListBox>("InstalledGamesList").SelectedItem = null;
        Check(!launch.IsEnabled && C<TextBlock>("GameLaunchStatusText").Text.Length == 0, "Clearing selection disables launch and removes stale feedback");
        Click(C<Button>("ManagedModeButton")); Check(C<ListBox>("InstalledGamesList").Items.Count == 0, "Tracked view starts empty"); Click(C<Button>("LibraryModeButton"));
        C<TextBox>("GameFilterBox").Text = "steam"; Check(C<ListBox>("InstalledGamesList").Items.Count == 1, "Search filters by launcher");
        var untouched = Snapshot(game);
        C<ListBox>("InstalledGamesList").SelectedIndex = 0;
        Check(next.IsEnabled && C<ListBox>("TargetsList").Items.Count == 0, "Selecting a game enables the one Set up action");
        Check(!launch.IsEnabled && C<TextBlock>("GameLaunchHint").Text.Contains("identify"), "Unknown executable explains why launch is disabled");
        Click(launch); Check(launches.Count == 1 && C<TextBlock>("GameLaunchStatusText").Text.Contains("identify"), "Unknown-executable handler fails safely without a process");
        Click(next); Done(); Page(WizardStep.Components);
        Click(launch); Click(launchCompleted); Check(launches.Count == 1 && !launch.IsEnabled, "Setup has no hidden game-launch shortcut");
        Check(C<ListBox>("TargetsList").SelectedItem is GameTarget { Directory: var dir } && dir == binary, "Set up resolves the nested game binary");
        Check(store.Read().Single().Name == "Fixture Game" && store.Read().Single().Snapshot!.Complete, "Inspected game and snapshot are stored locally");
        Check(C<TextBlock>("LibraryStatusText").Text.Contains("1 tracked"), "Tracked count updates immediately after inspecting a game");
        Check(C<ItemsControl>("FeaturesList").Items.Count == 4 && ((IEnumerable<FeatureStatus>)C<ItemsControl>("FeaturesList").ItemsSource).All(f => f.InGame == "Not checked"), "All four features initially have unconfirmed in-game settings");
        Check(!next.IsEnabled && route.SelectedIndex == 0, "Native support is not inferred from files or selected silently");
        Check(C<TextBlock>("ComponentsTargetText").Text.Contains(binary), "Setup identifies the actual destination");
        Capture("setup"); Capture("setup-minimum", 980, 680);
        route.SelectedIndex = 1; C<ComboBox>("ApiBox").SelectedIndex = 2; Click(next); Done(); Page(WizardStep.Review);
        Check(grid.Items.Count >= 7 && ((IEnumerable<PlannedFile>)grid.ItemsSource).Any(f => f.Component.EndsWith(".fx")) &&
            ((IEnumerable<PlannedFile>)grid.ItemsSource).Any(f => f.Component == InstallerEngine.PresetName) &&
            !((IEnumerable<PlannedFile>)grid.ItemsSource).Any(f => f.Component == "dlss5-feed.addon64"), "Native review includes shaders and an inactive preset, but not Feeder");
        Check(install.IsEnabled && !C<TextBlock>("ReviewText").Text.Contains("unsigned", StringComparison.OrdinalIgnoreCase), "Valid native review is installable without runtime acceptance");
        Check(C<TextBlock>("ReviewSummaryText").Text.Contains("Native DLSS") && !C<Expander>("FileDetailsExpander").IsExpanded, "Readable review summary is primary");
        Check(C<TextBlock>("PlanPathText").Text == binary && C<TextBlock>("PlanPathText").TextTrimming == TextTrimming.CharacterEllipsis && C<TextBlock>("PlanPathText").ToolTip?.ToString() == binary,
            "Review path is one line with the complete destination available on hover");
        Capture("review-native");
        Click(back); Check(C<TextBlock>("StatusText").Text == "Review your setup", "Back from review restores the setup footer");
        route.SelectedIndex = 2; Click(next); Done(); Page(WizardStep.Review);
        Check(grid.Items.Count >= 9 && C<TextBlock>("ReviewSummaryText").Text.Contains("Feeder"), "Feeder route includes its matched components and summary");
        Capture("review-feeder"); Capture("review-minimum", 980, 680); Capture("review-125pct", 1120, 820, 1.25);
        C<Expander>("FileDetailsExpander").IsExpanded = true; Capture("review-files");
        Check(grid.ActualWidth >= 800 && grid.ActualHeight >= 200, "File detail grid fits when expanded");
        Check(((IEnumerable<PlannedFile>)grid.ItemsSource).All(f => f.Destination.StartsWith(binary, StringComparison.OrdinalIgnoreCase)), "All reviewed destinations are in the selected binary tree");
        Click(back); replace.IsChecked = true; Click(replace); Check(grid.Items.Count == 0 && !install.IsEnabled, "Changed replacement choice invalidates stale preview");
        profile.SelectedIndex = 1; Check(C<Button>("CustomAddonButton").IsEnabled, "Custom build is available under Advanced");
        Click(back); Check(C<TextBlock>("StatusText").Text == "Game selected", "Games footer describes the current selection rather than old review errors");
        Click(next); Done(); Check(profile.SelectedIndex == 1 && route.SelectedIndex == 2, "Back and Set up preserve choices for the same game");
        profile.SelectedIndex = 0;
        replace.IsChecked = true; Click(replace); Check(nr.IsEnabled, "Runtime replacement requires add-on replacement approval");
        nr.IsChecked = true; replace.IsChecked = false; Click(replace); Check(!nr.IsEnabled && nr.IsChecked == false, "Removing replacement consent clears runtime permission");
        Click(next); Done(); Page(WizardStep.Review); Check(install.IsEnabled, "Valid review enables Install");
        Check(Same(untouched, Snapshot(game)), "Inspection, navigation and preview modify no game files");
        animations.IsChecked = true; window.AnimationPolicy = () => true; Click(back);
        Check(window.LastTransitionAnimated && C<Canvas>("TransitionLayer").Visibility == Visibility.Visible, "Step transition starts the scan-line effect");
        Wait(() => C<Canvas>("TransitionLayer").Visibility == Visibility.Collapsed);
        window.AnimationPolicy = () => false; Click(back); Check(!window.LastTransitionAnimated, "Reduced-motion policy bypasses effects");
        animations.IsChecked = false; Click(animations); window.AnimationPolicy = () => true; Click(next); Done(); Check(!window.LastTransitionAnimated, "User animation toggle is respected");
        Click(install); Check(window.CurrentStep == WizardStep.Components && Same(untouched, Snapshot(game)), "Hidden Install cannot run outside review");
        Click(next); Done();
        var engine = new InstallerEngine(catalog); var target = (GameTarget)C<ListBox>("TargetsList").SelectedItem;
        var plan = engine.BuildPlan(target, new()); var result = engine.Install(plan);
        var finish = window.FinishInstallationAsync(plan, result); Wait(() => finish.IsCompleted); finish.GetAwaiter().GetResult(); Page(WizardStep.Complete);
        Check(C<TextBlock>("CompletionTitle").Text == "Installed" && C<TextBlock>("CompletionDetails").Text.Contains(result.FilesVerified + " files verified"), "Completion reports verified fixture installation");
        Check(C<TextBlock>("StatusText").Text == "Installed", "Ready footer describes completion rather than game selection");
        Check(C<TextBlock>("CompletionStepsText").Text.Contains("Generic Depth") && C<Button>("OpenBackupButton").IsEnabled, "Feeder completion has its in-game steps and backup access");
        Check(store.Read().Single().Route == SetupRoute.Feeder && store.Read().Single().InstalledAt != null, "Successful setup records the selected route");
        Check(!install.IsEnabled && install.Visibility == Visibility.Collapsed && !back.IsEnabled, "Completion cannot reinstall accidentally");
        Check(launches.Count == 1 && launchCompleted.IsEnabled, "Installation exposes Launch game but never starts it automatically");
        var installedBeforeLaunch = Snapshot(game); string libraryBeforeLaunch = File.ReadAllText(libraryPath);
        Click(launchCompleted);
        Check(launches.Count == 2 && launches[^1].FileName == target.Executable && launches[^1].WorkingDirectory == binary, "Ready launch uses the completed nested game executable");
        Check(C<TextBlock>("CompletionLaunchStatusText").Text.StartsWith("Launch requested") && window.CurrentStep == WizardStep.Complete,
            "Ready launch reports a request and keeps Nexus on completion");
        Check(Same(installedBeforeLaunch, Snapshot(game)) && libraryBeforeLaunch == File.ReadAllText(libraryPath), "Ready launch preserves files, install timestamp and feature reports");
        launchError = new System.ComponentModel.Win32Exception(5, "Fixture access denied"); Click(launchCompleted); launchError = null;
        Check(launches.Count == 2 && C<TextBlock>("CompletionLaunchStatusText").Text.Contains("Fixture access denied") && C<TextBlock>("CompletionTitle").Text == "Installed",
            "Launch failure is shown inline without relabeling a successful installation");
        File.Move(target.Executable, target.Executable + ".held"); Click(launchCompleted);
        Check(launches.Count == 2 && !launchCompleted.IsEnabled && C<TextBlock>("CompletionLaunchStatusText").Text.Contains("no longer available"), "Ready revalidates a deleted executable at click time");
        File.Move(target.Executable + ".held", target.Executable); window.DisplayCompletion(plan, result);
        Check(launchCompleted.IsEnabled && launches.Count == 2, "Restored completed executable can be launched without automatic retry");
        Busy(true); Click(launchCompleted); Check(!launchCompleted.IsEnabled && launches.Count == 2, "Ready cannot launch while an operation is busy"); Busy(false);
        Capture("installed"); Capture("installed-minimum", 980, 680);
        CheckLaunchLayout(launchCompleted, C<Grid>("PageHost"), "Ready launch at minimum size");
        Click(C<Button>("AnotherGameButton")); Page(WizardStep.Game);
        Click(launchCompleted); Check(!launchCompleted.IsEnabled && launches.Count == 2, "Back to games clears the completed launch target");
        Check(!next.IsEnabled && !replace.IsChecked!.Value && !nr.IsChecked!.Value, "Back to games clears replacement choices and targets");
        Click(C<Button>("ManagedModeButton")); Check(C<ListBox>("InstalledGamesList").Items.Count == 1, "Tracked view contains the configured game");
        C<ListBox>("InstalledGamesList").SelectedIndex = 0; Check(C<ItemsControl>("VersionsList").Items.Count >= 4, "Saved game shows installed versions without reinstalling");
        Check(launch.IsEnabled && launch.ToolTip?.ToString() == target.Executable, "Tracked view offers the exact saved game executable");
        installedBeforeLaunch = Snapshot(game); libraryBeforeLaunch = File.ReadAllText(libraryPath); Click(launch);
        Check(launches.Count == 3 && launches[^1].FileName == target.Executable && Same(installedBeforeLaunch, Snapshot(game)) && libraryBeforeLaunch == File.ReadAllText(libraryPath),
            "Tracked launch does not reinstall or change the library");
        launchError = new System.ComponentModel.Win32Exception(5, "Fixture tracking launch denied"); Click(launch); launchError = null;
        Check(launches.Count == 3 && C<TextBlock>("GameLaunchStatusText").Text.Contains("Fixture tracking launch denied") && window.CurrentStep == WizardStep.Game,
            "Tracked launch failure appears beside its action without navigating to setup");
        File.Move(target.Executable, target.Executable + ".held"); Click(launch);
        Check(launches.Count == 3 && !launch.IsEnabled && C<TextBlock>("GameLaunchStatusText").Text.Contains("no longer available"), "Tracked launch revalidates stale saved executable paths");
        File.Move(target.Executable + ".held", target.Executable); Busy(false);
        Check(launch.IsEnabled && Same(installedBeforeLaunch, Snapshot(game)) && libraryBeforeLaunch == File.ReadAllText(libraryPath), "Launch failures leave the tracked game and reports unchanged");
        Check(launches.All(s => s.UseShellExecute && s.Arguments.Length == 0 && s.ArgumentList.Count == 0 && s.Verb.Length == 0), "Launch requests contain no shell command, arguments or elevation verb");
        Check(C<Expander>("FeatureReportExpander").IsEnabled, "Existing tracked game can record settings from the library");
        Check(C<TextBlock>("RuntimeKeptText").Visibility == Visibility.Visible, "Tracked setup identifies the runtime that will be kept");
        replace.IsChecked = true; Click(replace); nr.IsChecked = true; Click(nr);
        Check(C<TextBlock>("RuntimeKeptText").Visibility == Visibility.Collapsed, "Runtime replacement clears the kept-runtime hint");
        replace.IsChecked = false; Click(replace);
        C<ComboBox>("ReportedFeatureBox").SelectedIndex = 3; C<ComboBox>("ReportedStateBox").SelectedIndex = 1;
        Click(C<Button>("SaveFeatureButton")); Done();
        Check(((IEnumerable<FeatureStatus>)C<ItemsControl>("FeaturesList").ItemsSource).Single(f => f.Key == "nr").InGame == "Enabled · reported", "Library displays explicitly user-reported neural activity");
        Check(store.Read().Single().FeatureReports["nr"].State == ReportedFeatureState.Enabled, "Feature reports persist across library refreshes");
        Capture("tracked"); Capture("tracked-minimum", 980, 680);
        CheckLaunchLayout(launch, C<Grid>("PageHost"), "Tracked launch at minimum size");
        File.WriteAllBytes(Path.Combine(binary, "nvngx_dlssnr.dll"), SyntheticCatalog.Pe("changed NR")); Click(C<Button>("CheckStatusButton")); Done();
        Check(((IEnumerable<FeatureStatus>)C<ItemsControl>("FeaturesList").ItemsSource).Single(f => f.Key == "nr").InGame == "Recheck needed", "Refresh marks old feature reports stale after a file change");
        Click(C<Button>("ManualModeButton")); Check(C<Grid>("ManualPanel").Visibility == Visibility.Visible && C<Grid>("LibraryPanel").Visibility == Visibility.Collapsed, "Add game is the explicit manual flow");
        Check(C<TextBlock>("InventoryStatusText").Text == "Select a game to see its DLSS setup.", "Empty details do not suggest disabled setup or status actions");
        Check(!launch.IsEnabled && !launchCompleted.IsEnabled, "Empty Add game has no stale launch target");
        C<TextBox>("GamePathBox").Text = target.Executable; Click(next); Done(); Page(WizardStep.Components); Check(route.SelectedIndex == 2, "Tracked route is restored for the same manual executable");
        var originalInstalledAt = store.Read().Single().InstalledAt;
        var verify = engine.BuildPlan(target, new()); var verified = engine.Install(verify);
        finish = window.FinishInstallationAsync(verify, verified); Wait(() => finish.IsCompleted); finish.GetAwaiter().GetResult();
        Check(store.Read().Single().InstalledAt == originalInstalledAt && C<TextBlock>("CompletionTitle").Text == "Verified", "No-change verification keeps the original installation timestamp");
        Check(launchCompleted.IsEnabled && launches.Count == 3, "Verified completion also offers explicit launch without automatically starting a game");
        if (archive != null)
        {
            Click(C<Button>("AnotherGameButton")); Click(C<Button>("ManualModeButton"));
            string missing = Path.Combine(output, "missing-reshade"); Directory.CreateDirectory(missing);
            File.WriteAllBytes(Path.Combine(missing, "Game.exe"), SyntheticCatalog.Pe("game")); var before = Snapshot(missing);
            C<TextBox>("GamePathBox").Text = missing; Click(next); Done(); route.SelectedIndex = 1; C<ComboBox>("ApiBox").SelectedIndex = 2;
            Click(next); Done(); Page(WizardStep.Review);
            Check(install.IsEnabled && ((IEnumerable<PlannedFile>)grid.ItemsSource).Any(f => f.Component.StartsWith("ReShade (")), "Missing ReShade joins the native setup transaction");
            Check(((IEnumerable<PlannedFile>)grid.ItemsSource).Any(f => f.Component == InstallerEngine.PresetName) &&
                ((IEnumerable<PlannedFile>)grid.ItemsSource).Any(f => f.Component.EndsWith(".fx")), "Automatic native ReShade setup reviews the shader files and Nexus preset");
            Check(Same(before, Snapshot(missing)) && downloads.BinaryDownloads == 1, "Official package preparation modifies cache only");
            Click(C<Button>("PreviewButton")); Done(); Check(downloads.BinaryDownloads == 1, "Repeat review reuses the cache");
            var missingTarget = (GameTarget)C<ListBox>("TargetsList").SelectedItem;
            var native = engine.BuildPlan(missingTarget, new(Route: SetupRoute.NativeDlss, NativeDlssConfirmed: true, GraphicsApi: GraphicsApi.Direct3D12,
                ReShade: new ReShadeService(Path.Combine(output, "cache")).FindCached()));
            var nativeResult = engine.Install(native); finish = window.FinishInstallationAsync(native, nativeResult); Wait(() => finish.IsCompleted); finish.GetAwaiter().GetResult();
            Check(Directory.Exists(Path.Combine(missing, "reshade-shaders", "Shaders")) &&
                File.Exists(Path.Combine(missing, "reshade-shaders", "Shaders", "DLSS5_Feed.fx")) &&
                File.Exists(Path.Combine(missing, InstallerEngine.PresetName)), "Automatic native setup emits the shader folder, FX file and Nexus INI beside the game executable");
            Check(File.ReadAllText(Path.Combine(missing, "ReShade.ini")).Contains("PresetPath=.\\" + InstallerEngine.PresetName) &&
                GameConfiguration.Read(missingTarget, [missing]).FeederEffectEnabled == false &&
                !File.Exists(Path.Combine(missing, "dlss5-feed.addon64")), "Automatic native setup selects an inactive preset without enabling or installing Feeder");
            Check(store.Read().Single(g => g.Executable == missingTarget.Executable).Snapshot!.Has("dxgi.dll"), "Completion inventory includes newly installed ReShade without requiring another scan");
            Check(C<TextBlock>("LibraryStatusText").Text.StartsWith("3 games · 2 tracked"), "Manual games and newly tracked games are reflected in library totals");
            Check(!C<TextBlock>("CompletionStepsText").Text.Contains("Generic Depth") && C<TextBlock>("CompletionStepsText").Text.Contains("enable DLSS"), "Native completion has its own in-game steps");
            Click(launchCompleted); Check(launches.Count == 4 && launches[^1].FileName == missingTarget.Executable, "Native completion launches its own game, not the previous completion target");
            Capture("installed-native");
        }
        File.WriteAllText(Path.Combine(output, "state", "library.json"), "invalid json");
        var completionPlan = archive == null ? verify : engine.BuildPlan(new GameDiscovery().Scan(((GameTarget)C<ListBox>("TargetsList").SelectedItem).Root).Targets.Single(),
            new(Route: SetupRoute.NativeDlss, NativeDlssConfirmed: true, GraphicsApi: GraphicsApi.Direct3D12));
        var completionResult = engine.Install(completionPlan);
        finish = window.FinishInstallationAsync(completionPlan, completionResult); Wait(() => finish.IsCompleted); finish.GetAwaiter().GetResult();
        Check(C<TextBlock>("CompletionTitle").Text == "Verified" && window.CurrentStep == WizardStep.Complete, "Library failure does not relabel a verified deployment as failed");
        Check(C<TextBlock>("CompletionWarningText").Text.Contains("Could not save") && File.ReadAllText(Path.Combine(output, "state", "library.json")) == "invalid json",
            "Completion shows the library warning and keeps the unreadable state file");
        Check(!C<Expander>("FeatureReportExpander").IsEnabled, "Reports remain disabled after a library-save failure");
        Check(launchCompleted.IsEnabled, "Launch remains available from the completed target after a library-save failure");
        Click(launchCompleted); Check(launches[^1].FileName == completionPlan.Target.Executable && File.ReadAllText(libraryPath) == "invalid json", "Completion launch does not repair or overwrite an unreadable library");
        CheckDx10Setup(output, catalog, archive);
        CheckReviewFeedback(output, catalog);
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { assertions = passed.Count, passed, embedded, scope = "Offscreen UX, isolated local inventory and fixture installation. No desktop, live games or neural evaluation." }, JsonSettings.Options));
        Console.WriteLine($"{passed.Count} Nexus UI assertions passed. {output}"); app.Shutdown(); return 0;
    }
    private static void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException("FAIL: " + name); passed.Add(name); Console.WriteLine("PASS: " + name); }
    private static void CheckFocusTemplate(Button button, string label)
    {
        button.ApplyTemplate();
        var ring = button.Template.FindName("FocusRing", button) as Border;
        var triggers = button.Template.Triggers.OfType<Trigger>().ToArray();
        var focus = triggers.Single(t => t.Property == UIElement.IsKeyboardFocusedProperty && Equals(t.Value, true));
        Check(ring is { IsHitTestVisible: false, Visibility: Visibility.Collapsed, Child: null }
            && ring.BorderBrush is SolidColorBrush { Color: var color } && color == Colors.White
            && ring.BorderThickness == new Thickness(2)
            && focus.Setters.OfType<Setter>().Single() is { TargetName: "FocusRing", Property: var property, Value: Visibility.Visible }
            && property == UIElement.VisibilityProperty
            && triggers.Where(t => t.Property == UIElement.IsMouseOverProperty).SelectMany(t => t.Setters.OfType<Setter>()).All(s => s.TargetName != "FocusRing"),
            label + " template has a separate white keyboard-focus outline without changing content layout");
    }
    private static void CheckLaunchValidation(string output, string game, string executable)
    {
        var request = MainWindow.BuildGameLaunch(game, executable);
        Check(request.FileName == executable && request.WorkingDirectory == Path.GetDirectoryName(executable) && request.UseShellExecute,
            "Launch validator preserves the exact nested executable and binary working directory");
        string spaces = Path.Combine(output, "Game launch with spaces"); Directory.CreateDirectory(spaces);
        string spacedExe = Path.Combine(spaces, "Game with spaces.exe"); File.WriteAllBytes(spacedExe, SyntheticCatalog.Pe("game"));
        request = MainWindow.BuildGameLaunch(spaces, spacedExe);
        Check(request.FileName == spacedExe && request.Arguments.Length == 0 && request.ArgumentList.Count == 0, "Paths containing spaces are not converted into a shell command");
        string script = Path.Combine(game, "Launch.cmd"); File.WriteAllBytes(script, SyntheticCatalog.Pe("not a command"));
        string invalid = Path.Combine(game, "NotGame.exe"); File.WriteAllBytes(invalid, [1, 2, 3]);
        string x86 = Path.Combine(game, "Game32.exe"); var bytes = SyntheticCatalog.Pe("game");
        int pe = BitConverter.ToInt32(bytes, 0x3c); bytes[pe + 4] = 0x4c; bytes[pe + 5] = 0x01; File.WriteAllBytes(x86, bytes);
        foreach (var test in new (string Root, string? Exe, string Label)[]
        {
            (game, null, "Unknown executable rejected"),
            (game, "Game.exe", "Relative executable rejected"),
            ("relative", executable, "Relative game root rejected"),
            (game, "steam://rungameid/123", "Arbitrary launch URI rejected"),
            (game, spacedExe, "Executable outside the selected game rejected"),
            (Path.GetPathRoot(game)!, executable, "Drive root cannot be a launch target"),
            (game, script, "Command script cannot be a launch target"),
            (game, Path.Combine(game, "Missing.exe"), "Missing executable rejected"),
            (game, invalid, "Invalid PE executable rejected"),
            (game, x86, "Unsupported 32-bit executable rejected")
        })
        {
            bool rejected = false;
            try { MainWindow.BuildGameLaunch(test.Root, test.Exe); }
            catch (Exception) { rejected = true; }
            Check(rejected, test.Label);
        }
        File.Delete(script); File.Delete(invalid); File.Delete(x86);
    }
    private static void CheckLaunchLayout(Button button, FrameworkElement viewport, string label)
    {
        var position = button.TransformToAncestor(viewport).Transform(new Point());
        Check(button.ActualWidth >= 100 && button.ActualHeight >= 38 && position.X >= 0 && position.Y >= 0 &&
            position.X + button.ActualWidth <= viewport.ActualWidth && position.Y + button.ActualHeight <= viewport.ActualHeight,
            label + ": action visible in the viewport");
    }
    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Click(CheckBox b) => b.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Wait(Func<bool> done) { var time = Stopwatch.StartNew(); while (!done() && time.Elapsed < TimeSpan.FromSeconds(60)) { Pump(); Thread.Sleep(5); } Check(done(), "Async operation completed"); Pump(); }
    private static Dictionary<string, string> Snapshot(string path) => Directory.GetFiles(path, "*", SearchOption.AllDirectories).ToDictionary(f => f, FileSafety.Hash);
    private static bool Same(Dictionary<string, string> a, Dictionary<string, string> b) => a.Count == b.Count && a.All(p => b.GetValueOrDefault(p.Key) == p.Value);
    private static double Contrast(Color a, Color b)
    {
        static double Linear(byte v) { double c = v / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
        static double L(Color c) => .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
        double x = L(a), y = L(b); return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
    }
    private sealed class FixtureDownloads(byte[]? setup) : HttpMessageHandler
    {
        public int BinaryDownloads;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (setup == null) throw new HttpRequestException("offline fixture"); bool page = request.RequestUri!.AbsolutePath == "/"; if (!page) BinaryDownloads++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(page ? Encoding.UTF8.GetBytes("/downloads/ReShade_Setup_6.8.0_Addon.exe") : setup) });
        }
    }
}
