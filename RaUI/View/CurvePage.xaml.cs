using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using RaCore;
using RaCore.Model;

namespace RaUI.View;

public partial class CurvePage : UserControl
{
    enum Kind { Double, Bool, Enum }

    /// <summary>
    /// One editable number. <paramref name="For"/> lists the modes that actually read it, because
    /// the driver ignores most of these in most modes: showing all seventeen in every mode is how a
    /// settings panel becomes unreadable - the player cannot tell which four numbers matter for the
    /// curve they picked. Empty means "every mode".
    /// </summary>
    record AxisRow(string Label, string Hint, Kind Kind, string[]? Options, string[]? Names, string For,
                   Func<AccelDto, object> Get, Action<AccelDto, object> Set, bool RebuildsForm = false);

    record ProfRow(string Label, string Hint, Kind Kind, string[]? Options, string[]? Names,
                   Func<ProfileDto, object> Get, Action<ProfileDto, object> Set);

    static readonly string[] Modes = { "classic", "jump", "natural", "synchronous", "power", "lut", "noaccel" };
    static readonly string[] ModeNames =
    {
        "经典 classic", "跳跃 jump", "自然 natural", "同步 synchronous", "幂函数 power", "查表 lut", "不加速 noaccel",
    };
    static readonly string[] CapModes = { "in_out", "input", "output" };
    static readonly string[] CapNames = { "输入定输出 in_out", "按输入速度 input", "按输出速度 output" };

