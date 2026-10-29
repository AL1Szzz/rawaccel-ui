using System.Windows;
using RaCore;
using RaCore.Model;
using RaUI.View;

namespace RaUI;

public partial class App : Application
{
    public static AppState State { get; private set; }
    public static MainWindow Shell { get; private set; }
    public static TrayIcon Tray { get; set; }

    /// <summary>Only the tray menu / 退出 sets this; closing the window is otherwise "hide to tray".</summary>
    public static bool IsExiting { get; private set; }

    /// <summary>Offscreen render check (--dump-ui / --dump-flick / --dump-tray): no window on screen, no timer.</summary>
    public static bool Headless { get; private set; }

    public static void ExitApplication()
    {
        IsExiting = true;
        Tray?.SetVisible(false);
        Tray?.Dispose();
        Tray = null;
        Shell?.Close();
        Current?.Shutdown();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // defaults: work on the official file wherever it is installed, else our own data folder
        string path = RaCore.SettingsFile.Default();
        bool minimized = false, writeOnStart = false;

        for (int i = 0; i < e.Args.Length; i++)
        {
            switch (e.Args[i])
            {
                case "--settings" when i + 1 < e.Args.Length: path = e.Args[++i]; break;
                case "--minimized": minimized = true; break;
                case "--write": writeOnStart = true; break;
                default:
                    if (!e.Args[i].StartsWith("--") && e.Args[i].EndsWith(".json")) path = e.Args[i];
                    break;
            }
        }

        State = new AppState(path);
        State.StartupLoad();

        // headless visual check of the fullscreen test window (offscreen, no display change)
        int flickIdx = System.Array.FindIndex(e.Args, a => a == "--dump-flick");
        if (flickIdx >= 0)
        {
            Headless = true;
            DumpFlick(flickIdx + 1 < e.Args.Length ? e.Args[flickIdx + 1] : @"C:\temp\flick.png");
            Shutdown();
            return;
        }

        // headless visual check of the drill window, one mode per run (--dump-drill out.png --kind 1)
        int drillIdx = System.Array.FindIndex(e.Args, a => a == "--dump-drill");
        if (drillIdx >= 0)
        {
            Headless = true;
            DumpDrill(drillIdx + 1 < e.Args.Length ? e.Args[drillIdx + 1] : @"C:\temp\drill.png",
                      IntArg(e.Args, "--kind"));
            Shutdown();
            return;
        }

        // headless visual check of the calibration dial (--dump-calib out.png)
        int calibIdx = System.Array.FindIndex(e.Args, a => a == "--dump-calib");
        if (calibIdx >= 0)
        {
            Headless = true;
            DumpCalib(calibIdx + 1 < e.Args.Length ? e.Args[calibIdx + 1] : @"C:\temp\calib.png");
            Shutdown();
            return;
        }

        // driver round trip for the blind comparison (--calib-push-check): push one candidate, read it
        // back, restore, and prove the bytes came back. This is the only check that exercises the one
        // path that can leave somebody's mouse on a curve they never chose.
        int pushIdx = System.Array.FindIndex(e.Args, a => a == "--calib-push-check");
        if (pushIdx >= 0)
        {
            Headless = true;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = CalibPushCheckAsync();
            return;
        }

        // headless visual check of the blind-comparison report (--dump-calib-report out.png)
        int reportIdx = System.Array.FindIndex(e.Args, a => a == "--dump-calib-report");
        if (reportIdx >= 0)
        {
            Headless = true;
            DumpCalibReport(reportIdx + 1 < e.Args.Length ? e.Args[reportIdx + 1] : @"C:\temp\report.png",
                            StrArg(e.Args, "--stage"));
            Shutdown();
            return;
        }

        // live raw-input check: open the dial for N seconds and report what the mouse delivered.
        // Offscreen renders cannot prove WM_INPUT is wired up, and a dead dial looks identical to
        // "the mouse did not move", so this one has to run for real.
        int liveIdx = System.Array.FindIndex(e.Args, a => a == "--calib-live");
        if (liveIdx >= 0)
        {
            Headless = true;
            CalibLive(liveIdx + 1 < e.Args.Length && int.TryParse(e.Args[liveIdx + 1], out var secs) ? secs : 5);
            Shutdown();
            return;
        }

        // headless visual check: render offscreen to a PNG and quit (never steals focus)
        int dumpIdx = System.Array.FindIndex(e.Args, a => a == "--dump-ui");
        if (dumpIdx >= 0)
        {
            Headless = true;
            string outPng = dumpIdx + 1 < e.Args.Length ? e.Args[dumpIdx + 1] : @"C:\temp\raui.png";
            DumpUi(outPng, e.Args);
            Shutdown();
            return;
        }

        // headless check of the tray plumbing: build the HICON, add the icon, report, remove it.
        // This is the only way to verify the notify-icon path without touching the taskbar by hand.
        if (System.Array.Exists(e.Args, a => a == "--dump-tray"))
        {
            Headless = true;
            DumpTray();
            return;
        }

        // Render the .ico that gets embedded in the exe. Done from here rather than with an image
        // editor so the file icon is guaranteed to be the same drawing as the tray and the window.
        int icoIdx = System.Array.FindIndex(e.Args, a => a == "--dump-ico");
        if (icoIdx >= 0)
        {
            Headless = true;
            string outIco = icoIdx + 1 < e.Args.Length ? e.Args[icoIdx + 1] : @"RaUI\raui.ico";
            try
            {
                AppArt.ExportIco(outIco);
                System.Console.WriteLine($"icon: {new System.IO.FileInfo(outIco).Length} bytes, " +
                                         $"frames {string.Join(",", AppArt.IcoSizes)} -> {outIco}");
            }
            catch (Exception ex) { System.Console.WriteLine("icon FAILED: " + ex.Message); }
            Shutdown();
            return;
        }

        // headless check of the login-autostart round trip, using the real registry code path
        if (System.Array.Exists(e.Args, a => a == "--autostart-check"))
        {
            Headless = true;
            AutostartCheck();
            Current?.Shutdown();
            return;
        }

        // Set login autostart from here rather than with reg.exe: Enable() builds the command
        // string, so this and the Settings page can never drift apart into two different recipes.
        if (System.Array.Exists(e.Args, a => a == "--autostart-enable"))
        {
            Headless = true;
            Autostart.Enable(writeAtLogon: true, minimized: true, settingsPath: State.SettingsPath);
            Say("已注册登录自启: " + Autostart.CurrentCommand());
            Say("  程序路径存在: " + (System.IO.File.Exists(Autostart.ExePath) ? "是" : "否！自启会失败"));
            if (!System.IO.File.Exists(Autostart.ExePath)) Autostart.Disable();
            Current?.Shutdown();
            return;
        }
        if (System.Array.Exists(e.Args, a => a == "--autostart-disable"))
        {
            Headless = true;
            Autostart.Disable();
            Say("已取消登录自启: " + (Autostart.IsEnabled ? "仍然存在（失败）" : "确认已移除"));
            Current?.Shutdown();
            return;
        }
        if (System.Array.Exists(e.Args, a => a == "--autostart-status"))
        {
            Headless = true;
            Say("登录自启: " + (Autostart.CurrentCommand() ?? "未注册"));
            Current?.Shutdown();
            return;
        }

        Shell = new MainWindow
        {
            ShowActivated = !minimized,
            WindowState = minimized ? WindowState.Minimized : WindowState.Normal,
        };
        Shell.Show();

        if (AppState.Prefs.TrayEnabled)
        {
            Tray = new TrayIcon(Shell, State);
            if (Tray.LastError is { Length: > 0 } err) State.Status = err;
            if (minimized && AppState.Prefs.StartHidden) Shell.Hide();
        }
        else if (minimized) State.Status = "以最小化启动（托盘图标已关闭，关掉窗口就退出）";

        // Login writes go through the same safe path as the button, but with the extra guard:
        // if the settings file could not be read, what the UI is holding is our own default
        // document and writing it would replace a working curve.
        if (writeOnStart) _ = State.WriteToDriverAsync(fromLogon: true);
    }

