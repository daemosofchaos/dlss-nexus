using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Nexus;
using Nexus.Core;

internal static partial class Program
{
    private static void CheckVersionSelectors(string output)
    {
        string work = Path.Combine(output, "version-ui"), game = Path.Combine(work, "game"); Directory.CreateDirectory(game);
        string exe = Path.Combine(game, "VersionFixture.exe");
        File.WriteAllBytes(exe, SyntheticCatalog.Pe("inert version game"));
        File.WriteAllBytes(Path.Combine(game, "dxgi.dll"), SyntheticCatalog.Pe("ReShade fixture"));
        File.WriteAllText(Path.Combine(game, "ReShade.ini"), "[GENERAL]\nAddonPath=.\\\nEffectSearchPaths=.\\reshade-shaders\\Shaders\\**\nTextureSearchPaths=.\\reshade-shaders\\Textures\\**\nPresetPath=.\\Active.ini\n");
        File.WriteAllText(Path.Combine(game, "Active.ini"), "Techniques=\n[Custom.fx]\nStrength=0.75\n");
        var catalog = SyntheticCatalog.Create(versionChoices: true); var engine = new InstallerEngine(catalog);
        var store = new GameLibraryStore(Path.Combine(work, "state", "library.json"));
        var window = new MainWindow(catalog, new ReShadeService(Path.Combine(work, "cache"), new HttpClient(new FixtureDownloads(null))),
            _ => new GameLibraryResult([], []), store, _ => throw new InvalidOperationException("Fixture launch is disabled."));
        T C<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        var addon = C<ComboBox>("AddonVersionBox"); var nr = C<ComboBox>("NeuralRuntimeVersionBox");
        string? Id(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;
        void Select(ComboBox box, string id) => box.SelectedItem = box.Items.Cast<ComboBoxItem>().Single(i => (i.Tag as string) == id);
        void Done() => Wait(() => C<ProgressBar>("BusyBar").Visibility == Visibility.Collapsed);
        InstallPlan Plan() => (InstallPlan)typeof(MainWindow).GetField("plan", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        void Scan()
        {
            Click(C<Button>("ManualModeButton")); C<TextBox>("GamePathBox").Text = exe; Click(C<Button>("ScanButton")); Done();
            Click(C<Button>("WizardNextButton")); Done();
        }
        void Finish(InstallPlan plan)
        {
            var result = engine.Install(plan); var finish = window.FinishInstallationAsync(plan, result);
            Wait(() => finish.IsCompleted); finish.GetAwaiter().GetResult();
        }
        Check(addon.Items.Count == 2 && nr.Items.Count == 2 && Id(addon) == "shortfuse-20261016" && Id(nr) == "nr-310.9.16-fp16", "Independent version selectors load catalog defaults, not hard-coded indices");
        Scan(); C<ComboBox>("RouteBox").SelectedIndex = 1; C<ComboBox>("ApiBox").SelectedIndex = 2;
        var untouched = Snapshot(game);
        foreach (var pair in new[] { ("shortfuse-20261003", "nr-310.9.16-fp16"), ("shortfuse-20261016", "nr-310.8.0"), ("shortfuse-20261016", "nr-310.9.16-fp16"), ("shortfuse-20261003", "nr-310.8.0") })
        {
            Select(addon, pair.Item1); Select(nr, pair.Item2);
            if (window.CurrentStep == WizardStep.Review) Click(C<Button>("WizardBackButton"));
            Check(window.CurrentStep == WizardStep.Components && !C<Button>("InstallButton").IsEnabled, "Version change invalidates the previous review: " + pair);
            Click(C<Button>("WizardNextButton")); Done(); var plan = Plan();
            Check(plan.Options.AddonBuild == pair.Item1 && plan.Options.NeuralRuntimeBuild == pair.Item2 && plan.CanInstall, "UI passes both independent version IDs to the plan: " + pair);
            Check(C<TextBlock>("ReviewSummaryText").Text.Contains(plan.AddonLabel) && C<TextBlock>("ReviewSummaryText").Text.Contains(plan.NeuralRuntimeLabel), "Review names the selected add-on and NR builds: " + pair);
        }
        Check(Same(untouched, Snapshot(game)), "Changing all four version combinations and reviewing changes no game files");
        Check(C<TextBlock>("NeuralRuntimeVersionNotesText").Text.Contains("not the patched FP16") && C<TextBlock>("AddonVersionNotesText").Text.Contains("subjective"), "Previous-version notes distinguish stock compatibility from image-quality preference");
        Finish(Plan());
        Check(C<TextBlock>("CompletionDetails").Text.Contains("0.2026.1003.2333") && C<TextBlock>("CompletionDetails").Text.Contains("310.8.0.0"), "Ready names the actually installed previous versions");
        Check(store.Read().Single().Snapshot!.Components.Single(c => c.FileName == "nvngx_dlssnr.dll").Hash == catalog.GetComponent("versions/nr-310.8.0/nvngx_dlssnr.dll").Sha256, "Completion inventory records the exact selected NR hash");
        Click(C<Button>("AnotherGameButton"));
        Check(Id(addon) == "shortfuse-20261016" && Id(nr) == "nr-310.9.16-fp16", "Leaving a game resets version choices to catalog defaults");
        Scan();
        Check(Id(addon) == "shortfuse-20261003" && Id(nr) == "nr-310.8.0", "Reopening a game restores recognized installed alternatives by hash");
        C<ComboBox>("ApiBox").SelectedIndex = 2; C<ComboBox>("RouteBox").SelectedIndex = 1;
        Select(nr, "nr-310.9.16-fp16"); Click(C<Button>("WizardNextButton")); Done(); var keep = Plan();
        Check(keep.Files.Single(f => f.Component == "nvngx_dlssnr.dll").Action == Operation.Keep && keep.CanInstall &&
            C<TextBlock>("ReviewSummaryText").Text.Contains("will not be installed"), "Review distinguishes a proposed NR choice from the existing runtime that will be kept");
        Click(C<Button>("PreviewButton")); Done();
        Check(Id(nr) == "nr-310.9.16-fp16" && Plan().Options.NeuralRuntimeBuild == "nr-310.9.16-fp16", "Recheck retains the proposed choice rather than restoring the old installed version");
        Finish(Plan());
        Check(C<TextBlock>("CompletionDetails").Text.Contains("Existing runtime kept") && !C<TextBlock>("CompletionDetails").Text.Contains("310.9.16.0"), "Kept-runtime completion does not falsely claim the selected newer NR was installed");
        Click(C<Button>("AnotherGameButton")); Scan();
        C<ComboBox>("RouteBox").SelectedIndex = 1; C<ComboBox>("ApiBox").SelectedIndex = 2;
        var report = store.ReportFeature(exe, "nr", ReportedFeatureState.Enabled);
        Select(nr, "nr-310.9.16-fp16"); C<CheckBox>("ReplaceBox").IsChecked = true; Click(C<CheckBox>("ReplaceBox"));
        Check(Id(addon) == "shortfuse-20261003" && Id(nr) == "nr-310.9.16-fp16", "Replacement consent does not reset the independently chosen versions");
        C<CheckBox>("ReplaceNrBox").IsChecked = true; Click(C<CheckBox>("ReplaceNrBox"));
        Click(C<Button>("WizardNextButton")); Done(); var upgrade = Plan();
        Check(upgrade.Files.Count(f => f.ChangesFile) == 1 && upgrade.Files.Single(f => f.ChangesFile).Component == "nvngx_dlssnr.dll", "Explicit NR replacement changes only the chosen runtime while retaining the older add-on");
        Finish(upgrade);
        Check(GameFeatures.Describe(store.Read().Single()).Single(f => f.Key == "nr").InGame == "Recheck needed" && report.Snapshot!.Fingerprint != store.Read().Single().Snapshot!.Fingerprint,
            "Switching versions invalidates earlier user-reported neural activity by fingerprint");
        Click(C<Button>("AnotherGameButton")); Scan();
        Check(Id(addon) == "shortfuse-20261003" && Id(nr) == "nr-310.9.16-fp16", "Reopening restores the independently installed mixed pair");
        C<ComboBox>("RouteBox").SelectedIndex = 2; C<ComboBox>("ApiBox").SelectedIndex = 3;
        Check(addon.Items.Count == 1 && Id(addon) == "generic-7.0.0-rc8-dx10" && !addon.IsEnabled && Id(nr) == "nr-310.9.16-fp16", "DX10 filters out ShortFuse alternatives while leaving NR selection independent");
        C<ComboBox>("ApiBox").SelectedIndex = 2;
        Check(addon.Items.Count == 2 && Id(addon) == "shortfuse-20261016", "Leaving DX10 restores the Standard add-on choices without a stale Generic ID");
        C<ComboBox>("ProfileBox").SelectedIndex = 2;
        Check(Id(addon) == "generic-4.7" && !addon.IsEnabled && nr.IsEnabled, "Legacy profile has its own catalog build and independent NR choices");
        C<ComboBox>("ProfileBox").SelectedIndex = 1;
        Check(Id(addon) == null && !addon.IsEnabled && C<Button>("CustomAddonButton").IsEnabled && nr.IsEnabled, "Custom add-on mode cannot silently retain a bundled version override");
        C<ComboBox>("ProfileBox").SelectedIndex = 0;
        typeof(MainWindow).GetMethod("SetBusy", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [true, "Fixture busy"]);
        Check(!addon.IsEnabled && !nr.IsEnabled, "Both version selectors are unavailable during operations");
        typeof(MainWindow).GetMethod("SetBusy", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [false, "Fixture ready"]);
        Check(addon.IsEnabled && nr.IsEnabled, "Version selectors recover their eligible state after an operation");
        // The explicit Feeder-off consent must not survive selecting another binary version.
        string active = Path.Combine(game, "Active.ini"); File.WriteAllText(active, "Techniques=DLSS5_Feed@DLSS5_Feed.fx\n[Custom.fx]\nStrength=0.75\n");
        C<ComboBox>("RouteBox").SelectedIndex = 1;
        C<CheckBox>("ReplaceBox").IsChecked = true; Click(C<CheckBox>("ReplaceBox"));
        Click(C<Button>("WizardNextButton")); Done(); Click(C<Button>("DisableFeederButton")); Done();
        Check(Plan().CanInstall && Plan().Options.DisableFeederEffects, "Version fixture reaches an explicitly resolved Feeder migration");
        Select(addon, "shortfuse-20261003");
        Check(window.CurrentStep == WizardStep.Components && !C<Button>("InstallButton").IsEnabled, "Changing the add-on version invalidates resolved migration review");
        Click(C<Button>("WizardNextButton")); Done();
        Check(!Plan().CanInstall && !Plan().Options.DisableFeederEffects && C<Button>("DisableFeederButton").Visibility == Visibility.Visible, "Add-on version change clears Feeder-deactivation consent");
        Click(C<Button>("DisableFeederButton")); Done(); Select(nr, "nr-310.8.0");
        Click(C<Button>("WizardNextButton")); Done();
        Check(!Plan().CanInstall && !Plan().Options.DisableFeederEffects, "NR version change also clears Feeder-deactivation consent");
        window.Close();
        var futureCatalog = SyntheticCatalog.Create(versionChoices: true); byte[] futureBytes = SyntheticCatalog.Pe("RenoDX DLSS controller future");
        string futurePayload = "versions/future.addon64"; using var memory = new MemoryStream(futureBytes);
        futureCatalog.Manifest.Components.Add(new() { PayloadPath = futurePayload, TargetPath = "renodx-dlss.addon64", Sha256 = FileSafety.Hash(memory), Version = "future" });
        futureCatalog.Manifest.AddonBuilds.Add(new() { Id = "future-addon", Profile = "default", Label = "Future add-on", PayloadPath = futurePayload, TargetPath = "renodx-dlss.addon64", Family = "Controller" });
        futureCatalog.Manifest.ConsumerSelections["default"].PayloadPath = futurePayload;
        byte[] futureNr = SyntheticCatalog.Pe("future NR"); using var futureNrStream = new MemoryStream(futureNr);
        futureCatalog.Manifest.Components.Add(new() { PayloadPath = "versions/future-nr.dll", TargetPath = "nvngx_dlssnr.dll", Sha256 = FileSafety.Hash(futureNrStream), Version = "future NR" });
        futureCatalog.Manifest.NeuralRuntimeBuilds.Add(new() { Id = "future-nr", Label = "Future runtime", PayloadPath = "versions/future-nr.dll" });
        futureCatalog.Manifest.DefaultNeuralRuntimeBuild = "future-nr";
        var completeFutureCatalog = new BundleCatalog(futureCatalog.Manifest, key => key == "payload/" + futurePayload ? new MemoryStream(futureBytes, false) :
            key == "payload/versions/future-nr.dll" ? new MemoryStream(futureNr, false) : futureCatalog.OpenSource(key));
        Check(completeFutureCatalog.VerifyAll() == 18, "Future UI catalog includes verifiable sources for every additional choice");
        var futureWindow = new MainWindow(completeFutureCatalog, new ReShadeService(Path.Combine(work, "future-cache"), new HttpClient(new FixtureDownloads(null))),
            _ => new GameLibraryResult([], []), new GameLibraryStore(Path.Combine(work, "future-state", "library.json")), _ => throw new InvalidOperationException("Fixture launch disabled."));
        var futureBox = (ComboBox)futureWindow.FindName("AddonVersionBox"); var futureNrBox = (ComboBox)futureWindow.FindName("NeuralRuntimeVersionBox");
        Check(futureBox.Items.Count == 3 && ((futureBox.SelectedItem as ComboBoxItem)?.Tag as string) == "future-addon" &&
            futureNrBox.Items.Count == 3 && ((futureNrBox.SelectedItem as ComboBoxItem)?.Tag as string) == "future-nr", "UI includes third catalog-only add-on/NR choices and follows new defaults without UI code changes");
        futureWindow.Close();
    }
}