    // The driver reads these per mode (common/accel-*.hpp upstream). Anything not listed for the
    // current mode is hidden rather than shown greyed out: a disabled row still costs a line and
    // still invites the question "why can't I change this".
    static readonly AxisRow[] AxisRows =
    {
        new("模式", "每个轴可以单独选模式。改这里，下面只留下这个模式真正会用到的参数。", Kind.Enum, Modes, ModeNames, "",
            d => d.Mode, (d, v) => d.Mode = (string)v, RebuildsForm: true),

        new("按增益", "曲线形状画在哪个图上：勾上 = 作用在增益（斜率）上，不勾 = 作用在灵敏度上。" +
                     "classic/power 两者能得到同一条曲线；natural/jump/synchronous 勾上才是它们本来的形状。默认保持勾上。",
            Kind.Bool, null, null, "classic,jump,natural,synchronous,power,lut",
            d => d.Gain, (d, v) => d.Gain = (bool)v),

        // classic
        new("起始阈值", "手速低于这个值完全不加速，超过才开始。单位 counts/ms：600 DPI 下 1 c/ms ≈ 2.4 cm/s，" +
                     "普通人慢慢定位大约 2~5。填 0 = 第一下就开始加速。",
            Kind.Double, null, null, "classic,natural", d => d.InputOffset, (d, v) => d.InputOffset = (double)v),
        new("加速强度", "classic 的核心。速度先乘它再取指数：0.06 是常见起点，调大更猛、调小更温和。" +
                     "想整体更快又不想动曲线形状，去改上面的输出 DPI。",
            Kind.Double, null, null, "classic", d => d.Acceleration, (d, v) => d.Acceleration = (double)v),
        new("指数", "曲线的陡峭程度。2 = 常见的 Quake 手感（增益线性上升）；调大 → 起步慢、后段陡；调小 → 整体更平。",
            Kind.Double, null, null, "classic", d => d.ExponentClassic, (d, v) => d.ExponentClassic = (double)v),

        // caps, shared by classic and power (natural/synchronous do not read them at all)
        new("封顶方式", "封顶从哪一侧算。in_out（按输入定输出）是默认也是绝大多数人该用的；" +
                     "input / output 只在你要精确控制'封顶时输出多快'时才不同。",
            Kind.Enum, CapModes, CapNames, "classic,power", d => d.CapMode, (d, v) => d.CapMode = (string)v),
        new("封顶速度", "从多快的手速开始封顶，单位 counts/ms。0 = 这一轴不封顶 —— 你现在垂直就是 0，" +
                     "所以垂直会一直涨。想让它也停住，就填一个速度再配下面的倍数。",
            Kind.Double, null, null, "classic,power", d => d.Cap.X, (d, v) => d.Cap.X = (double)v),
        new("封顶倍数", "封顶之后最多几倍灵敏度。1.97 = 再快也不会超过 1.97 倍；1.0 = 等于不封顶但限死 1 倍。" +
                     "只有在封顶速度 > 0 时才有意义。",
            Kind.Double, null, null, "classic,power", d => d.Cap.Y, (d, v) => d.Cap.Y = (double)v),

        // jump
        new("跳跃阈值", "手速超过这个值，灵敏度直接换到另一套。单位 counts/ms。跳跃模式只有这两个数加平滑，没有别的东西。",
            Kind.Double, null, null, "jump", d => d.Cap.X, (d, v) => d.Cap.X = (double)v),
        new("跳跃后倍数", "跳过去之后变成几倍。1.5 = 快速甩枪时灵敏度 1.5 倍，慢速定位保持 1 倍。",
            Kind.Double, null, null, "jump", d => d.Cap.Y, (d, v) => d.Cap.Y = (double)v),
        new("平滑", "0 = 瞬间切换（阈值附近会有一点点顿挫）；1 = 平滑过渡成一个 S 形。jump 常用 0~0.5；synchronous 官方建议 0.5。",
            Kind.Double, null, null, "jump,synchronous", d => d.Smooth, (d, v) => d.Smooth = (double)v),

        // natural
        new("衰减率", "natural 的上升快慢：越大越快逼近上限。0.1 是默认起点。",
            Kind.Double, null, null, "natural", d => d.DecayRate, (d, v) => d.DecayRate = (double)v),
        new("上限", "natural 最终能到的最大倍数。1.5 = 最快时比现在快 50%，慢速时仍是 1 倍。",
            Kind.Double, null, null, "natural", d => d.Limit, (d, v) => d.Limit = (double)v),

        // synchronous
        new("同步速度", "synchronous 的锚点：手速正好等于它时灵敏度是 1；比它快就加速，比它慢就减速。" +
                     "这是这个模式最重要的一个数，先把它调到'平时瞄准的手速'。单位 counts/ms。",
            Kind.Double, null, null, "synchronous", d => d.SyncSpeed, (d, v) => d.SyncSpeed = (double)v),
        new("动力", "总变化幅度：曲线从 1/动力 走到 动力。1.5 = 最慢 0.67 倍、最快 1.5 倍。",
            Kind.Double, null, null, "synchronous", d => d.Motivity, (d, v) => d.Motivity = (double)v),
        new("伽马", "过渡的快慢（相当于 power 的指数）。越大越快进入加减速，1 是默认。",
            Kind.Double, null, null, "synchronous", d => d.Gamma, (d, v) => d.Gamma = (double)v),

        // power
        new("缩放", "power 的速度系数，对应 CS:GO 的 m_customaccel_scale。它是线性的，不是百分比。",
            Kind.Double, null, null, "power", d => d.Scale, (d, v) => d.Scale = (double)v),
        new("幂次", "power 的指数。0.05 约等于 CS:GO 默认的 1.05（非常缓），越大越陡。",
            Kind.Double, null, null, "power", d => d.ExponentPower, (d, v) => d.ExponentPower = (double)v),
        new("输出起点", "power 曲线从 0 开始，这个值让它在 0 手速时也有一定倍数（输出端的偏移）。" +
                     "不用 CS 那套就别动，留 0。",
            Kind.Double, null, null, "power", d => d.OutputOffset, (d, v) => d.OutputOffset = (double)v),
    };

    // ---------- whole-profile parameters ----------

