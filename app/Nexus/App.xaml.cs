using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using Nexus.Core;

namespace Nexus;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var catalog = BundleCatalog.FromEmbedded(Assembly.GetExecutingAssembly());
            if (e.Args.Length != 0)
            {
                if (e.Args is ["--verify-package", var report])
                {
                    int count = catalog.VerifyAll();
                    File.WriteAllText(report, JsonSerializer.Serialize(new { product = "DLSS Nexus", valid = true,
                        componentsVerified = count, framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                        nativeInstaller = true, scriptsRequired = false }, JsonSettings.Options));
                    Shutdown(0); return;
                }
                if (e.Args is ["--scan", var root, "--report", var output])
                {
                    var scan = new GameDiscovery().Scan(root, includeWithoutReShade: true);
                    File.WriteAllText(output, JsonSerializer.Serialize(scan, JsonSettings.Options));
                    Shutdown(0); return;
                }
                if (e.Args is ["--inventory", var inventoryRoot, "--report", var inventoryReport])
                {
                    var snapshots = await Task.Run(() => new GameDiscovery().Scan(inventoryRoot, includeWithoutReShade: true).Targets
                        .Select(target => new { target, inventory = new GameInventory(catalog).Inspect(target) }).ToArray());
                    File.WriteAllText(inventoryReport, JsonSerializer.Serialize(new { product = "DLSS Nexus", snapshots }, JsonSettings.Options));
                    Shutdown(0); return;
                }
                if (e.Args is ["--list-games", "--report", var libraryReport])
                {
                    var library = await Task.Run(() => InstalledGames.Discover());
                    File.WriteAllText(libraryReport, JsonSerializer.Serialize(library, JsonSettings.Options));
                    Shutdown(0); return;
                }
                if (e.Args is ["--prepare-reshade", "--report", var cacheReport])
                {
                    var acquisition = await new ReShadeService().AcquireAsync();
                    File.WriteAllText(cacheReport, JsonSerializer.Serialize(acquisition, JsonSettings.Options));
                    Shutdown(0); return;
                }
                throw new InvalidOperationException("Supported diagnostics: --verify-package <report.json>, --scan <game folder> --report <report.json>, --inventory <game folder> --report <report.json>, --list-games --report <report.json>, or --prepare-reshade --report <report.json>. No diagnostic installs into a game.");
            }
            var window = new MainWindow(catalog);
            MainWindow = window; window.Show();
        }
        catch (Exception ex)
        {
            if (e.Args.Length != 0)
            {
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "DLSS-Nexus-error.txt"), ex.ToString()); } catch (IOException) { }
            }
            else MessageBox.Show(ex.Message, "DLSS Nexus", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}