using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Nexus;

internal static class DesktopMetrics
{
    internal static void Attach(MainWindow window, string output)
    {
        string? previous = null; int records = 0;
        window.LayoutUpdated += (_, _) =>
        {
            if (!window.IsVisible || records >= 300) return;
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref monitor) || !GetWindowRect(hwnd, out var bounds)) return;
            var status = (TextBlock)window.FindName("StatusText");
            var actions = new[] { "WizardBackButton", "WizardNextButton", "InstallButton", "WizardFinishButton" }
                .Select(n => (Button)window.FindName(n)).Where(b => b.IsVisible).Select(b =>
                {
                    var start = b.PointToScreen(new System.Windows.Point(0, 0));
                    var end = b.PointToScreen(new System.Windows.Point(b.ActualWidth, b.ActualHeight));
                    return new { b.Name, b.IsEnabled, left = start.X, top = start.Y, right = end.X, bottom = end.Y,
                        insideWorkArea = start.X >= monitor.Work.Left && start.Y >= monitor.Work.Top && end.X <= monitor.Work.Right && end.Y <= monitor.Work.Bottom };
                }).ToArray();
            string text = JsonSerializer.Serialize(new { state = window.WindowState.ToString(), step = window.CurrentStep.ToString(), dpi = GetDpiForWindow(hwnd),
                monitor = new { monitor.Monitor.Left, monitor.Monitor.Top, monitor.Monitor.Right, monitor.Monitor.Bottom },
                work = new { monitor.Work.Left, monitor.Work.Top, monitor.Work.Right, monitor.Work.Bottom },
                bounds = new { bounds.Left, bounds.Top, bounds.Right, bounds.Bottom },
                contentWidth = ((FrameworkElement)window.Content).ActualWidth, contentHeight = ((FrameworkElement)window.Content).ActualHeight,
                status = status.Text, actions });
            if (text == previous) return; previous = text; records++;
            File.AppendAllText(Path.Combine(output, "desktop-metrics.jsonl"), text + "\n");
        };
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
}