    static readonly ProfRow[] BenchRows =
    {
        new("输出 DPI", "输出基准 DPI，它和「鼠标真实 DPI」一起决定基础倍率：\n" +
                     "    倍率 = 输出 DPI ÷ 鼠标真实 DPI（设备页那个；没填则按 1000 算）\n" +
                     "所以同样填 1000：设备 DPI 空着是 1.00×，填 600 就变成 1.67× —— 等于把 600 的鼠标当 1000 用。\n" +
                     "想 1:1（不额外放大）就让两个数相等。改它会整体变快或变慢，改之前先把游戏内灵敏度按同样比例调回去。",
            Kind.Double, null, null, p => p.OutputDpi, (p, v) => p.OutputDpi = (double)v),

        new("垂直比例 Y/X", "垂直方向整体 ×N，相对水平。1.25 = 上下移动时准星多走 25%（压枪省手），水平完全不变。\n" +
                     "为什么没有「水平比例」？因为水平就是基准 1，这个数描述的是垂直相对水平。",
            Kind.Double, null, null, p => p.YxRatio, (p, v) => p.YxRatio = (double)v),

        new("向左倍数 L/R", "只作用于「向左」的那一半，向右仍是 1。给左右手不对称的人用。\n" +
                     "为什么只有向左没有向右？向右是基准 1，这个数是相对它的额外倍数。不知道要不要，就留 1。",
            Kind.Double, null, null, p => p.LrRatio, (p, v) => p.LrRatio = (double)v),

        new("向上倍数 U/D", "只作用于「向上」的那一半，向下仍是 1。注意它和「垂直比例」不是一回事：" +
                     "垂直比例让上下一起变，这个只加上不加上。留 1 即可。",
            Kind.Double, null, null, p => p.UdRatio, (p, v) => p.UdRatio = (double)v),
    };

    static readonly ProfRow[] AdvancedRows =
    {
        new("整体 / 分量", "勾上 = 用「合速度」一次性作用于整个方向（whole）；不勾 = 水平、垂直各算各的（by component）。" +
                       "分轴曲线必须不勾。改之前想清楚：整体模式下驱动根本不读垂直那套参数。",
            Kind.Bool, null, null, p => p.Speed.Whole, (p, v) => p.Speed.Whole = (bool)v),
        new("lp 范数", "整体模式下「合速度」怎么算：2 = 欧氏距离（圆的），≥16 取两轴里最大的那个。一般 2。",
            Kind.Double, null, null, p => p.Speed.LpNorm, (p, v) => p.Speed.LpNorm = (double)v),
        new("域权重 X", "domain_weights.x：拉伸水平方向的输入速度轴。只有做各向异性实验时才用。",
            Kind.Double, null, null, p => p.Domain.X, (p, v) => p.Domain.X = (double)v),
        new("域权重 Y", "domain_weights.y：同上，垂直方向。",
            Kind.Double, null, null, p => p.Domain.Y, (p, v) => p.Domain.Y = (double)v),
        new("值域 X", "range_weights.x：把水平的加速幅度按比例缩放（1 = 原样，0.5 = 加速效果减半）。",
            Kind.Double, null, null, p => p.Range.X, (p, v) => p.Range.X = (double)v),
        new("值域 Y", "range_weights.y：垂直的加速幅度缩放；整体模式下会按角度插值。",
            Kind.Double, null, null, p => p.Range.Y, (p, v) => p.Range.Y = (double)v),
        new("旋转", "先把输入向量旋转这么多度再加速。给斜着握鼠标的人用，一般人留 0。",
            Kind.Double, null, null, p => p.Rotation, (p, v) => p.Rotation = (double)v),
        new("角度吸附", "把输入方向吸附到最近的 N 度网格上。会让斜向移动变得一格一格的，一般人留 0。",
            Kind.Double, null, null, p => p.Snap, (p, v) => p.Snap = (double)v),
        new("输入速度上限", "超过这个手速的输入被夹住（0 = 关）。防止极端甩枪时曲线飞出可控范围，一般人不用。",
            Kind.Double, null, null, p => p.InputSpeedCap, (p, v) => p.InputSpeedCap = (double)v),
        new("输入平滑", "EMA 半衰期（ms）。只影响「判定手速有多快」，不改光标输出，所以不会加手感延迟。0 = 不平滑。",
            Kind.Double, null, null, p => p.Speed.InputHalflife, (p, v) => p.Speed.InputHalflife = (double)v),
        new("比例平滑", "EMA 半衰期（ms）。同上，只影响加速判定的平滑程度。0 = 不平滑。",
            Kind.Double, null, null, p => p.Speed.ScaleHalflife, (p, v) => p.Speed.ScaleHalflife = (double)v),
        new("输出平滑", "这才是传统意义上的「鼠标平滑」：会真的让光标变黏并加延迟。除非你明确想要，否则保持 0。",
            Kind.Double, null, null, p => p.Speed.OutputHalflife, (p, v) => p.Speed.OutputHalflife = (double)v),
    };