    void DumpTray()
    {
        var w = new MainWindow
        {
            ShowActivated = false,
            // WS_EX_TOOLWINDOW (ShowInTaskbar=false) is NOT how the app really runs, and the shell
            // is pickier about windows it did not see being created normally - mirror production
            Left = -20000,
            Top = -20000,
            Width = 800,
            Height = 600,
        };
        w.Show();
        w.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);

        var tray = new TrayIcon(w, State);
        string icon = tray.ArtPath;
        bool ok = tray.LastError.Length == 0;
        System.Console.WriteLine($"tray add: {(ok ? "Shell_NotifyIcon accepted" : "FAILED - " + tray.LastError)}");
        System.Console.WriteLine($"tray tip: {tray.TipText}");
        System.Console.WriteLine($"tray art: {icon}");
        System.Console.WriteLine($"tray menu render: {tray.RenderMenuPng() ?? "FAILED"}");

        // give the shell a moment to paint it, then take the icon back down
        w.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        System.Threading.Thread.Sleep(400);
        tray.Dispose();
        w.Close();
        Shutdown();
    }

    /// <summary>
    /// Exercises the real HKCU\...\Run round trip and puts the registry back exactly as it was,
    /// so "自启动是否完善" is a measured answer instead of a code-reading guess.
    /// </summary>
    static void AutostartCheck()
    {
        string before = Autostart.CurrentCommand();
        Say("登录前状态: " + (before == null ? "未注册自启" : "已注册: " + before));
        Say("本程序路径: " + Autostart.ExePath + (System.IO.File.Exists(Autostart.ExePath) ? " (存在)" : " (不存在！自启会失败)"));

        Autostart.Enable(writeAtLogon: true, minimized: true, settingsPath: State.SettingsPath);
        string after = Autostart.CurrentCommand();
        Say("Enable 之后: " + after);
        Say("  IsEnabled = " + Autostart.IsEnabled);
        Say("  含 --minimized = " + (after?.Contains("--minimized") ?? false));
        Say("  含 --write = " + (after?.Contains("--write") ?? false));
        Say("  指向自身 = " + (after?.Contains(Autostart.ExePath) ?? false));
        string pinned = Autostart.SettingsPathOf(after);
        Say("  登录时会用哪个设置文件 = " + (pinned ?? "(没钉死，下次登录重新猜路径)") +
            (pinned != null && pinned == State.SettingsPath ? "  · 与当前一致 (OK)" :
             pinned != null ? $"  *** 与当前 {State.SettingsPath} 不一致 ***" : ""));

        Autostart.Disable();
        Say("Disable 之后: " + (Autostart.CurrentCommand() ?? "已移除 (OK)"));

        Autostart.WriteRaw(before);                       // restore whatever the user had
        string restored = Autostart.CurrentCommand();
        Say("还原: " + (restored == before ? "与测试前一致 (OK)" : "*** 不一致 *** 现在 = " + restored));

        var legacy = Autostart.LegacyWriterShortcut();
        Say("官方 writer.exe 开机快捷方式: " + (legacy is null ? "无" : legacy.Value.path + " 参数 " + legacy.Value.args));
    }

    static void Say(object line) => System.Console.WriteLine(line);

    void DumpUi(string outPng, string[] args)
    {
        int page = 0;
        var pi = System.Array.FindIndex(args, a => a == "--page");
        if (pi >= 0 && pi + 1 < args.Length) int.TryParse(args[pi + 1], out page);

        // --height: a card below the fold can only be checked by rendering a taller window
        int dumpHeight = IntArg(args, "--height");
        if (dumpHeight <= 0) dumpHeight = 780;

        var w = new MainWindow
        {
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = -20000,
            Top = -20000,
            Width = 1220,
            Height = dumpHeight,
        };
        w.GoToPage(page);
        w.Measure(new Size(w.Width, w.Height));
        w.Arrange(new Rect(0, 0, w.Width, w.Height));
        w.UpdateLayout();
        w.Show();   // offscreen + not activated: needed for WPF to complete layout/render
        w.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);

        // --scroll N: park the page's scroll viewer so a card below the fold can be rendered too
        var si = System.Array.FindIndex(args, a => a == "--scroll");
        if (si >= 0 && si + 1 < args.Length && double.TryParse(args[si + 1], out var offset))
        {
            if (FirstScrollViewer(w) is { } sv)
            {
                sv.ScrollToVerticalOffset(offset);
                w.UpdateLayout();
                w.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            }
        }

        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)w.Width, (int)w.Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(w);

        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        var dir = System.IO.Path.GetDirectoryName(outPng);
        if (dir != null && !System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
        using (var fs = System.IO.File.Create(outPng)) enc.Save(fs);

        w.Close();
        System.Console.WriteLine("dumped " + outPng);
    }

    static System.Windows.Controls.ScrollViewer? FirstScrollViewer(System.Windows.DependencyObject root)
    {
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.ScrollViewer sv) return sv;
            if (FirstScrollViewer(child) is { } found) return found;
        }
        return null;
    }

    /// <summary>Renders the fullscreen test window offscreen with a fake pointer position.</summary>
    void DumpFlick(string outPng)
    {
        const double cx = 960, cy = 700;
        var cfg = new FlickWindow.Config
        {
            DistancePx = 420,
            RadiusPx = 26,
            TargetCount = 8,
            Mode = RaCore.TestMode.Balls,
            Trials = 30,
            Seconds = 60,
            DirectionMode = 0,
            Seed = 20261020,
            CrossArm = 9,
            CrossGap = 3,
            CrossColor = "#FFFFFFFF",
            CrossDot = true,
            Resolution = null,
            DebugCursorPx = (cx, cy),
        };

        var w = new FlickWindow(cfg)
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            Topmost = false,
            Left = -30000,
            Top = -30000,
        };
        w.Show();

        // debug mode places the batch synchronously (no countdown, no timer, no display change)
        w.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        w.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);

        int pw = (int)w.ActualWidth, ph = (int)w.ActualHeight;
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(pw, ph, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(w);

        // whole window, not a crop: multi-ball layout must be checked for overlaps and off-screen balls
        System.Windows.Media.Imaging.BitmapSource crop = rtb;

        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(crop));
        var dir = System.IO.Path.GetDirectoryName(outPng);
        if (dir != null && !System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
        using (var fs = System.IO.File.Create(outPng)) enc.Save(fs);

        w.Close();
        System.Console.WriteLine($"dumped {outPng} (window {pw}x{ph}, cursor faked at {cx},{cy})");
    }

    static int IntArg(string[] args, string name)
    {
        int i = System.Array.FindIndex(args, a => a == name);
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : 0;
    }

    static string StrArg(string[] args, string name)
    {
        int i = System.Array.FindIndex(args, a => a == name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : "";
    }

    /// <summary>Offscreen render of one drill mode: no countdown, no timer, no display change.</summary>
    void DumpDrill(string outPng, int kind)
    {
        var cfg = new DrillWindow.Config
        {
            Kind = (RaCore.DrillKind)Math.Clamp(kind, 0, 8),
            Seconds = 60,
            RadiusPx = 22,
            Alive = RaCore.DrillInfo.DefaultAlive((RaCore.DrillKind)Math.Clamp(kind, 0, 8)),
            RepTarget = RaCore.DrillInfo.DefaultReps((RaCore.DrillKind)Math.Clamp(kind, 0, 8)),
            CrossArm = 9,
            CrossGap = 3,
            CrossColor = "#FFFFFFFF",
            CrossDot = true,
            Resolution = null,
            DebugCursorPx = (960, 700),
        };

        var w = new DrillWindow(cfg)
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            Topmost = false,
            Left = -30000,
            Top = -30000,
        };
        w.Show();
        Capture(w, outPng, $"kind {cfg.Kind}");
    }

    /// <summary>Offscreen render of the calibration dial at a fixed angle.</summary>
    void DumpCalib(string outPng)
    {
        var cfg = new CalibrationWindow.Config { Sens = 1.5, Dpi = 600, TargetCm360 = 30, DebugStatic = true };
        var w = new CalibrationWindow(cfg)
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            Topmost = false,
            Left = -30000,
            Top = -30000,
        };
        w.Show();
        Capture(w, outPng, "calibration");
    }

    /// <summary>
    /// End-to-end check of the temporary curve push: capture, push a candidate, read it back,
    /// restore, and compare the driver's bytes with what was there at the start.
    /// </summary>
    async Task CalibPushCheckAsync()
    {
        var push = new CurvePush();
        byte[] before;
        try { before = RaCore.RaDevice.Read(); }
        catch (Exception ex)
        {
            System.Console.WriteLine("driver unreadable: " + ex.Message);
            Shutdown();
            return;
        }

        if (!push.Capture()) { System.Console.WriteLine("capture failed: " + push.Error); Shutdown(); return; }

        var live = State.SelectedProfile;
        if (live == null) { System.Console.WriteLine("no profile selected"); Shutdown(); return; }

        var cands = AccelCandidates.Build(live, new[] { 1.5 }, includeNoAccel: false);
        string run = AccelCandidates.KnobName(live.X.Mode);

        bool pushed = await push.PushAsync(cands[0].Profile);
        var afterPush = RaCore.RaDevice.Read();
        System.Console.WriteLine(pushed
            ? $"push ok: {run} changed and read back identical"
            : "push FAILED: " + push.Error);
        System.Console.WriteLine(!afterPush.AsSpan().SequenceEqual(before)
            ? "the driver really did change (the candidate was not a no-op)"
            : "WARNING: the driver bytes did not change - this check proved nothing");

        bool restored = await push.RestoreAsync();
        var afterRestore = RaCore.RaDevice.Read();
        System.Console.WriteLine(restored && afterRestore.AsSpan().SequenceEqual(before)
            ? "restore ok: byte-for-byte the settings from before the check"
            : "RESTORE FAILED: " + (push.RestoreFailure ?? "bytes differ") );

        push.Dispose();
        Shutdown();
    }

    /// <summary>Render the blind-comparison report with invented rounds and save it.</summary>
    static void DumpCalibReport(string outPng, string stage)
    {
        var w = new CalibrationSessionWindow(new CalibrationSessionWindow.Options
        {
            DebugReport = stage.Length == 0 || stage == "report",
            DebugStage = stage,
        });
        w.Show();
        Capture(w, outPng, stage.Length > 0 ? stage : "report");
        w.Close();
    }

    /// <summary>Open the calibration dial for a few seconds and print what raw input delivered.</summary>
    void CalibLive(int seconds)
    {
        var w = new CalibrationWindow(new CalibrationWindow.Config { Sens = 1.5, Dpi = 600, TargetCm360 = 30 });
        w.Show();
        w.Activate();

        var until = DateTime.UtcNow.AddSeconds(Math.Clamp(seconds, 1, 30));
        while (DateTime.UtcNow < until)
        {
            w.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            System.Threading.Thread.Sleep(16);
        }

        string err = w.RawError;
        System.Console.WriteLine(err.Length > 0
            ? "raw input FAILED: " + err
            : $"raw input ok: counts={w.RawCounts} packets={w.RawPackets}");
        w.Close();
    }

    static void Capture(Window w, string outPng, string what)
    {
        w.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        w.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);

        int pw = (int)w.ActualWidth, ph = (int)w.ActualHeight;
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(pw, ph, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(w);

        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        var dir = System.IO.Path.GetDirectoryName(outPng);
        if (dir != null && !System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
        using (var fs = System.IO.File.Create(outPng)) enc.Save(fs);

        w.Close();
        System.Console.WriteLine($"dumped {outPng} (window {pw}x{ph}, {what})");
    }
}
