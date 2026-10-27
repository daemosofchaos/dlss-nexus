using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Nexus;
using Nexus.Core;

internal static partial class Program
{
    private const string MigrationPreset = "Techniques=Lumenite_Kernel@lumenite_Kernel.fx,DLSS5_Feed@DLSS5_Feed.fx,CustomEffect@Custom.fx\r\nTechniqueSorting=Lumenite_Kernel@lumenite_Kernel.fx,DLSS5_Feed@DLSS5_Feed.fx,CustomEffect@Custom.fx\r\n\r\n[DLSS5_Feed.fx]\r\nPreprocessorDefinitions=DLSS5_MV_PROVIDER=3\r\n\r\n[Custom.fx]\r\nStrength=0.75\r\n";
    private static string CreateMigrationFixture(string output, BundleCatalog catalog)
    {
        string game = Path.Combine(output, "native-migration-game"), directory = Path.Combine(game, "Project", "Binaries", "Win64");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "Game-Win64-Shipping.exe"), SyntheticCatalog.Pe("inert game"));
        File.WriteAllBytes(Path.Combine(directory, "dxgi.dll"), SyntheticCatalog.Pe("ReShade fixture"));
        File.WriteAllText(Path.Combine(directory, "ReShade.ini"), "[GENERAL]\r\nAddonPath=.\\\r\nEffectSearchPaths=.\\reshade-shaders\\Shaders\\**\r\nTextureSearchPaths=.\\reshade-shaders\\Textures\\**\r\nPresetPath=.\\Existing-Feeder.ini\r\nPerformanceMode=0\r\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "Existing-Feeder.ini"), MigrationPreset, new UTF8Encoding(true));
        foreach (var pair in new[] { ("dlss5-feed.addon64", "payload/dlss5-feed.addon64"), ("renodx-dlss5.addon64", "payload/legacy/renodx-dlss5.addon64") })
        {
            using var input = catalog.OpenSource(pair.Item2); using var file = File.Create(Path.Combine(directory, pair.Item1)); input.CopyTo(file);
        }
        return directory;
    }
    private static void CheckReviewFeedback(string output, BundleCatalog catalog)
    {
        string directory = CreateMigrationFixture(output, catalog), game = Path.GetFullPath(Path.Combine(directory, "../../.."));
        var window = new MainWindow(catalog, new ReShadeService(Path.Combine(output, "migration-cache"), new HttpClient(new FixtureDownloads(null))),
            _ => new GameLibraryResult([], []), new GameLibraryStore(Path.Combine(output, "migration-state", "library.json")), _ => throw new InvalidOperationException("Fixture launch is disabled."));
        T C<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Done() => Wait(() => C<ProgressBar>("BusyBar").Visibility == Visibility.Collapsed);
        Click(C<Button>("ManualModeButton")); C<TextBox>("GamePathBox").Text = Path.Combine(directory, "Game-Win64-Shipping.exe");
        Click(C<Button>("ScanButton")); Done(); Click(C<Button>("WizardNextButton")); Done();
        C<ComboBox>("RouteBox").SelectedIndex = 1; C<ComboBox>("ApiBox").SelectedIndex = 2;
        C<CheckBox>("ReplaceBox").IsChecked = true; Click(C<CheckBox>("ReplaceBox"));
        var before = Snapshot(game); Click(C<Button>("WizardNextButton")); Done();
        Check(!C<Button>("InstallButton").IsEnabled && window.CurrentStep == WizardStep.Review, "Active Feeder safely blocks native migration");
        var blockers = window.FindName("ReviewBlockersCard") as Border;
        Check(blockers?.Visibility == Visibility.Visible, "A disabled Install has a distinct visible blocking-error panel");
        Check(C<TextBlock>("ReviewErrorsText").Text.Contains("Feeder") && !C<TextBlock>("ReviewText").Text.Contains("Turn off"), "Blocking reasons are separate from advisory warnings");
        Check(C<TextBlock>("StatusText").Text.Contains("blocked", StringComparison.OrdinalIgnoreCase) && C<Button>("InstallButton").ToolTip?.ToString()?.Contains("Feeder") == true,
            "Footer and disabled Install explain the actual blocker");
        Check(C<Button>("DisableFeederButton").Visibility == Visibility.Visible, "Native migration offers explicit backed-up Feeder deactivation");
        Click(C<Button>("PreviewButton")); Done();
        Check(!C<Button>("InstallButton").IsEnabled && blockers!.Visibility == Visibility.Visible, "Recheck never silently grants preset migration consent");
        Click(C<Button>("WizardBackButton")); C<CheckBox>("ReplaceBox").IsChecked = false; Click(C<CheckBox>("ReplaceBox"));
        Click(C<Button>("WizardNextButton")); Done(); Click(C<Button>("DisableFeederButton")); Done();
        Check(!C<Button>("InstallButton").IsEnabled && C<TextBlock>("ReviewErrorsText").Text.Contains("Replace existing") && blockers!.Visibility == Visibility.Visible,
            "Feeder resolution leaves unrelated replacement blockers visible and Install disabled");
        Click(C<Button>("WizardBackButton")); C<CheckBox>("ReplaceBox").IsChecked = true; Click(C<CheckBox>("ReplaceBox"));
        Click(C<Button>("WizardNextButton")); Done();
        Check(!C<Button>("InstallButton").IsEnabled && C<Button>("DisableFeederButton").Visibility == Visibility.Visible, "Changing setup choices clears preset migration consent");
        Click(C<Button>("DisableFeederButton")); Done();
        Check(C<Button>("InstallButton").IsEnabled && blockers!.Visibility == Visibility.Collapsed, "Review resolution enables Install only after the blocker is resolved");
        Click(C<Button>("PreviewButton")); Done();
        Check(C<Button>("InstallButton").IsEnabled && C<Button>("InstallButton").ToolTip == null && C<TextBlock>("ReviewErrorsText").Text.Length == 0,
            "Recheck of the same resolved setup retains consent and removes stale blocker feedback");
        Check(C<TextBlock>("ReviewText").Text.Length != 0 && C<Button>("InstallButton").IsEnabled, "Advisory warnings alone do not disable Install");
        var plan = (InstallPlan)typeof(MainWindow).GetField("plan", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        string presetPath = Path.Combine(directory, "Existing-Feeder.ini");
        Check(plan.Files.Any(f => f.Destination == presetPath && f.Action == Operation.Replace) && plan.Files.Any(f => f.Component == "dlss5-feed.addon64" && f.Action == Operation.Retire),
            "Review includes preset deactivation and Feeder add-on retirement");
        Check(Same(before, Snapshot(game)), "Resolving the review changes no game files before Install");
        var result = new InstallerEngine(catalog).Install(plan);
        var finish = window.FinishInstallationAsync(plan, result); Wait(() => finish.IsCompleted); finish.GetAwaiter().GetResult();
        Check(GameConfiguration.Read(plan.Target, [directory]).FeederEffectEnabled == false && !File.Exists(Path.Combine(directory, "dlss5-feed.addon64")), "Fixture installation disables Feeder in the selected preset and retires its add-on");
        Check(File.ReadAllText(presetPath).Contains("CustomEffect@Custom.fx") && File.ReadAllText(presetPath).Contains("Strength=0.75"), "Native migration retains unrelated preset techniques and settings");
        Check(C<TextBlock>("CompletionStepsText").Text.Contains("keep your active preset"), "Migration completion keeps the reviewed preset rather than switching away from custom settings");
        Click(C<Button>("AnotherGameButton"));
        Check(window.FindName("DisableFeederButton") is Button { Visibility: Visibility.Collapsed }, "Returning to games clears migration consent and feedback");
        window.Close();
    }
    private static string CreateDesktopVersionFixture(string output, BundleCatalog catalog)
    {
        string directory = Path.Combine(output, "version-choice-game", "Project", "Binaries", "Win64");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "Game-Win64-Shipping.exe"), SyntheticCatalog.Pe("inert version-choice game"));
        File.WriteAllBytes(Path.Combine(directory, "dxgi.dll"), SyntheticCatalog.Pe("ReShade version-choice fixture"));
        File.WriteAllText(Path.Combine(directory, "ReShade.ini"), "[GENERAL]\r\nAddonPath=.\\\r\nEffectSearchPaths=.\\reshade-shaders\\Shaders\\**\r\nTextureSearchPaths=.\\reshade-shaders\\Textures\\**\r\nPresetPath=.\\Existing-Custom.ini\r\nPerformanceMode=0\r\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "Existing-Custom.ini"), "Techniques=CustomEffect@Custom.fx\r\n[Custom.fx]\r\nStrength=0.75\r\n", new UTF8Encoding(true));
        foreach (string name in new[] { "renodx-dlss.addon64", "nvngx_dlssnr.dll", "nvngx_dlss.dll" })
        {
            using var input = catalog.OpenSource("payload/" + name); using var file = File.Create(Path.Combine(directory, name)); input.CopyTo(file);
        }
        File.WriteAllText(Path.Combine(output, "desktop-fixture-before.json"), System.Text.Json.JsonSerializer.Serialize(Snapshot(directory), new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return directory;
    }
    private static int RunDesktopSmoke(string output, Application app, bool versionChoices = false)
    {
        var catalog = BundleCatalog.FromEmbedded(typeof(MainWindow).Assembly);
        catalog.VerifyAll();
        string directory = versionChoices ? CreateDesktopVersionFixture(output, catalog) : CreateMigrationFixture(output, catalog);
        string game = Path.GetFullPath(Path.Combine(directory, "../../.."));
        var window = new MainWindow(catalog, new ReShadeService(Path.Combine(output, "desktop-cache"), new HttpClient(new FixtureDownloads(null))),
            _ => new GameLibraryResult([new(versionChoices ? "Version selection fixture" : "Native migration fixture", game, "Synthetic", Path.Combine(directory, "Game-Win64-Shipping.exe"))], []),
            new GameLibraryStore(Path.Combine(output, "desktop-state", "library.json")), _ => throw new InvalidOperationException("Game launches are disabled in this fixture."));
        window.Title = "DLSS Nexus — isolated desktop test";
        DesktopMetrics.Attach(window, output);
        window.Closed += (_, _) => app.Shutdown();
        File.WriteAllText(Path.Combine(output, "desktop-fixture.txt"), directory);
        window.Show(); return app.Run();
    }
}