    readonly List<FrameworkElement> editors = new();
    readonly Dictionary<TextBox, string> shownText = new();
    bool suppress;

    public CurvePage()
    {
        InitializeComponent();
        DataContext = App.State;

        TxtChartDpi.Text = AppState.Prefs.ChartDpi.ToString("0");
        TxtChartPoll.Text = AppState.Prefs.ChartPollRate.ToString();
        // one rebuild for the two boxes, and loading prefs must not rewrite the prefs file
        App.State.ApplyChartScale(AppState.Prefs.ChartDpi, AppState.Prefs.ChartPollRate, savePrefs: false);

        Chart.SetBinding(Controls.CurveChart.SeriesProperty,
            new Binding(nameof(AppState.Series)) { Source = App.State });
        Chart.SetBinding(Controls.CurveChart.MarkersProperty,
            new Binding(nameof(AppState.Markers)) { Source = App.State });
        Chart.SetBinding(Controls.CurveChart.ZonesProperty,
            new Binding(nameof(AppState.Zones)) { Source = App.State });
        Chart.SetBinding(Controls.CurveChart.XLabelProperty,
            new Binding(nameof(AppState.XAxisLabel)) { Source = App.State });
        Chart.SetBinding(Controls.CurveChart.YLabelProperty,
            new Binding(nameof(AppState.YAxisLabel)) { Source = App.State });

        BuildForm();
        UpdateEditingState();

        App.State.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppState.SelectedProfile) or nameof(AppState.CanWrite) or nameof(AppState.Busy))
                Dispatcher.Invoke(UpdateEditingState);
            if (e.PropertyName is nameof(AppState.SelectedProfile) or nameof(AppState.Series))
            {
                // the visible rows depend on the selected profile's modes, not just their values
                Dispatcher.Invoke(() => { BuildForm(); RefreshValues(); RefreshMeaning(); });
            }
        };

        Loaded += (_, _) => { UpdateEditingState(); RefreshValues(); RefreshMeaning(); };
    }

    void UpdateEditingState()
    {
        ParamCard.IsEnabled = !App.State.Busy && App.State.SelectedProfile != null;
    }

    // ---------- which rows the current mode actually uses ----------

    string[] ModesNow()
    {
        var p = App.State.SelectedProfile;
        if (p == null) return new[] { "classic" };
        string x = (p.X.Mode ?? "").Trim().ToLowerInvariant();
        string y = (p.Y.Mode ?? "").Trim().ToLowerInvariant();
        return x == y ? new[] { x } : new[] { x, y };
    }

    static bool Applies(AxisRow r, string[] modes) =>
        r.For.Length == 0 || modes.Any(m => r.For.Split(',').Contains(m, StringComparer.OrdinalIgnoreCase));

    static string ModeBlurb(string[] modes)
    {
        if (modes.Length > 1)
            return "两轴模式不同，下面显示两个模式各自会用到的参数（每一行只有对应那轴的格子有意义）。";
        return modes[0] switch
        {
            "classic" => "经典加速（Quake 系）：速度乘个系数再取指数。上面四个数就够调了。",
            "jump" => "跳跃：慢速一套灵敏度、快速另一套，中间可选平滑过渡。只有三个数。",
            "natural" => "自然：从 1 倍开始逐渐逼近上限，曲线是凹的，小幅定位更稳。",
            "synchronous" => "同步：围绕一个锚点做对数对称的加减速 —— 慢于锚点会变慢，快于锚点会变快。最难调也最讲道理。",
            "power" => "幂函数（CS:GO 那套）：从 0 开始按幂次上升，适合复刻游戏内加速。",
            "lut" => "查表：驱动按你自己给的曲线插值，别的参数基本不参与。",
            "noaccel" => "不加速：1:1。这一轴的曲线参数全部无效，只剩上面的倍率还起作用。",
            _ => "",
        };
    }

    /// <summary>The DPI the driver normalises input against, per device. Falls back to the template
    /// entry, and to 0 when neither is filled - which changes what "output DPI" even means.</summary>
    static double DeviceDpi()
    {
        var doc = App.State.Doc;
        foreach (var d in doc.Devices)
        {
            if (d.Config?.Disable == true) continue;
            if (d.Config is { Dpi: > 0 }) return d.Config.Dpi;
        }
        return doc.DefaultDeviceConfig.Dpi;
    }

    // ---------- form ----------

    void BuildForm()
    {
        ParamPanel.Children.Clear();
        editors.Clear();
        shownText.Clear();

        var modes = ModesNow();

        ParamPanel.Children.Add(Section("灵敏度基准",
            "只改倍率，不动曲线形状。全软件有三处 DPI，只有这里的「输出 DPI」会改变鼠标快慢；" +
            "鼠标悬停每个参数名都有解释。"));
        foreach (var r in BenchRows) ParamPanel.Children.Add(Row(r));
        ParamPanel.Children.Add(BenchNote());

        ParamPanel.Children.Add(Section("曲线参数（水平 X / 垂直 Y）", ModeBlurb(modes)));

        var header = new Grid { Margin = new Thickness(0, 2, 0, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        AddCell(header, 0, Label("参数", null, true));
        AddCell(header, 1, Label("水平 X", null, true));
        AddCell(header, 2, Label("垂直 Y", null, true));
        ParamPanel.Children.Add(header);

        foreach (var r in AxisRows.Where(x => Applies(x, modes)))
        {
            var g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
            AddCell(g, 0, Label(r.Label, r.Hint, false));
            AddCell(g, 1, MakeEditor(r.Kind, r.Options, r.Names,
                v => r.Set(App.State.SelectedProfile.X, v), () => r.Get(App.State.SelectedProfile.X), r.RebuildsForm));
            AddCell(g, 2, MakeEditor(r.Kind, r.Options, r.Names,
                v => r.Set(App.State.SelectedProfile.Y, v), () => r.Get(App.State.SelectedProfile.Y), r.RebuildsForm));
            ParamPanel.Children.Add(g);
        }

        ParamPanel.Children.Add(Advanced());
    }

    /// <summary>
    /// The rest of the document, folded away. These are real settings the driver reads, but they are
    /// not part of "make my mouse feel like this": they are units, smoothing and direction tricks
    /// that a player changes once in a lifetime, if ever. Folding them keeps the four numbers that
    /// matter visible while leaving the rest reachable.
    /// </summary>
    FrameworkElement Advanced()
    {
        var inner = new StackPanel();
        foreach (var r in AdvancedRows) inner.Children.Add(Row(r));

        return new Expander
        {
            Header = "高级：速度计算 · 平滑 · 方向细节（一般不用动）",
            IsExpanded = false,
            Margin = new Thickness(0, 14, 0, 0),
            Content = inner,
        };
    }

    FrameworkElement Row(ProfRow r)
    {
        var g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(224) });
        AddCell(g, 0, Label(r.Label, r.Hint, false));
        AddCell(g, 1, MakeEditor(r.Kind, r.Options, r.Names,
            v => r.Set(App.State.SelectedProfile, v), () => r.Get(App.State.SelectedProfile)));
        return g;
    }

    /// <summary>
    /// "Output 1000 ÷ mouse 600 = 1.67×" - the one number that explains why a 600 DPI mouse does not
    /// behave like 600 DPI. It was previously only inferable from the driver's source: a player sets
    /// output DPI 1000 (the default) and device DPI 600 (the truth) and gets a mouse 67% faster than
    /// the number on the box, with nothing on screen saying so.
    /// </summary>
    FrameworkElement BenchNote()
    {
        var p = App.State.SelectedProfile;
        var t = new TextBlock
        {
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 2),
            Foreground = (Brush)FindResource("TextTertiaryBrush"),
        };

        if (p == null) { t.Text = ""; return t; }

        double dev = DeviceDpi();
        double output = p.OutputDpi;
        double k = Sensitivity.BaseRatio(output, dev);

        if (dev <= 0)
        {
            t.Text = $"实际基础倍率 {k:0.00}×。设备页没填「鼠标真实 DPI」（0 = 关闭归一化），" +
                     $"所以只能按 1000 基准算 —— 这种情况下输出 {output:0} 就是 {k:0.00} 倍，" +
                     $"光标仍是你鼠标原本的 DPI。\n" +
                     $"填上真实的 {AppState.Prefs.MouseDpi:0} 之后，同样这个 {output:0} 会变成 " +
                     $"{Sensitivity.BaseRatio(output, AppState.Prefs.MouseDpi):0.00}×：那才是「600 的鼠标当 1000 用」。";
            return t;
        }

        string what = Sensitivity.IsUnity(output, dev)
            ? "，和鼠标 1:1，没有额外放大"
            : $"，等效于一只 {Sensitivity.EffectiveDpi(output, dev):0} DPI 的鼠标（不改曲线形状，整体快慢就是它）";
        t.Text = $"实际基础倍率 {k:0.00}× ＝ 输出 {output:0} ÷ 鼠标 {dev:0}{what}";
        return t;
    }

    static void AddCell(Grid g, int col, UIElement e) { Grid.SetColumn(e, col); g.Children.Add(e); }

    static FrameworkElement Label(string text, string hint, bool strong)
    {
        var t = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            FontSize = 12.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
        };
        if (!strong) t.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        if (!string.IsNullOrEmpty(hint)) t.ToolTip = hint;
        return t;
    }

    FrameworkElement Section(string title, string? sub = null)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 14, 0, 6) };
        sp.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 13 });
        if (!string.IsNullOrEmpty(sub))
            sp.Children.Add(new TextBlock
            {
                Text = sub,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = (Brush)FindResource("TextTertiaryBrush"),
            });
        return sp;
    }

    /// <summary>Editor bound to a get/set pair; commit on enter/lostfocus, then redraw.</summary>
    FrameworkElement MakeEditor(Kind kind, string[]? options, string[]? names,
                               Action<object> set, Func<object> get, bool rebuildsForm = false)
    {
        switch (kind)
        {
            case Kind.Bool:
            {
                var c = new CheckBox
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 2, 0, 2),
                    Content = "开",
                };
                c.SetResourceReference(Control.ForegroundProperty, "TextSecondaryBrush");
                c.Checked += (_, _) => { if (!suppress) Commit(() => set(true)); };
                c.Unchecked += (_, _) => { if (!suppress) Commit(() => set(false)); };
                editors.Add(Memo(c, get));
                return c;
            }
            case Kind.Enum:
            {
                // The stored values are the driver's own words ("in_out", "classic"). They are shown
                // next to a plain-language name, because "in_out" is not something anyone can choose
                // between without a manual.
                var cb = new ComboBox { Margin = new Thickness(4, 2, 0, 2), Height = 28 };
                var list = options ?? Modes;
                for (int i = 0; i < list.Length; i++)
                    cb.Items.Add(new ComboBoxItem
                    {
                        Content = names != null && i < names.Length ? names[i] : list[i],
                        Tag = list[i],
                    });

                cb.SelectionChanged += (_, _) =>
                {
                    if (suppress || cb.SelectedItem is not ComboBoxItem item || item.Tag is not string value) return;
                    Commit(() => set(value));
                    if (!rebuildsForm) return;
                    // The row list itself changes with the mode, and this combo is one of the rows it
                    // would destroy - rebuild after the event has finished unwinding.
                    Dispatcher.BeginInvoke(new Action(() => { BuildForm(); RefreshValues(); RefreshMeaning(); }),
                        System.Windows.Threading.DispatcherPriority.Background);
                };
                editors.Add(Memo(cb, get));
                return cb;
            }
            default:
            {
                var tb = new TextBox { Margin = new Thickness(4, 2, 0, 2), MinHeight = 28 };
                void CommitIt()
                {
                    if (suppress || App.State.Busy || App.State.SelectedProfile == null) return;
                    // An untouched box must not commit. The display is rounded, so simply focusing a
                    // field and clicking away used to overwrite a long value with its rounded form
                    // (0.123456789 -> 0.123457) and mark the whole document dirty.
                    if (shownText.TryGetValue(tb, out var shown) && tb.Text.Trim() == shown) return;
                    // never let NaN / Infinity / 1e300 reach the driver: it precomputes
                    // logarithms and roots from these and the mouse can become unusable
                    if (double.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                        && double.IsFinite(d) && Math.Abs(d) <= 100_000)
                        Commit(() => set(d));
                    else
                    {
                        App.State.Status = "这个数不收：只接受有限数字（不能有 NaN、无穷、超过 1e5），已退回上一个值";
                        RefreshValues();
                    }
                }
                tb.LostFocus += (_, _) => CommitIt();
                tb.KeyDown += (_, e) =>
                {
                    if (e.Key == System.Windows.Input.Key.Enter) { CommitIt(); e.Handled = true; }
                };
                editors.Add(Memo(tb, get));
                return tb;
            }
        }
    }

    static FrameworkElement Memo(FrameworkElement e, Func<object> getter) { e.Tag = getter; return e; }

    void Commit(Action apply)
    {
        if (suppress || App.State.Busy || App.State.SelectedProfile == null) return;
        try { apply(); } catch { /* keep the old value on a bad assignment */ }
        // Touch() already redraws the chart when the selected profile actually changed; calling
        // RebuildCurves here too evaluated the whole curve a second time per keystroke.
        App.State.Touch();
    }

    /// <summary>Shortest text that still parses back to the same double, so a round trip through
    /// the boxes can never quietly round a parameter.</summary>
    static string Format(double v)
    {
        string s = v.ToString("0.##########", CultureInfo.InvariantCulture);
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var back) && back == v) return s;
        return v.ToString("R", CultureInfo.InvariantCulture);
    }

    void RefreshValues()
    {
        if (App.State.SelectedProfile == null) return;
        suppress = true;
        try
        {
            foreach (var e in editors)
            {
                if (e.Tag is not Func<object> get) continue;
                object v;
                try { v = get(); } catch { continue; }

                switch (e)
                {
                    case TextBox tb:
                        string text = v is double dv ? Format(dv) : Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
                        shownText[tb] = text;
                        if (!HasFocus(tb)) tb.Text = text;
                        break;
                    case ComboBox cb:
                        if (v is not string s) break;
                        foreach (ComboBoxItem item in cb.Items)
                            if (item.Tag is string tag && tag == s)
                            {
                                if (cb.SelectedItem != item) cb.SelectedItem = item;
                                break;
                            }
                        break;
                    case CheckBox c:
                        if (v is bool b) c.IsChecked = b;
                        break;
                }
            }
        }
        finally { suppress = false; }
    }

    static bool HasFocus(Control c) => c.IsKeyboardFocused || c.IsKeyboardFocusWithin;

    /// <summary>The same curve in units you can feel, so the chart is not just a shape.</summary>
    void RefreshMeaning()
    {
        var p = App.State.SelectedProfile;
        CurveMeaning.Text = p == null ? "" : App.State.PhysicsReadout(p);
    }

    void OnKind(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        // the setter rebuilds once, and only when the kind actually changed
        App.State.CurveKind = RbGain.IsChecked == true ? CurveKind.Gain : CurveKind.Sensitivity;
    }

    void OnChartScale(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(TxtChartDpi.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var dpi) || dpi < 0)
        {
            App.State.Status = "图表 DPI 只接受 0 或正数（0 = 横轴按 counts/ms），这次改动没有生效";
            return;
        }
        if (!int.TryParse(TxtChartPoll.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pr) || pr < 0)
        {
            App.State.Status = "图表轮询率只接受 0 或正整数，这次改动没有生效";
            return;
        }
        // both boxes belong to one action: ApplyChartScale saves the prefs once and redraws once
        App.State.ApplyChartScale(dpi, pr);
    }
}
