using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Nexus;

public partial class MainWindow
{
    private HwndSource? chromeSource;

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        chromeSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        chromeSource?.AddHook(ChromeWindowProc);
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        chromeSource?.RemoveHook(ChromeWindowProc); chromeSource = null;
    }

    private static IntPtr ChromeWindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int getMinMaxInfo = 0x0024;
        if (message != getMinMaxInfo) return IntPtr.Zero;
        var monitor = MonitorFromWindow(hwnd, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;
        var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var geometry = MaximizedGeometry(info.Monitor.Left, info.Monitor.Top,
            info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom);
        limits.MaxPosition = new() { X = geometry.X, Y = geometry.Y };
        limits.MaxSize = new() { X = geometry.Width, Y = geometry.Height };
        Marshal.StructureToPtr(limits, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    // WM_GETMINMAXINFO uses physical pixels, including on high-DPI and negative-origin monitors.
    internal static (int X, int Y, int Width, int Height) MaximizedGeometry(int monitorLeft, int monitorTop,
        int workLeft, int workTop, int workRight, int workBottom) =>
        (workLeft - monitorLeft, workTop - monitorTop, workRight - workLeft, workBottom - workTop);

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo
    { public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo
    { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}