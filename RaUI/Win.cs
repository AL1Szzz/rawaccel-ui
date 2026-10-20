using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace RaUI;

/// <summary>Win11 DWM niceties: Mica backdrop, rounded corners, dark titlebar. All optional.</summary>
public static class Win
{
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    const int DWMWCP_ROUND = 2;
    const int DWMSBT_NONE = 1;
    const int DWMSBT_MAINWINDOW = 2; // Mica
    const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic

    public static bool TryApplyMica(Window w, bool dark = true)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return false;

        int success = 0;

        int corner = DWMWCP_ROUND;
        if (DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, 4) == 0) success++;

        int mode = dark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref mode, 4) == 0) success++;

        int backdrop = DWMSBT_MAINWINDOW;
        if (DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, 4) != 0)
        {
            // Windows 10 has no Mica: fall back to no backdrop, UI keeps its solid theme
            int none = DWMSBT_NONE;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref none, 4);
            return false;
        }
        return success >= 2;
    }

    public static void SetBackdrop(Window w, int kind) // 0 mica, 1 acrylic, 2 none
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int backdrop = kind switch { 0 => DWMSBT_MAINWINDOW, 1 => DWMSBT_TRANSIENTWINDOW, _ => DWMSBT_NONE };
        DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, 4);
    }

    /// <summary>Work area of the monitor the window lives on, in DIPs.</summary>
    public static Rect WorkAreaDip(Window w)
    {
        var src = PresentationSource.FromVisual(w);
        var dpiX = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var dpiY = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        var wa = SystemParameters.WorkArea;
        return new Rect(wa.Left / dpiX * dpiX, wa.Top / dpiY * dpiY, wa.Width, wa.Height);
    }
}
