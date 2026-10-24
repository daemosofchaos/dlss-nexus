using Nexus;

internal static partial class Program
{
    private static void CheckChromeGeometry()
    {
        foreach (int dpi in new[] { 96, 144, 192 })
        {
            int width = 1920 * dpi / 96, height = 1080 * dpi / 96, bar = 48 * dpi / 96;
            Check(MainWindow.MaximizedGeometry(0, 0, 0, 0, width, height - bar) == (0, 0, width, height - bar), $"{dpi} DPI physical geometry: bottom taskbar excluded without double scaling");
            Check(MainWindow.MaximizedGeometry(0, 0, 0, bar, width, height) == (0, bar, width, height - bar), $"{dpi} DPI physical geometry: top taskbar excluded");
            Check(MainWindow.MaximizedGeometry(-width, 0, -width + bar, 0, 0, height) == (bar, 0, width - bar, height), $"{dpi} DPI physical geometry: left taskbar on negative-origin monitor excluded");
            Check(MainWindow.MaximizedGeometry(width, -height, width, -height, 2 * width - bar, 0) == (0, 0, width - bar, height), $"{dpi} DPI physical geometry: right taskbar on secondary monitor excluded");
        }
        Check(MainWindow.MaximizedGeometry(-1920, -1080, -1920, -1080, 0, 0) == (0, 0, 1920, 1080), "Native geometry preserves full work area when no taskbar space is reserved");
    }
}