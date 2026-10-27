using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Nexus.Core;
using Microsoft.Win32;

namespace Nexus;

public partial class MainWindow : Window
{
    private readonly InstallerEngine engine;
    private readonly ReShadeService reshade;
    private readonly GameInventory inventory;
    private readonly GameLibraryStore libraryStore;
    private readonly Func<CancellationToken, GameLibraryResult> libraryDiscovery;
    private readonly Action<ProcessStartInfo> startGameProcess;
    private readonly CancellationTokenSource backgroundCancellation = new();
    private IReadOnlyList<InstalledGame> installedGames = [];
    private IReadOnlyList<TrackedGame> trackedGames = [];
    private IReadOnlyList<string> libraryNotices = [];
    private TrackedGame? currentGame;
    private InstallPlan? plan;
    private GameTarget? completedTarget;
    private bool busy, initialized, libraryBusy, updateBusy, startupStarted, applyingFilter, trackedOnly, disableFeederEffects;
    private string? experimentalAddon, backupDirectory, completionDirectory;
    private CancellationTokenSource? cancellation;
    public MainWindow(BundleCatalog catalog, ReShadeService? reShadeService = null,
        Func<CancellationToken, GameLibraryResult>? discoverGames = null, GameLibraryStore? gameLibraryStore = null,
        Action<ProcessStartInfo>? launchGame = null)
    {
        engine = new(catalog); inventory = new(catalog); versionCatalog = catalog;
        reshade = reShadeService ?? new ReShadeService();
        libraryStore = gameLibraryStore ?? new GameLibraryStore();
        libraryDiscovery = discoverGames ?? InstalledGames.Discover;
        startGameProcess = launchGame ?? (start => { using var process = Process.Start(start); });
        InitializeComponent(); initialized = true; RefreshVersionChoices(reset: true);
        string version = typeof(MainWindow).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion.Split('+')[0];
        AppVersionText.Text = version + " / WINDOWS x64";
        SetGameMode(false); SetWizardStep(WizardStep.Game, animate: false);
    }
    private string Profile => (ProfileBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "default";
    private bool HasRoute => RouteBox.SelectedIndex > 0;
    private SetupRoute SelectedRoute => RouteBox.SelectedIndex == 1 ? SetupRoute.NativeDlss : SetupRoute.Feeder;
    private GraphicsApi SelectedApi => Enum.TryParse<GraphicsApi>((ApiBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var api) && api != GraphicsApi.Unknown
        ? api : (TargetsList.SelectedItem as GameTarget)?.DetectedApi ?? GraphicsApi.Unknown;
    private InstallOptions Options => new(Profile: Profile, ReplaceExisting: ReplaceBox.IsChecked == true,
        ReplaceNeuralRuntime: ReplaceNrBox.IsChecked == true, ExperimentalAddon: experimentalAddon, GraphicsApi: SelectedApi,
        Route: SelectedRoute, NativeDlssConfirmed: SelectedRoute == SetupRoute.NativeDlss, DisableFeederEffects: disableFeederEffects,
        AddonBuild: SelectedAddonBuild, NeuralRuntimeBuild: SelectedNeuralRuntimeBuild);
    private GameListItem? SelectedLibraryGame => InstalledGamesList.SelectedItem as GameListItem;

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (startupStarted) return; startupStarted = true;
        await Task.WhenAll(RefreshLibraryAsync(), CheckReShadeAsync());
    }
    private async void RefreshGames_Click(object sender, RoutedEventArgs e) => await RefreshLibraryAsync();
    private async Task RefreshLibraryAsync()
    {
        if (libraryBusy || busy) return;
        libraryBusy = true; RefreshGamesButton.IsEnabled = false; LibraryStatusText.Text = "Loading games…";
        try
        {
            var library = await Task.Run(() => libraryDiscovery(backgroundCancellation.Token)); installedGames = library.Games;
            libraryNotices = library.Notices;
            ReadTrackedGames(); ApplyGameFilter();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LibraryStatusText.Text = "Could not load games. Use Add game."; LogBox.AppendText(ex.Message + Environment.NewLine); }
        finally { libraryBusy = false; RefreshGamesButton.IsEnabled = !busy; UpdateWizardNavigation(); }
    }
    private void ReadTrackedGames()
    {
        try { trackedGames = libraryStore.Read(); LibraryErrorText.Text = ""; }
        catch (Exception ex) { LibraryErrorText.Text = "Could not read the saved library. The original file has been kept."; LogBox.AppendText(ex.Message + Environment.NewLine); }
    }
    private void ApplyGameFilter()
    {
        string? selectedRoot = SelectedLibraryGame?.Root; string filter = GameFilterBox.Text.Trim();
        var manual = trackedGames.Where(t => !installedGames.Any(g => g.Root.Equals(t.Root, StringComparison.OrdinalIgnoreCase)))
            .Select(t => new InstalledGame(t.Name, t.Root, t.Source, t.Executable));
        var rows = installedGames.Concat(manual).Select(g =>
        {
            var tracked = trackedGames.Where(t => t.Root.Equals(g.Root, StringComparison.OrdinalIgnoreCase)).OrderByDescending(t => t.Snapshot?.CheckedAt).FirstOrDefault();
            return new GameListItem(g.Name, g.Root, g.Source, tracked?.Status ?? "Not checked", tracked?.Executable ?? g.Executable);
        }).Where(g => (!trackedOnly || trackedGames.Any(t => t.Root.Equals(g.Root, StringComparison.OrdinalIgnoreCase))) &&
            (filter.Length == 0 || g.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || g.Source.Contains(filter, StringComparison.OrdinalIgnoreCase) || g.Root.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(g => g.Name).ToArray();
        applyingFilter = true;
        try { InstalledGamesList.ItemsSource = rows; InstalledGamesList.SelectedItem = rows.FirstOrDefault(g => g.Root.Equals(selectedRoot, StringComparison.OrdinalIgnoreCase)); }
        finally { applyingFilter = false; }
        if (selectedRoot != null && SelectedLibraryGame == null) ClearTarget();
        int total = installedGames.Concat(manual).Select(g => g.Root).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int trackedCount = trackedGames.Select(g => g.Root).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        LibraryStatusText.Text = $"{total} games · {trackedCount} tracked" + (libraryNotices.Count == 0 ? "" : $" · {libraryNotices.Count} records unavailable");
        UpdateWizardNavigation();
    }
    private void GameFilterChanged(object sender, TextChangedEventArgs e) { if (initialized) ApplyGameFilter(); }
    private void LibrarySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || applyingFilter) return;
        ClearTarget();
        var saved = trackedGames.FirstOrDefault(g => g.Executable.Equals(SelectedLibraryGame?.Executable, StringComparison.OrdinalIgnoreCase));
        ShowInventory(saved, SelectedLibraryGame?.Name); UpdateWizardNavigation();
        if (step == WizardStep.Game) StatusText.Text = SelectedLibraryGame == null ? "Select a game" : "Game selected";
    }
    private void ClearTarget()
    {
        GameLaunchStatusText.Text = "";
        currentGame = null; TargetsList.ItemsSource = null; ResetSetupOptions(); InvalidatePlan();
    }
    private async void CheckStatus_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (SelectedLibraryGame is { } game) await ScanPathAsync(game.Root, game.Executable);
        else if (TargetsList.SelectedItem is GameTarget target)
        { SetBusy(true, "Checking installed versions…"); await InspectTargetAsync(target); SetBusy(false, "Status updated"); }
    }
    private async void CheckReShade_Click(object sender, RoutedEventArgs e) => await CheckReShadeAsync();
    private async Task CheckReShadeAsync()
    {
        if (updateBusy || busy) return;
        updateBusy = true; CheckReShadeButton.IsEnabled = false; ReShadeUpdateText.Text = "Checking ReShade…";
        try { ReShadeUpdateText.Text = (await reshade.CheckLatestAsync(backgroundCancellation.Token)).Status; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReShadeUpdateText.Text = "Update check unavailable."; LogBox.AppendText(ex.Message + Environment.NewLine); }
        finally { updateBusy = false; CheckReShadeButton.IsEnabled = !busy; }
    }
    private void ApiChanged(object sender, SelectionChangedEventArgs e) { disableFeederEffects = false; UpdateSetupHints(); InvalidatePlan(); }
    private void RouteChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized) return;
        disableFeederEffects = false;
        UpdateSetupHints();
        InvalidatePlan();
    }
    private void InvalidatePlan(bool keepPage = false)
    {
        if (!initialized) return;
        wizardComplete = false; CompletionCard.Visibility = Visibility.Collapsed; completionDirectory = null;
        completedTarget = null; CompletionLaunchStatusText.Text = "";
        plan = null; PlanGrid.ItemsSource = null; InstallButton.IsEnabled = false; InstallButton.Content = "Install";
        PlanText.Text = ""; PlanPathText.Text = ""; ReviewText.Text = ""; ReviewSummaryText.Text = "";
        ReviewErrorsText.Text = ""; ReviewBlockersCard.Visibility = FeederResolutionPanel.Visibility = DisableFeederButton.Visibility = Visibility.Collapsed;
        InstallButton.ToolTip = null;
        if (!keepPage && step is WizardStep.Review or WizardStep.Complete) SetWizardStep(HasTarget ? WizardStep.Components : WizardStep.Game);
        UpdateWizardNavigation();
    }
    private void InputsChanged(object sender, TextChangedEventArgs e)
    {
        if (!initialized) return;
        ClearTarget(); ShowInventory(null, null); SetWizardStep(WizardStep.Game);
    }
    private void TargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized) return;
        disableFeederEffects = false;
        backupDirectory = null; OpenBackupButton.IsEnabled = false; ApiBox.SelectedIndex = 0;
        if (TargetsList.SelectedItem is GameTarget target)
        {
            ComponentsTargetText.Text = (SelectedLibraryGame?.Name ?? target.DisplayName) + " · " + target.Directory;
            ComponentsTargetText.ToolTip = target.Executable;
            ReShadeStateText.Text = target.Proxies.Length != 0 ? "ReShade installed" : "ReShade will be installed";
            currentGame = trackedGames.FirstOrDefault(g => g.Executable.Equals(target.Executable, StringComparison.OrdinalIgnoreCase));
            if (currentGame?.GraphicsApi is { } savedApi && savedApi != GraphicsApi.Unknown)
                ApiBox.SelectedItem = ApiBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Tag?.ToString() == savedApi.ToString()) ?? ApiBox.Items[0];
            RouteBox.SelectedIndex = currentGame?.Route == SetupRoute.NativeDlss ? 1 : currentGame?.Route == SetupRoute.Feeder ? 2 : 0;
            RefreshVersionChoices(reset: true);
            if (currentGame?.Snapshot is { } snapshot) RestoreInstalledVersionChoices(target, snapshot);
            ShowInventory(currentGame, SelectedLibraryGame?.Name ?? target.DisplayName);
        }
        else currentGame = null;
        InvalidatePlan();
    }
    private void ResetSetupOptions()
    {
        disableFeederEffects = false;
        RouteBox.SelectedIndex = 0; ProfileBox.SelectedIndex = 0; ApiBox.SelectedIndex = 0;
        ReplaceBox.IsChecked = ReplaceNrBox.IsChecked = false;
        ReplaceNrBox.IsEnabled = false; experimentalAddon = null; CustomAddonButton.IsEnabled = false;
        CustomAddonText.Text = "No custom add-on selected."; backupDirectory = null; OpenBackupButton.IsEnabled = false;
        RefreshVersionChoices(reset: true);
    }
    private void OptionsChanged(object sender, RoutedEventArgs e)
    {
        disableFeederEffects = false;
        ReplaceNrBox.IsEnabled = ReplaceBox.IsChecked == true;
        if (!ReplaceNrBox.IsEnabled) ReplaceNrBox.IsChecked = false;
        UpdateSetupHints();
        InvalidatePlan();
    }
    private void UpdateSetupHints()
    {
        if (!initialized) return;
        RefreshVersionChoices();
        bool keepRuntime = false;
        var runtimeTarget = TargetsList.SelectedItem as GameTarget;
        var runtimeOptions = Options;
        if (runtimeTarget == null && currentGame is { } saved)
        {
            runtimeTarget = new(saved.Root, Path.GetDirectoryName(saved.Executable)!, saved.Executable, []);
            runtimeOptions = runtimeOptions with { GraphicsApi = saved.GraphicsApi ?? GraphicsApi.Unknown, Route = saved.Route ?? SelectedRoute };
        }
        if (ReplaceNrBox.IsChecked != true && runtimeTarget != null)
        {
            try
            {
                string destination = Path.Combine(InstallerEngine.NeuralRuntimeDirectory(runtimeTarget, runtimeOptions), "nvngx_dlssnr.dll");
                keepRuntime = currentGame?.Snapshot?.Components.Any(c => c.Status == "Installed" && c.Path.Equals(destination, StringComparison.OrdinalIgnoreCase)) == true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { }
        }
        RuntimeKeptText.Visibility = keepRuntime ? Visibility.Visible : Visibility.Collapsed;
        RuntimeKeptText.Text = "Your existing neural runtime will be kept unless you enable replacement. Choosing a different version alone does not replace it.";
        ApiRequiredText.Visibility = HasTarget && SelectedApi == GraphicsApi.Unknown ? Visibility.Visible : Visibility.Collapsed;
        Dx10SetupText.Visibility = HasTarget && SelectedApi == GraphicsApi.Direct3D10 ? Visibility.Visible : Visibility.Collapsed;
        bool helper = InstallerEngine.UsesDx10Helper(Options);
        ((ComboBoxItem)ProfileBox.Items[0]).Content = helper ? "DX10 helper - RenoDX Generic" : "Standard - ShortFuse";
        RouteHelpText.Text = !HasRoute ? "Check the game's graphics settings if you're unsure." : SelectedRoute == SetupRoute.NativeDlss
            ? "Uses the game's DLSS. No Feeder. The new Nexus preset starts with effects off."
            : helper ? "DX10 uses Feeder's 64-bit helper and RenoDX Generic. Depth setup is required in the game's ReShade."
            : "Feeder supplies the inputs for games without DLSS. Depth setup is required in ReShade.";
    }
    private void ProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized) return;
        disableFeederEffects = false;
        CustomAddonButton.IsEnabled = Profile == "experimental";
        CustomAddonText.Text = Profile == "experimental" && experimentalAddon != null ? Path.GetFileName(experimentalAddon) : "No custom add-on selected.";
        UpdateSetupHints();
        InvalidatePlan();
    }
    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Game folder", Multiselect = false };
        if (dialog.ShowDialog(this) == true) GamePathBox.Text = dialog.FolderName;
    }
    private void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Game executable", Filter = "Game executable (*.exe)|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) GamePathBox.Text = dialog.FileName;
    }
    private void CustomAddon_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Custom add-on", Filter = "64-bit add-on (*.addon64)|*.addon64", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try { FileSafety.AddonFamily(dialog.FileName); experimentalAddon = dialog.FileName; disableFeederEffects = false; CustomAddonText.Text = Path.GetFileName(experimentalAddon); InvalidatePlan(); }
        catch (Exception ex) { ShowError(ex); }
    }
    private void SetBusy(bool value, string status)
    {
        busy = value; StatusText.Text = status; BusyBar.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        GamePathBox.IsEnabled = BrowseFolderButton.IsEnabled = BrowseExeButton.IsEnabled = ScanButton.IsEnabled = !value;
        OptionsPanel.IsEnabled = TargetsList.IsEnabled = InstalledGamesList.IsEnabled = GameFilterBox.IsEnabled = !value;
        AddonVersionBox.IsEnabled = !value && AddonVersionBox.Items.Count > 1;
        NeuralRuntimeVersionBox.IsEnabled = !value && NeuralRuntimeVersionBox.Items.Count > 1;
        LibraryModeButton.IsEnabled = ManagedModeButton.IsEnabled = ManualModeButton.IsEnabled = !value;
        RefreshGamesButton.IsEnabled = !value && !libraryBusy; CheckReShadeButton.IsEnabled = !value && !updateBusy;
        FeatureReportExpander.IsEnabled = !value && currentGame?.Snapshot?.Complete == true && LibraryErrorText.Text.Length == 0;
        OpenBackupButton.IsEnabled = !value && backupDirectory != null; UpdateWizardNavigation();
    }
    private IProgress<string> Progress => new Progress<string>(text => { StatusText.Text = text; LogBox.AppendText(text + Environment.NewLine); LogBox.ScrollToEnd(); });
    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanPathAsync(GamePathBox.Text);
    private async Task ScanPathAsync(string path, string? preferredExecutable = null)
    {
        if (busy) return;
        InvalidatePlan(); SetBusy(true, "Finding the game…"); cancellation = new();
        try
        {
            var result = await Task.Run(() => new GameDiscovery().Scan(path, cancellation.Token, includeWithoutReShade: true));
            TargetsList.ItemsSource = result.Targets;
            var preferred = result.Targets.FirstOrDefault(t => t.Executable.Equals(preferredExecutable, StringComparison.OrdinalIgnoreCase));
            if (preferred != null) TargetsList.SelectedItem = preferred;
            else if (result.Targets.Count == 1) TargetsList.SelectedIndex = 0;
            DiscoveryText.Text = result.Targets.Count > 1 ? "Choose the executable you play." : string.Join(" ", result.Notices);
            if (TargetsList.SelectedItem is GameTarget target) await InspectTargetAsync(target, restoreVersions: true);
            SetBusy(false, HasTarget ? "Game selected" : result.Targets.Count == 0 ? "No supported game found" : "Choose an executable");
        }
        catch (Exception ex) { ShowError(ex); SetBusy(false, "Could not read the game"); }
        finally { cancellation.Dispose(); cancellation = null; }
    }
    private async Task InspectTargetAsync(GameTarget target, SetupRoute? route = null, bool installed = false, GraphicsApi? graphicsApi = null, bool restoreVersions = false)
    {
        string name = SelectedLibraryGame?.Name ?? trackedGames.FirstOrDefault(g => g.Executable.Equals(target.Executable, StringComparison.OrdinalIgnoreCase))?.Name ?? target.DisplayName;
        string source = SelectedLibraryGame?.Source ?? "Manual";
        var snapshot = await Task.Run(() => inventory.Inspect(target, backgroundCancellation.Token));
        try
        {
            currentGame = await Task.Run(() => libraryStore.Record(target, name, source, snapshot, route, installed, graphicsApi));
            trackedGames = libraryStore.Read(); ApplyGameFilter();
            LibraryErrorText.Text = "";
        }
        catch (Exception ex)
        {
            currentGame = new() { Name = name, Root = target.Root, Executable = target.Executable, Snapshot = snapshot, Route = route, GraphicsApi = graphicsApi };
            LogBox.AppendText("Library: " + ex.Message + Environment.NewLine);
            LibraryErrorText.Text = "Could not save the game library. Check the Activity log for details.";
        }
        if (restoreVersions) RestoreInstalledVersionChoices(target, snapshot);
        ShowInventory(currentGame, name);
    }
    private void ShowInventory(TrackedGame? game, string? name)
    {
        currentGame = game;
        InventoryTitle.Text = name ?? "Game details";
        InventoryStatusText.Text = game?.Snapshot == null ? (name == null ? "Select a game to see its DLSS setup." : "Not checked. Choose Check status or Set up.") :
            $"Checked {game.Snapshot.CheckedAt.LocalDateTime:g}" + (game.Snapshot.Notices.Count == 0 ? "" : "\n" + string.Join(" ", game.Snapshot.Notices));
        VersionsList.ItemsSource = game?.Snapshot?.Components; FeaturesList.ItemsSource = game == null ? null : GameFeatures.Describe(game);
        ConfigurationText.Text = game?.Snapshot?.Configuration?.Description ?? "";
        FeatureReportExpander.IsEnabled = !busy && game?.Snapshot?.Complete == true && LibraryErrorText.Text.Length == 0;
        UpdateSetupHints();
    }
    private async void SaveFeature_Click(object sender, RoutedEventArgs e)
    {
        if (busy || currentGame == null) return;
        string exe = currentGame.Executable;
        string key = (ReportedFeatureBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "sr";
        if (!Enum.TryParse<ReportedFeatureState>((ReportedStateBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var state)) return;
        SetBusy(true, "Saving setting…");
        try
        {
            var target = new GameTarget(currentGame.Root, Path.GetDirectoryName(exe)!, exe,
                InstallerEngine.GraphicsProxyNames.Select(n => Path.Combine(Path.GetDirectoryName(exe)!, n)).Where(File.Exists).Where(FileSafety.IsReShade).ToArray());
            await InspectTargetAsync(target);
            currentGame = libraryStore.ReportFeature(exe, key, state); trackedGames = libraryStore.Read();
            ApplyGameFilter(); ShowInventory(currentGame, currentGame.Name); SetBusy(false, "Setting saved · your report");
        }
        catch (Exception ex) { ShowError(ex); SetBusy(false, "Could not save the setting"); }
    }
    private async void Preview_Click(object sender, RoutedEventArgs e) => await GeneratePreviewAsync();
    private async Task GeneratePreviewAsync()
    {
        if (busy || wizardComplete || !HasRoute || TargetsList.SelectedItem is not GameTarget target) return;
        InvalidatePlan(keepPage: true); var options = Options; SetBusy(true, "Checking setup…");
        try
        {
            if (InstallerEngine.NeedsReShadePackage(target, options))
            {
                if (options.GraphicsApi is not (GraphicsApi.Direct3D10 or GraphicsApi.Direct3D11 or GraphicsApi.Direct3D12))
                { AdvancedExpander.IsExpanded = true; throw new InvalidOperationException("Choose DirectX 10, 11 or 12 under Advanced. Check the game's graphics settings if you're unsure."); }
                var acquired = await reshade.AcquireAsync(Progress); options = options with { ReShade = acquired.Package }; ReShadeUpdateText.Text = acquired.Status;
            }
            plan = await Task.Run(() => engine.BuildPlan(target, options)); PlanGrid.ItemsSource = plan.Files;
            int changes = plan.Files.Count(f => f.ChangesFile);
            PlanText.Text = SelectedLibraryGame?.Name ?? target.DisplayName;
            PlanPathText.Text = target.Directory; PlanPathText.ToolTip = target.Directory;
            ReviewSummaryText.Text = ReviewSummary(plan);
            ReviewText.Text = string.Join(Environment.NewLine, plan.Warnings.Select(t => "• " + t));
            ReviewErrorsText.Text = string.Join(Environment.NewLine, plan.Errors.Select(t => "• " + t));
            ReviewBlockersCard.Visibility = plan.CanInstall ? Visibility.Collapsed : Visibility.Visible;
            FeederResolutionPanel.Visibility = DisableFeederButton.Visibility = plan.CanDisableFeederEffects ? Visibility.Visible : Visibility.Collapsed;
            if (plan.Errors.Any(e => e.Contains("DirectX", StringComparison.OrdinalIgnoreCase))) AdvancedExpander.IsExpanded = true;
            SetBusy(false, ReviewStatus); InstallButton.Content = changes == 0 ? "Verify" : "Install";
            FileDetailsExpander.IsExpanded = false; SetWizardStep(WizardStep.Review);
        }
        catch (Exception ex) { plan = null; ShowError(ex); SetBusy(false, "Could not check setup"); }
    }
    private string ReviewStatus => plan?.CanInstall == true ? "Ready" : "Install blocked — " + (plan?.Errors.FirstOrDefault() ?? "Recheck the setup.");
    private async void DisableFeeder_Click(object sender, RoutedEventArgs e)
    {
        if (busy || step != WizardStep.Review || SelectedRoute != SetupRoute.NativeDlss || plan?.CanDisableFeederEffects != true) return;
        disableFeederEffects = true;
        await GeneratePreviewAsync();
    }
    private static string ReviewSummary(InstallPlan review)
    {
        static string Label(string name) => name switch
        {
            "nvngx_dlssnr.dll" => "Neural Rendering", "nvngx_dlss.dll" => "Super Resolution",
            "dlss5-feed.addon64" => "Feeder", "renodx-dlss.addon64" => "ShortFuse", "renodx-dlss5.addon64" => "RenoDX Generic",
            "dlss5-feed-helper.addon64" => "Feeder helper", "dlss5-feed-host64.exe" => "64-bit host",
            _ => name.StartsWith("ReShade") ? "ReShade" : name.EndsWith(".fx") || name.EndsWith(".fxh") || name.EndsWith(".png") ? "Shader support" : "Preset and settings"
        };
        List<string> summary = [review.Options.Route == SetupRoute.NativeDlss ? "Setup: Native DLSS" : review.UsesDx10Helper ? "Setup: DirectX 10 Feeder helper (beta)" : "Setup: Feeder"];
        summary.Add("Add-on selected: " + review.AddonLabel);
        summary.Add("DLSS-NR selected: " + review.NeuralRuntimeLabel);
        if (review.Files.Any(f => f.Component == "nvngx_dlssnr.dll" && f.Action == Operation.Keep))
            summary.Add("DLSS-NR: Keep the existing runtime; the selected version will not be installed");
        foreach (var group in new[] { ("Install", Operation.Install), ("Replace", Operation.Replace), ("Keep", Operation.Keep), ("Back up and remove", Operation.Retire) })
        {
            var labels = review.Files.Where(f => f.Action == group.Item2 || group.Item2 == Operation.Keep && f.Action == Operation.Current).Select(f => Label(f.Component)).Distinct().ToArray();
            if (labels.Length != 0) summary.Add(group.Item1 + ": " + string.Join(", ", labels));
        }
        if (review.Files.Any(f => f.Component == "Active preset (Feeder off)" && f.ChangesFile))
            summary.Add("Active preset: Turn off Feeder; retain other effects and settings");
        return string.Join("\n", summary);
    }
    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (busy || step != WizardStep.Review || plan?.CanInstall != true) return;
        var selectedPlan = plan; int changes = selectedPlan.Files.Count(f => f.ChangesFile);
        if (changes != 0 && MessageBox.Show(this, $"Install for {SelectedLibraryGame?.Name ?? selectedPlan.Target.DisplayName}?\n\n{selectedPlan.Target.Directory}",
            "DLSS Nexus", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        SetBusy(true, "Installing…"); var progress = Progress;
        try
        {
            var result = await Task.Run(() => engine.Install(selectedPlan, progress));
            await FinishInstallationAsync(selectedPlan, result);
        }
        catch (Exception ex) { plan = null; ShowError(ex); SetBusy(false, "Installation stopped"); }
    }
    internal async Task FinishInstallationAsync(InstallPlan completedPlan, InstallResult result)
    {
        try { await InspectTargetAsync(completedPlan.Target, completedPlan.Options.Route, installed: result.FilesChanged != 0, graphicsApi: completedPlan.Options.GraphicsApi); }
        catch (Exception ex) { LibraryErrorText.Text = "Installed, but the game library could not be refreshed."; LogBox.AppendText(ex.Message + Environment.NewLine); }
        SetBusy(false, result.FilesChanged == 0 ? "Verified" : "Installed");
        DisplayCompletion(completedPlan, result);
    }
    internal void DisplayCompletion(InstallPlan completedPlan, InstallResult result)
    {
        plan = null; wizardComplete = true; backupDirectory = result.BackupDirectory ?? backupDirectory;
        completedTarget = completedPlan.Target; CompletionLaunchStatusText.Text = "";
        OpenBackupButton.IsEnabled = backupDirectory != null; completionDirectory = completedPlan.Target.Directory;
        string name = SelectedLibraryGame?.Name ?? completedPlan.Target.DisplayName;
        CompletionTitle.Text = result.FilesChanged == 0 ? "Verified" : "Installed";
        CompletionDetails.Text = $"{name}\n{result.FilesVerified} files verified\nAdd-on: {completedPlan.AddonLabel}\nDLSS-NR: " +
            (completedPlan.Files.Any(f => f.Component == "nvngx_dlssnr.dll" && f.Action == Operation.Keep)
                ? "Existing runtime kept (selected alternative not installed)" : completedPlan.NeuralRuntimeLabel);
        CompletionWarningText.Text = string.Join("\n", new[] { LibraryErrorText.Text,
            completedPlan.UsesDx10Helper ? "DirectX 10 Feeder is a beta route. Verify neural activity in the helper; installed files do not confirm gameplay compatibility." : "" }.Where(t => t.Length != 0));
        CompletionStepsText.Text = completedPlan.Options.Route == SetupRoute.NativeDlss
            ? "1. Start the game and enable DLSS in its graphics settings.\n2. Open ReShade (Home), " +
                (completedPlan.Files.Any(f => f.Component == "Active preset (Feeder off)" && f.ChangesFile) ? "keep your active preset" : "select DLSS-Nexus.ini") +
                " and open the ShortFuse panel.\n3. Enable Neural Rendering and check its activity. Leave Feeder effects off for native DLSS."
            : "1. Start the game and open ReShade (Home).\n2. Select DLSS-Nexus.ini.\n3. In Generic Depth, select this game's depth buffer.\n4. " + (completedPlan.UsesDx10Helper
                ? "In DLSS 5 Feed, show the DLSS 5 panel in-game (windowed/borderless).\n5. Enable Neural Rendering in the helper's RenoDX Generic panel and check activity."
                : completedPlan.Family == "Controller"
                ? "In ShortFuse, select the Present hook and check neural activity."
                : "Check neural activity in the RenoDX Generic panel.");
        CompletionCard.Visibility = Visibility.Visible; InstallButton.IsEnabled = false;
        SetWizardStep(WizardStep.Complete);
    }
    private void OpenGameFolder_Click(object sender, RoutedEventArgs e) { if (completionDirectory != null) OpenFolder(completionDirectory); }
    private void OpenBackup_Click(object sender, RoutedEventArgs e) { if (backupDirectory != null) OpenFolder(backupDirectory); }
    private void OpenFolder(string directory)
    {
        try { var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false }; start.ArgumentList.Add(directory); Process.Start(start); }
        catch (Exception ex) { ShowError(ex); }
    }
    private void ShowError(Exception ex) { LogBox.AppendText(ex.Message + Environment.NewLine); MessageBox.Show(this, ex.Message, "DLSS Nexus", MessageBoxButton.OK, MessageBoxImage.Warning); }
    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e) { if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this); else SystemCommands.MaximizeWindow(this); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!busy) { backgroundCancellation.Cancel(); return; }
        e.Cancel = true; MessageBox.Show(this, "Wait for the current operation to finish.", "DLSS Nexus", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}

internal sealed record GameListItem(string Name, string Root, string Source, string Status, string? Executable);
