using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Nexus.Core;

namespace Nexus;

internal enum WizardStep { Game, Components, Review, Complete }

public partial class MainWindow
{
    private WizardStep step;
    private bool wizardComplete;
    private int transitionGeneration;
    internal WizardStep CurrentStep => step;
    internal bool LastTransitionAnimated { get; private set; }
    internal Func<bool> AnimationPolicy { get; set; } = () => SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
    private bool HasTarget => TargetsList.SelectedItem is GameTarget;
    private FrameworkElement[] Pages => [GamePage, ComponentsPage, ReviewPage, CompletePage];

    private void UpdateWizardNavigation()
    {
        if (!initialized) return;
        bool ready = HasTarget;
        bool selection = ready || SelectedLibraryGame != null || ManualPanel.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(GamePathBox.Text);
        StepGameButton.IsEnabled = !busy && !wizardComplete;
        StepComponentsButton.IsEnabled = !busy && ready && !wizardComplete;
        StepReviewButton.IsEnabled = !busy && ready && HasRoute && !wizardComplete;
        StepCompleteButton.IsEnabled = !busy && wizardComplete;
        WizardBackButton.IsEnabled = !busy && step is WizardStep.Components or WizardStep.Review;
        WizardNextButton.Visibility = step is WizardStep.Game or WizardStep.Components ? Visibility.Visible : Visibility.Collapsed;
        WizardNextButton.Content = step == WizardStep.Game ? "Set up" : "Review";
        WizardNextButton.IsEnabled = !busy && (step == WizardStep.Game ? selection : ready && HasRoute);
        CheckStatusButton.IsEnabled = !busy && (SelectedLibraryGame != null || ready);
        UpdateLaunchActions();
        InstallButton.Visibility = step == WizardStep.Review ? Visibility.Visible : Visibility.Collapsed;
        InstallButton.IsEnabled = !busy && step == WizardStep.Review && plan?.CanInstall == true;
        ToolTipService.SetShowOnDisabled(InstallButton, true);
        InstallButton.ToolTip = step == WizardStep.Review && plan?.CanInstall == false ? string.Join("\n", plan.Errors) : null;
        DisableFeederButton.IsEnabled = !busy && plan?.CanDisableFeederEffects == true;
        WizardFinishButton.Visibility = step == WizardStep.Complete ? Visibility.Visible : Visibility.Collapsed;
        WizardFinishButton.IsEnabled = !busy;
        AnimateTransitionsBox.IsEnabled = !busy;
        PreviewButton.IsEnabled = !busy && ready && HasRoute && !wizardComplete;
        WizardStepText.Text = step switch { WizardStep.Game => "GAMES", WizardStep.Components => "SETUP", WizardStep.Review => "REVIEW", _ => "READY" };
        var buttons = new[] { StepGameButton, StepComponentsButton, StepReviewButton, StepCompleteButton };
        for (int i = 0; i < buttons.Length; i++)
        {
            buttons[i].BorderBrush = (Brush)FindResource(i == (int)step ? (step == WizardStep.Complete ? "Lime" : "Accent") : "Line");
            buttons[i].Foreground = (Brush)FindResource(i == (int)step ? (step == WizardStep.Complete ? "Lime" : "Accent") : "Muted");
            buttons[i].Background = (Brush)FindResource(i == (int)step ? "Selection" : "SurfaceRaised");
        }
    }
    private void SetWizardStep(WizardStep next, bool animate = true)
    {
        if (busy || (next != WizardStep.Game && !HasTarget) || (next == WizardStep.Review && plan == null) ||
            (next == WizardStep.Complete && !wizardComplete)) return;
        var previous = step; step = next; ResetPageAnimations();
        foreach (var page in Pages) page.Visibility = Visibility.Collapsed;
        var active = Pages[(int)next]; active.Visibility = Visibility.Visible; UpdateWizardNavigation();
        StatusText.Text = next switch
        {
            WizardStep.Game => HasTarget || SelectedLibraryGame != null ? "Game selected" : "Select a game",
            WizardStep.Components => HasRoute ? "Review your setup" : "Choose a setup",
            WizardStep.Review => ReviewStatus,
            _ => CompletionTitle.Text
        };
        LastTransitionAnimated = animate && previous != next && AnimateTransitionsBox.IsChecked == true && AnimationPolicy();
        if (LastTransitionAnimated)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            active.BeginAnimation(OpacityProperty, new DoubleAnimation(.15, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            var translate = new TranslateTransform(); active.RenderTransform = translate;
            translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(next > previous ? 24 : -24, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease });
            int generation = transitionGeneration; TransitionScan.Height = Math.Max(0, PageHost.ActualHeight); TransitionLayer.Visibility = Visibility.Visible;
            var scan = new DoubleAnimation(0, Math.Max(0, PageHost.ActualWidth), TimeSpan.FromMilliseconds(360)) { EasingFunction = ease };
            scan.Completed += (_, _) => { if (generation == transitionGeneration) TransitionLayer.Visibility = Visibility.Collapsed; };
            TransitionScan.BeginAnimation(Canvas.LeftProperty, scan);
            TransitionScan.BeginAnimation(OpacityProperty, new DoubleAnimation(.6, 0, TimeSpan.FromMilliseconds(360)));
        }
        if (IsVisible) Dispatcher.BeginInvoke(() =>
        {
            if (step != next || busy) return;
            if (next == WizardStep.Complete) WizardFinishButton.Focus();
            else if (next == WizardStep.Review && InstallButton.IsEnabled) InstallButton.Focus();
            else active.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        });
    }
    private void ResetPageAnimations()
    {
        transitionGeneration++;
        foreach (var page in Pages) { page.BeginAnimation(OpacityProperty, null); page.Opacity = 1; page.RenderTransform = Transform.Identity; }
        TransitionScan.BeginAnimation(Canvas.LeftProperty, null); TransitionScan.BeginAnimation(OpacityProperty, null); TransitionLayer.Visibility = Visibility.Collapsed;
    }
    private void TransitionsChanged(object sender, RoutedEventArgs e)
    { if (initialized && AnimateTransitionsBox.IsChecked != true) { ResetPageAnimations(); LastTransitionAnimated = false; } }
    private async void WizardNext_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (step == WizardStep.Game)
        {
            if (!HasTarget)
            {
                if (SelectedLibraryGame is { } game) await ScanPathAsync(game.Root, game.Executable);
                else if (ManualPanel.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(GamePathBox.Text)) await ScanPathAsync(GamePathBox.Text);
            }
            else if (TargetsList.SelectedItem is GameTarget target)
            {
                SetBusy(true, "Checking installed versions…");
                try { await InspectTargetAsync(target); }
                finally { SetBusy(false, "Game selected"); }
            }
            if (HasTarget) SetWizardStep(WizardStep.Components);
        }
        else if (step == WizardStep.Components) await GeneratePreviewAsync();
    }
    private void WizardBack_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (step == WizardStep.Components) SetWizardStep(WizardStep.Game);
        else if (step == WizardStep.Review) SetWizardStep(WizardStep.Components);
    }
    private async void Step_Click(object sender, RoutedEventArgs e)
    {
        if (busy || sender is not Button { IsEnabled: true } button || !Enum.TryParse<WizardStep>(button.Tag?.ToString(), out var next)) return;
        if (next == WizardStep.Review && plan == null) await GeneratePreviewAsync(); else SetWizardStep(next);
    }
    private void GameMode_Click(object sender, RoutedEventArgs e)
    {
        if (busy || sender is not Button button) return;
        string? mode = button.Tag?.ToString(); trackedOnly = mode == "tracked";
        SetGameMode(mode == "manual"); ApplyGameFilter();
    }
    private void SetGameMode(bool manual)
    {
        LibraryPanel.Visibility = manual ? Visibility.Collapsed : Visibility.Visible;
        ManualPanel.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
        if (manual)
        {
            applyingFilter = true; InstalledGamesList.SelectedItem = null; applyingFilter = false;
            ClearTarget(); ShowInventory(null, null);
            StatusText.Text = "Choose a game folder or executable";
        }
        LibraryModeButton.BorderBrush = (Brush)FindResource(!manual && !trackedOnly ? "Accent" : "Line");
        ManagedModeButton.BorderBrush = (Brush)FindResource(!manual && trackedOnly ? "Accent" : "Line");
        ManualModeButton.BorderBrush = (Brush)FindResource(manual ? "Accent" : "Line"); UpdateWizardNavigation();
    }
    private void AnotherGame_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        wizardComplete = false; plan = null; completionDirectory = null;
        CompletionCard.Visibility = Visibility.Collapsed; ClearTarget();
        applyingFilter = true; InstalledGamesList.SelectedItem = null; applyingFilter = false;
        GamePathBox.Clear(); GameFilterBox.Clear(); trackedOnly = false;
        ReadTrackedGames(); ApplyGameFilter(); ShowInventory(null, null);
        SetGameMode(false); SetWizardStep(WizardStep.Game); StatusText.Text = "Select a game";
    }
}
