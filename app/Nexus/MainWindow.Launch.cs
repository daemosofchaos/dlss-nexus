using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Nexus.Core;

namespace Nexus;

public partial class MainWindow
{
    private const string DirectLaunchHint = "Starts the selected executable. Use the game's launcher for custom launch options or required sign-in.";

    private (string Root, string? Executable)? SelectedLaunchTarget => SelectedLibraryGame is { } game
        ? (game.Root, game.Executable)
        : TargetsList.SelectedItem is GameTarget target ? (target.Root, target.Executable) : null;

    internal static ProcessStartInfo BuildGameLaunch(string root, string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
            throw new InvalidOperationException("Choose Check status or Set up to identify the game executable first.");
        if (!Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(executable))
            throw new InvalidOperationException("Choose a local game folder and executable.");
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        executable = Path.GetFullPath(executable);
        if (root.Equals(Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(root) || !FileSafety.IsInside(root, executable) ||
            !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The executable must be an .exe inside the selected game folder.");
        FileSafety.EnsureNoLinks(executable);
        if (!File.Exists(executable))
            throw new FileNotFoundException("The game executable is no longer available. Check status or choose the game again.");
        if (!FileSafety.IsX64(executable))
            throw new InvalidOperationException("The selected file is not a supported 64-bit game executable.");
        return new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
    }

    private void UpdateLaunchActions()
    {
        SetLaunchAvailability(LaunchGameButton, GameLaunchHint, SelectedLaunchTarget, step == WizardStep.Game);
        SetLaunchAvailability(LaunchCompletedGameButton, CompletionLaunchHint,
            completedTarget is { } target ? (target.Root, target.Executable) : null, step == WizardStep.Complete && wizardComplete);
    }

    private void SetLaunchAvailability(Button button, TextBlock hint, (string Root, string? Executable)? target, bool activePage)
    {
        button.IsEnabled = false; button.ToolTip = null;
        if (target == null) { hint.Text = "Select a game to launch it."; return; }
        try
        {
            var start = BuildGameLaunch(target.Value.Root, target.Value.Executable);
            button.ToolTip = start.FileName; hint.Text = DirectLaunchHint;
            button.IsEnabled = activePage && !busy;
        }
        catch (Exception ex) { hint.Text = ex.Message; }
    }

    private void LaunchGame_Click(object sender, RoutedEventArgs e)
    {
        if (busy || step != WizardStep.Game || SelectedLaunchTarget is not { } target) return;
        LaunchGame(target.Root, target.Executable, GameLaunchStatusText);
    }

    private void LaunchCompletedGame_Click(object sender, RoutedEventArgs e)
    {
        if (busy || step != WizardStep.Complete || !wizardComplete || completedTarget == null) return;
        LaunchGame(completedTarget.Root, completedTarget.Executable, CompletionLaunchStatusText);
    }

    private void LaunchGame(string root, string? executable, TextBlock status)
    {
        try
        {
            // Revalidate at click time: library records and availability can become stale.
            startGameProcess(BuildGameLaunch(root, executable));
            status.Foreground = (System.Windows.Media.Brush)FindResource("Accent");
            status.Text = "Launch requested. Complete any prompts in the game or its launcher.";
        }
        catch (Exception ex)
        {
            status.Foreground = (System.Windows.Media.Brush)FindResource("Warning");
            status.Text = "Could not launch the game: " + ex.Message;
            LogBox.AppendText(status.Text + Environment.NewLine);
        }
        UpdateLaunchActions();
    }
}