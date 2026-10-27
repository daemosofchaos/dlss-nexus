using System.IO;
using System.Windows;
using System.Windows.Controls;
using Nexus.Core;

namespace Nexus;

public partial class MainWindow
{
    private readonly BundleCatalog versionCatalog;
    private bool applyingVersionChoices;
    private string? versionProfile;
    private string EffectiveVersionProfile => InstallerEngine.UsesDx10Helper(Options) && Profile == "default" ? "dx10" : Profile;
    private string? SelectedAddonBuild => (AddonVersionBox.SelectedItem as ComboBoxItem)?.Tag as string;
    private string? SelectedNeuralRuntimeBuild => (NeuralRuntimeVersionBox.SelectedItem as ComboBoxItem)?.Tag as string;

    private void RefreshVersionChoices(bool reset = false)
    {
        if (!initialized) return;
        applyingVersionChoices = true;
        try
        {
            if (reset || NeuralRuntimeVersionBox.Items.Count == 0)
            {
                NeuralRuntimeVersionBox.Items.Clear();
                var defaultRuntime = versionCatalog.ResolveNeuralRuntimeBuild();
                foreach (var build in versionCatalog.Manifest.NeuralRuntimeBuilds)
                    NeuralRuntimeVersionBox.Items.Add(new ComboBoxItem { Content = build.Label + (build.Id == defaultRuntime?.Id ? " · default" : ""), Tag = build.Id, ToolTip = build.Notes });
                if (NeuralRuntimeVersionBox.Items.Count == 0)
                    NeuralRuntimeVersionBox.Items.Add(new ComboBoxItem { Content = "Package default" });
                SelectVersion(NeuralRuntimeVersionBox, defaultRuntime?.Id);
            }
            string profile = EffectiveVersionProfile;
            if (reset || profile != versionProfile)
            {
                versionProfile = profile; AddonVersionBox.Items.Clear();
                var defaultAddon = versionCatalog.ResolveAddonBuild(profile);
                foreach (var build in versionCatalog.Manifest.AddonBuilds.Where(b => b.Profile == profile))
                    AddonVersionBox.Items.Add(new ComboBoxItem { Content = build.Label + (build.Id == defaultAddon?.Id ? " · default" : ""), Tag = build.Id, ToolTip = build.Notes });
                if (AddonVersionBox.Items.Count == 0)
                    AddonVersionBox.Items.Add(new ComboBoxItem { Content = profile == "experimental" ? "Use the selected custom add-on" : "Package default" });
                SelectVersion(AddonVersionBox, defaultAddon?.Id);
            }
            AddonVersionBox.IsEnabled = !busy && AddonVersionBox.Items.Count > 1;
            NeuralRuntimeVersionBox.IsEnabled = !busy && NeuralRuntimeVersionBox.Items.Count > 1;
            RefreshVersionNotes();
        }
        finally { applyingVersionChoices = false; }
    }

    private static void SelectVersion(ComboBox box, string? id) => box.SelectedItem =
        box.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (i.Tag as string) == id) ?? box.Items[0];

    private void RefreshVersionNotes()
    {
        AddonVersionNotesText.Text = versionCatalog.Manifest.AddonBuilds.FirstOrDefault(b => b.Id == SelectedAddonBuild)?.Notes ??
            (Profile == "experimental" ? "The custom file is used instead of a bundled version. Its compatibility must match this setup." : "");
        NeuralRuntimeVersionNotesText.Text = versionCatalog.Manifest.NeuralRuntimeBuilds.FirstOrDefault(b => b.Id == SelectedNeuralRuntimeBuild)?.Notes ?? "";
    }

    private void VersionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || applyingVersionChoices || busy) return;
        disableFeederEffects = false;
        UpdateSetupHints(); InvalidatePlan();
    }

    // Infer a recognized installed version only when opening a setup. A status refresh or
    // Recheck must not overwrite the user's proposed new choice. No library schema change.
    internal void RestoreInstalledVersionChoices(GameTarget target, GameSnapshot snapshot)
    {
        RefreshVersionChoices(); applyingVersionChoices = true;
        try
        {
            var paths = ReShadePaths.Resolve(target, []);
            string runtimeDirectory = InstallerEngine.NeuralRuntimeDirectory(target, Options);
            string runtimePath = Path.Combine(runtimeDirectory, "nvngx_dlssnr.dll");
            var nr = snapshot.Components.SingleOrDefault(c => c.Status == "Installed" && c.Path.Equals(runtimePath, StringComparison.OrdinalIgnoreCase));
            var matchingRuntime = versionCatalog.Manifest.NeuralRuntimeBuilds.Where(b =>
                versionCatalog.GetComponent(b.PayloadPath).Sha256.Equals(nr?.Hash, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matchingRuntime.Length == 1) SelectVersion(NeuralRuntimeVersionBox, matchingRuntime[0].Id);
            string[] addonDirectories = InstallerEngine.UsesDx10Helper(Options) ? [runtimeDirectory] : paths.AddonSearchDirectories;
            var addonHashes = snapshot.Components.Where(c => c.Status == "Installed" &&
                c.FileName.StartsWith("renodx-dlss", StringComparison.OrdinalIgnoreCase) &&
                addonDirectories.Contains(Path.GetDirectoryName(c.Path)!, StringComparer.OrdinalIgnoreCase)).Select(c => c.Hash).ToArray();
            var matchingAddons = versionCatalog.Manifest.AddonBuilds.Where(b => b.Profile == versionProfile &&
                addonHashes.Contains(versionCatalog.GetComponent(b.PayloadPath).Sha256, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (matchingAddons.Length == 1) SelectVersion(AddonVersionBox, matchingAddons[0].Id);
            RefreshVersionNotes();
        }
        finally { applyingVersionChoices = false; }
    }
}