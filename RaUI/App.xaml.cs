using System.Windows;
using RaCore.Model;
using RaUI.View;

namespace RaUI;

public partial class App : Application
{
    public static AppState State { get; private set; }
    public static MainWindow Shell { get; private set; }

    /// <summary>Headless render check: do not register raw input or start the telemetry timer.</summary>
    public static bool Headless { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // defaults: work on the official file unless the user pointed us elsewhere
        string path = @"D:\RawAccel\settings.json";
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

        Shell = new MainWindow();
        if (minimized) Shell.WindowState = WindowState.Minimized;
        Shell.Show();

        if (writeOnStart) _ = State.WriteToDriverAsync();
    }

    void DumpUi(string outPng, string[] args)
    {
        int page = 0;
        var pi = System.Array.FindIndex(args, a => a == "--page");
        if (pi >= 0 && pi + 1 < args.Length) int.TryParse(args[pi + 1], out page);

        var w = new MainWindow
        {
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = -20000,
            Top = -20000,
            Width = 1220,
            Height = 780,
        };
        w.GoToPage(page);
        w.Measure(new Size(w.Width, w.Height));
        w.Arrange(new Rect(0, 0, w.Width, w.Height));
        w.UpdateLayout();
        w.Show();   // offscreen + not activated: needed for WPF to complete layout/render
        w.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);

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

    /// <summary>Renders the fullscreen test window offscreen with a fake pointer position.</summary>
    void DumpFlick(string outPng)
    {
        const double cx = 960, cy = 700;
        var cfg = new FlickWindow.Config
        {
            AngleDeg = 15,
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
}
