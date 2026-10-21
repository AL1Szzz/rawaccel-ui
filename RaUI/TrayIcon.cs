using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RaUI;

/// <summary>
/// Notification-area ("任务栏常驻") icon, built directly on Shell_NotifyIcon.
///
/// Deliberately NOT WinForms: enabling UseWindowsForms would make Application, Clipboard,
/// MessageBox and Brushes ambiguous in every file of this project. The icon bitmap is therefore
/// rendered with WPF and turned into an HICON through GDI, which also means the tray glyph is the
/// same art the app can export for a visual check.
///
/// The window it is attached to is never Closed while the tray lives - hiding it keeps the HWND
/// (and therefore the callback hook) alive, which is what "resident in the tray" means here.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_STATE = 0x8, NIF_SHOWTIP = 0x20;
    const uint NIM_ADD = 0x0, NIM_MODIFY = 0x1, NIM_DELETE = 0x2;
    const uint NIS_HIDDEN = 0x1;

    const int WM_TRAY = 0x0400 + 7;              // WM_USER + 7, our callback
    const int WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205;
    const int NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0408, NIN_POPUPOPEN = 0x0406;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint RegisterWindowMessage(string lpString);

    [DllImport("gdi32.dll")] static extern IntPtr CreateBitmap(int w, int h, int planes, int bitCount, byte[] bits);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO
    {
        public int fIcon;                        // 1 = icon, 0 = cursor
        public int xHotspot, yHotspot;
        public IntPtr hbmMask;                   // 1bpp AND mask
        public IntPtr hbmColor;                  // 32bpp BGRA
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    readonly Window window;
    readonly AppState state;
    readonly HwndSource source;
    readonly IntPtr hIcon;
    readonly uint taskbarCreated;
    readonly ContextMenu menu;
    bool added, hidden;

    public const uint IconId = 0x5241;          // "RA"
    public string LastError { get; private set; } = "";
    static string LastIconError = "";

    public TrayIcon(Window window, AppState state)
    {
        this.window = window;
        this.state = state;

        var helper = new WindowInteropHelper(window);
        source = HwndSource.FromHwnd(helper.Handle);
        if (source == null) { LastError = "拿不到窗口句柄，托盘没建起来"; return; }

        hIcon = BuildIcon(out string artPath);
        ArtPath = artPath;
        if (hIcon == IntPtr.Zero) { LastError = "托盘图标位图生成失败：" + LastIconError; return; }
        menu = BuildMenu();
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        source.AddHook(WndProc);

        window.Closing += OnWindowClosing;
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppState.Status) or nameof(AppState.DriverInfo) or "IsDirty")
                DispatcherInvoke(UpdateTip);
        };

        if (!Add()) { /* Add() already wrote the reason into LastError */ }
        else UpdateTip();
    }

    /// <summary>Where the generated glyph was exported, for an offscreen visual check.</summary>
    public string ArtPath { get; } = "";

    /// <summary>Text the shell shows on hover; printed by --dump-tray so it can be checked.</summary>
    public string TipText => Tip();

    void DispatcherInvoke(Action a)
    {
        if (window.CheckAccess()) a();
        else window.Dispatcher.BeginInvoke(a);
    }

    // ---------- add / remove ----------

    NOTIFYICONDATA Data(uint flags)
    {
        var d = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = source.Handle,
            uID = IconId,
            uFlags = flags,
            uCallbackMessage = WM_TRAY,
            hIcon = hIcon,
            szTip = Tip(),
            szInfo = "",
            szInfoTitle = "",
        };
        return d;
    }

    bool Add()
    {
        var d = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        added = Shell_NotifyIcon(NIM_ADD, ref d);
        if (!added)
            LastError = $"NIM_ADD 被拒绝：cbSize={d.cbSize} win32={Marshal.GetLastPInvokeError()} 结构实测={Marshal.SizeOf<NOTIFYICONDATA>()}";
        return added;
    }

    void UpdateTip()
    {
        var d = Data(NIF_TIP);
        if (!Shell_NotifyIcon(NIM_MODIFY, ref d) && added)
        {
            // an explorer restart (or a policy change) drops foreign icons: re-add rather than go dead
            added = Shell_NotifyIcon(NIM_ADD, ref d);
        }
    }

    string Tip()
    {
        // same hash the round logs print, so "图标上写的 sha" and "日志里的 sha" are comparable
        var p = state.SelectedProfile;
        string sha = p == null ? "无配置文件" : RaCore.ConfigHash.Of(p);
        string dirty = state.IsDirty ? " · 未写入" : "";
        return $"Raw Accel 现代界面  ·  {(state.DriverReachable ? "驱动已连接" : "驱动未连接")} · {sha}{dirty}";
    }

    public void SetVisible(bool visible)
    {
        var d = Data(NIF_STATE);
        d.dwState = visible ? 0u : NIS_HIDDEN;
        d.dwStateMask = NIS_HIDDEN;
        if (Shell_NotifyIcon(NIM_MODIFY, ref d)) hidden = !visible;
        else if (visible && !added) Add();
    }

    // ---------- messages ----------

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)msg == taskbarCreated && taskbarCreated != 0)
        {
            // explorer restarted: our icon vanished with the old shell
            added = false;
            Add();
            return IntPtr.Zero;
        }
        if (msg != WM_TRAY) return IntPtr.Zero;

        int note = (int)((long)lParam & 0xFFFF);
        switch (note)
        {
            case WM_LBUTTONUP:
            case WM_LBUTTONDBLCLK:
            case NIN_SELECT:
            case NIN_KEYSELECT:
                ToggleWindow();
                handled = true;
                break;
            case WM_RBUTTONUP:
            case NIN_POPUPOPEN:
                OpenMenu();
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    void OpenMenu()
    {
        // without this the menu stays on screen after the next click outside it
        SetForegroundWindow(source.Handle);
        // MousePoint follows the real cursor, which is over the icon and therefore on whatever
        // monitor the taskbar is on - positioning it relative to the (possibly hidden) window
        // would drop the menu off-screen.
        menu.PlacementTarget = window;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    public void ToggleWindow()
    {
        if (window.IsVisible && window.WindowState != WindowState.Minimized)
            window.WindowState = WindowState.Minimized;
        else
        {
            window.ShowInTaskbar = true;
            window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }
    }

    void OnWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // only intercept while this icon is really the live one - otherwise "hide to tray" would
        // hide the window with no way back and the process would quietly never exit
        if (App.IsExiting || !added || App.Tray != this || !AppState.Prefs.CloseToTray) return;
        e.Cancel = true;
        window.Hide();
        App.Shell.ShowInTaskbar = false;
        state.Status = "已收进托盘：程序还在跑，双击图标可以重新打开；要真退出请右键图标 → 退出";
        Balloon("已最小化到托盘", "关掉窗口不会退出程序，驱动里的曲线仍然生效。右键托盘图标有“退出”。");
    }

    public void Balloon(string title, string text)
    {
        const uint NIF_INFO = 0x10, NIIF_INFO = 0x1, NIIF_NOSOUND = 0x10;
        var d = Data(NIF_MESSAGE | NIF_ICON | NIF_INFO);
        d.szInfo = text;
        d.szInfoTitle = title;
        d.dwInfoFlags = NIIF_INFO | NIIF_NOSOUND;
        d.uVersionOrTimeout = 3000;
        Shell_NotifyIcon(NIM_MODIFY, ref d);
    }

    // ---------- menu ----------

    /// <summary>
    /// Renders the context menu to a PNG without ever opening it on screen - the offscreen
    /// proof that the palette is readable (this is the check for the white-on-white bug).
    /// </summary>
    public string? RenderMenuPng()
    {
        try
        {
            menu.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            menu.Arrange(new Rect(new Point(0, 0), menu.DesiredSize));
            int w = (int)Math.Ceiling(menu.ActualWidth), h = (int)Math.Ceiling(menu.ActualHeight);
            if (w <= 0 || h <= 0) return $"FAILED (size {w}x{h}, desired {menu.DesiredSize.Width:0}x{menu.DesiredSize.Height:0})";
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(menu);
            string path = System.IO.Path.Combine(RaCore.AppData.Root, "tray-menu.png");
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = System.IO.File.Create(path);
            enc.Save(fs);
            return path;
        }
        catch (Exception e) { return "FAILED: " + e.Message; }
    }

    ContextMenu BuildMenu()
    {
        var m = new ContextMenu { MinWidth = 210 };
        StyleMenu(m);
        m.Items.Add(Item("显示 / 隐藏主界面", () => ToggleWindow()));
        m.Items.Add(Item("立即把设置写入驱动", () => _ = state.WriteToDriverAsync()));
        m.Items.Add(new Separator());
        m.Items.Add(Item("打开日志目录", () =>
        {
            try
            {
                System.IO.Directory.CreateDirectory(RaCore.AppData.LogDir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe",
                    $"\"{RaCore.AppData.LogDir}\"") { UseShellExecute = true });
            }
            catch (Exception e) { state.Status = "打不开日志目录：" + e.Message; }
        }));
        var chk = new MenuItem { Header = "登录时自动启动", IsCheckable = true, IsChecked = Autostart.IsEnabled };
        chk.Click += (_, _) =>
        {
            if (chk.IsChecked) Autostart.Enable(true, true);
            else Autostart.Disable();
            state.Status = Autostart.IsEnabled ? "已注册登录自启：" + Autostart.CurrentCommand()
                                               : "已移除登录自启（驱动里的设置不受影响）";
        };
        m.Items.Add(chk);
        m.Items.Add(new Separator());
        m.Items.Add(Item("退出（曲线仍留在驱动里）", () => App.ExitApplication()));
        m.Opened += (_, _) => { if (m.Items[4] is MenuItem c) c.IsChecked = Autostart.IsEnabled; };
        return m;
    }

    /// <summary>
    /// The tray window is invisible, so the menu inherits nothing useful from it: the WPF
    /// defaults are a light menu background while the text colour lands on white - white on
    /// white, exactly what was reported. Pin the whole palette explicitly, dark like the app.
    /// </summary>
    static void StyleMenu(ContextMenu m)
    {
        var bg = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
        var hover = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
        var border = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A));
        bg.Freeze(); hover.Freeze(); border.Freeze();

        m.Foreground = Brushes.White;

        // Even with Background set, the system ContextMenu template keeps a white icon-gutter
        // column on the left (part of the default Aero2 chrome). Replace the whole template:
        // one dark rounded border around the items, nothing else inherited from the theme.
        var t = new ControlTemplate(typeof(ContextMenu));
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.Name = "Bd";
        bd.SetValue(Border.BackgroundProperty, bg);
        bd.SetValue(Border.BorderBrushProperty, border);
        bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        bd.SetValue(Border.PaddingProperty, new Thickness(0, 5, 0, 5));
        var items = new FrameworkElementFactory(typeof(ItemsPresenter));
        items.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 0, 2, 0));
        bd.AppendChild(items);
        t.VisualTree = bd;
        m.Template = t;

        // NOT ItemContainerStyle: that one is applied to Separators too and WPF refuses a
        // MenuItem-typed style on a Separator ("样式不能应用于 Separator 类型"). Each item gets
        // the style directly instead, and the separator uses the keyed resource style below.
        menuItemStyle = new Style(typeof(MenuItem));
        menuItemStyle.Setters.Add(new Setter(MenuItem.BackgroundProperty, Brushes.Transparent));
        menuItemStyle.Setters.Add(new Setter(MenuItem.ForegroundProperty, Brushes.White));
        menuItemStyle.Setters.Add(new Setter(MenuItem.PaddingProperty, new Thickness(14, 7, 14, 7)));
        var hi = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
        hi.Setters.Add(new Setter(MenuItem.BackgroundProperty, hover));
        menuItemStyle.Triggers.Add(hi);

        var sep = new Style(typeof(Separator));
        var rect = new FrameworkElementFactory(typeof(System.Windows.Shapes.Rectangle));
        rect.SetValue(System.Windows.Shapes.Rectangle.FillProperty, border);
        rect.SetValue(System.Windows.Shapes.Rectangle.HeightProperty, 1.0);
        sep.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(Separator))
        {
            VisualTree = rect,
        }));
        sep.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(10, 4, 10, 4)));
        m.Resources[typeof(Separator)] = sep;
    }

    static Style menuItemStyle = new(typeof(MenuItem));   // shared; StyleMenu rebuilds it once per menu

    static MenuItem Item(string header, Action act)
    {
        var mi = new MenuItem { Header = header, Style = menuItemStyle };
        mi.Click += (_, _) => act();
        return mi;
    }

    // ---------- icon art ----------

    /// <summary>Renders the glyph at 32px and turns it into an HICON; also exports a PNG so the
    /// art can be checked without touching the taskbar.</summary>
    static IntPtr BuildIcon(out string artPath)
    {
        const int size = 32;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var bg = new LinearGradientBrush(Color.FromRgb(0x2A, 0x2F, 0x38), Color.FromRgb(0x14, 0x17, 0x1C),
                                             new Point(0, 0), new Point(size, size));
            bg.Freeze();
            dc.DrawRoundedRectangle(bg, null, new Rect(1, 1, size - 2, size - 2), 7, 7);

            // the thing this app is about: a curve that stays flat, then rises
            var line = new SolidColorBrush(Color.FromRgb(0x7C, 0xD6, 0xFF));
            line.Freeze();
            var pen = new Pen(line, 2.4)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            var fig = new PathFigure { StartPoint = new Point(5, 24) };
            fig.Segments.Add(new BezierSegment(new Point(12, 24), new Point(18, 23), new Point(21, 15), true));
            fig.Segments.Add(new BezierSegment(new Point(23, 9), new Point(25, 6), new Point(27, 5), true));
            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            geo.Freeze();
            dc.DrawGeometry(null, pen, geo);

            var dot = new SolidColorBrush(Color.FromRgb(0xFF, 0xC2, 0x4D));
            dot.Freeze();
            dc.DrawEllipse(dot, null, new Point(27, 6), 2.6, 2.6);
        }

        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);

        artPath = System.IO.Path.Combine(RaCore.AppData.Root, "tray-icon.png");
        try
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = System.IO.File.Create(artPath);
            enc.Save(fs);
        }
        catch { /* the PNG copy is only for checking the art */ }

        byte[] top = new byte[size * size * 4];
        rtb.CopyPixels(top, size * 4, 0);

        // GDI DIBs are bottom-up; the shell reads the alpha channel when the AND mask is empty
        byte[] bgra = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            Array.Copy(top, (size - 1 - y) * size * 4, bgra, y * size * 4, size * 4);

        byte[] andMask = new byte[((size + 31) / 32) * 4 * size];   // all zero = fully transparent-by-alpha

        IntPtr color = IntPtr.Zero, mask = IntPtr.Zero;
        try
        {
            color = CreateBitmap(size, size, 1, 32, bgra);
            if (color == IntPtr.Zero) { LastIconError = $"CreateBitmap(32bpp) 失败 win32={Marshal.GetLastPInvokeError()}"; return IntPtr.Zero; }
            mask = CreateBitmap(size, size, 1, 1, andMask);
            if (mask == IntPtr.Zero) { LastIconError = $"CreateBitmap(1bpp mask) 失败 win32={Marshal.GetLastPInvokeError()}"; return IntPtr.Zero; }
            var ii = new ICONINFO { fIcon = 1, hbmColor = color, hbmMask = mask };
            IntPtr h = CreateIconIndirect(ref ii);
            if (h == IntPtr.Zero) LastIconError = $"CreateIconIndirect 失败 win32={Marshal.GetLastPInvokeError()}";
            return h;
        }
        finally
        {
            if (color != IntPtr.Zero) DeleteObject(color);
            if (mask != IntPtr.Zero) DeleteObject(mask);
        }
    }

    [DllImport("user32.dll")] static extern IntPtr CreateIconIndirect(ref ICONINFO icon);

    public void Dispose()
    {
        if (added)
        {
            var d = Data(0);
            Shell_NotifyIcon(NIM_DELETE, ref d);
            added = false;
        }
        window.Closing -= OnWindowClosing;
        source?.RemoveHook(WndProc);
        if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
    }
}
