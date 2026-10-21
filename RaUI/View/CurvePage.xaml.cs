using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using RaCore.Model;

namespace RaUI.View;

public partial class CurvePage : UserControl
{
    enum Kind { Double, Bool, Enum }

    record AxisRow(string Label, string Hint, Kind Kind, string[] Options,
                   Func<AccelDto, object> Get, Action<AccelDto, object> Set);
    record ProfRow(string Label, string Hint, Kind Kind, string[] Options,
                   Func<ProfileDto, object> Get, Action<ProfileDto, object> Set);

    static readonly string[] Modes = { "classic", "jump", "natural", "synchronous", "power", "lut", "noaccel" };
    static readonly string[] CapModes = { "in_out", "input", "output" };

    readonly List<FrameworkElement> editors = new();
    bool suppress;

    public CurvePage()
    {
        InitializeComponent();
        DataContext = App.State;

        TxtChartDpi.Text = AppState.Prefs.ChartDpi.ToString("0");
        TxtChartPoll.Text = AppState.Prefs.ChartPollRate.ToString();
        ChkSamples.IsChecked = AppState.Prefs.ShowSamples;
        App.State.ShowSamples = AppState.Prefs.ShowSamples;
        App.State.ChartDpi = AppState.Prefs.ChartDpi;
        App.State.ChartPollRate = AppState.Prefs.ChartPollRate;

        Chart.SetBinding(Controls.CurveChart.SeriesProperty,
            new System.Windows.Data.Binding(nameof(AppState.Series)) { Source = App.State });
        Chart.SetBinding(Controls.CurveChart.HistogramProperty,
            new System.Windows.Data.Binding(nameof(AppState.Histogram)) { Source = App.State });
        Chart.SetBinding(Controls.CurveChart.MarkersProperty,
            new System.Windows.Data.Binding(nameof(AppState.Markers)) { Source = App.State });
        Chart.SetBinding(Controls.CurveChart.ZonesProperty,
            new System.Windows.Data.Binding(nameof(AppState.Zones)) { Source = App.State });
        Chart.SetBinding(Controls.CurveChart.XLabelProperty,
            new System.Windows.Data.Binding(nameof(AppState.XAxisLabel)) { Source = App.State });
        Chart.SetBinding(Controls.CurveChart.YLabelProperty,
            new System.Windows.Data.Binding(nameof(AppState.YAxisLabel)) { Source = App.State });

        BuildForm();

        App.State.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppState.SelectedProfile) or nameof(AppState.Series))
                Dispatcher.Invoke(RefreshValues);
        };

        Loaded += (_, _) => RefreshValues();
    }

    // ---------- axis-specific parameters ----------
    static readonly AxisRow[] AxisRows =
    {
        new("模式", "acceleration style", Kind.Enum, Modes, d => d.Mode, (d, v) => d.Mode = (string)v),
        new("按增益", "Gain / Velocity：曲线形状作用在增益图还是灵敏度图上", Kind.Bool, null, d => d.Gain, (d, v) => d.Gain = (bool)v),
        new("输入阈值", "gain offset：低于此速度不加速（classic/natural 有效）", Kind.Double, null, d => d.InputOffset, (d, v) => d.InputOffset = (double)v),
        new("加速强度", "acceleration", Kind.Double, null, d => d.Acceleration, (d, v) => d.Acceleration = (double)v),
        new("指数", "exponentClassic：2 = 线性增益上升", Kind.Double, null, d => d.ExponentClassic, (d, v) => d.ExponentClassic = (double)v),
        new("封顶 X", "Cap / Jump x：封顶触发点（含义随 Cap mode 变）", Kind.Double, null, d => d.Cap.X, (d, v) => d.Cap.X = (double)v),
        new("封顶 Y", "Cap / Jump y：封顶后的增益比率", Kind.Double, null, d => d.Cap.Y, (d, v) => d.Cap.Y = (double)v),
        new("封顶方式", "in_out=按输入定输出，input=按输入速度，output=按输出速度", Kind.Enum, CapModes, d => d.CapMode, (d, v) => d.CapMode = (string)v),
        new("输出阈值", "outputOffset（power 模式专用）", Kind.Double, null, d => d.OutputOffset, (d, v) => d.OutputOffset = (double)v),
        new("衰减率", "decayRate（natural 模式）", Kind.Double, null, d => d.DecayRate, (d, v) => d.DecayRate = (double)v),
        new("上限", "limit（natural 模式的最大比率）", Kind.Double, null, d => d.Limit, (d, v) => d.Limit = (double)v),
        new("动力", "motivity（synchronous 模式的总变化幅度）", Kind.Double, null, d => d.Motivity, (d, v) => d.Motivity = (double)v),
        new("伽马", "gamma（synchronous 的过渡陡度）", Kind.Double, null, d => d.Gamma, (d, v) => d.Gamma = (double)v),
        new("同步速度", "syncSpeed（synchronous 的锚定速度）", Kind.Double, null, d => d.SyncSpeed, (d, v) => d.SyncSpeed = (double)v),
        new("平滑", "smooth（jump/synchronous 的过渡形状 0..1）", Kind.Double, null, d => d.Smooth, (d, v) => d.Smooth = (double)v),
        new("缩放", "scale（power 模式）", Kind.Double, null, d => d.Scale, (d, v) => d.Scale = (double)v),
        new("power 指数", "exponentPower（power 模式）", Kind.Double, null, d => d.ExponentPower, (d, v) => d.ExponentPower = (double)v),
    };

    // ---------- whole-profile parameters ----------
    static readonly ProfRow[] ProfRows =
    {
        new("输出 DPI", "Output DPI：输出 counts 的基准（1000 = 不改）", Kind.Double, null, p => p.OutputDpi, (p, v) => p.OutputDpi = (double)v),
        new("垂直倍率", "Y/X output DPI ratio", Kind.Double, null, p => p.YxRatio, (p, v) => p.YxRatio = (double)v),
        new("向左倍率", "L/R ratio（只作用于 x<0）", Kind.Double, null, p => p.LrRatio, (p, v) => p.LrRatio = (double)v),
        new("向上倍率", "U/D ratio（只作用于 y<0）", Kind.Double, null, p => p.UdRatio, (p, v) => p.UdRatio = (double)v),
        new("整体/分量", "true=用合速度作用于整个向量(whole)；false=XY 各自独立(by component)", Kind.Bool, null,
            p => p.Speed.Whole, (p, v) => p.Speed.Whole = (bool)v),
        new("lp 范数", "whole 模式下合速度的范数：2=欧氏，>=16 取 max", Kind.Double, null, p => p.Speed.LpNorm, (p, v) => p.Speed.LpNorm = (double)v),
        new("域权重 X", "domain_weights.x：拉伸输入速度轴（各向异性）", Kind.Double, null, p => p.Domain.X, (p, v) => p.Domain.X = (double)v),
        new("域权重 Y", "domain_weights.y", Kind.Double, null, p => p.Domain.Y, (p, v) => p.Domain.Y = (double)v),
        new("值域 X", "range_weights.x：把加速幅度按比例缩放", Kind.Double, null, p => p.Range.X, (p, v) => p.Range.X = (double)v),
        new("值域 Y", "range_weights.y（whole 模式下按角度插值到 Y）", Kind.Double, null, p => p.Range.Y, (p, v) => p.Range.Y = (double)v),
        new("旋转", "Degrees of rotation：先旋转输入向量", Kind.Double, null, p => p.Rotation, (p, v) => p.Rotation = (double)v),
        new("角度吸附", "Degrees of angle snapping", Kind.Double, null, p => p.Snap, (p, v) => p.Snap = (double)v),
        new("输入速度上限", "Input Speed Cap：超过此速度的输入被夹住（0=关）", Kind.Double, null, p => p.InputSpeedCap, (p, v) => p.InputSpeedCap = (double)v),
        new("输入平滑", "EMA 半衰期 ms：只影响加速判定，不是鼠标平滑", Kind.Double, null, p => p.Speed.InputHalflife, (p, v) => p.Speed.InputHalflife = (double)v),
        new("比例平滑", "EMA 半衰期 ms：只影响加速判定", Kind.Double, null, p => p.Speed.ScaleHalflife, (p, v) => p.Speed.ScaleHalflife = (double)v),
        new("输出平滑", "EMA 半衰期 ms：这才是传统“鼠标平滑”，会加延迟", Kind.Double, null, p => p.Speed.OutputHalflife, (p, v) => p.Speed.OutputHalflife = (double)v),
    };

    void BuildForm()
    {
        Form.Children.Add(Section("配置文件"));

        var pick = new ComboBox { Margin = new Thickness(0, 0, 0, 4) };
        var nameTemplate = new System.Windows.DataTemplate(typeof(ProfileDto));
        var fef = new FrameworkElementFactory(typeof(TextBlock));
        fef.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ProfileDto.Name)));
        fef.SetValue(TextBlock.ForegroundProperty, (Brush)FindResource("TextPrimaryBrush"));
        fef.SetValue(TextBlock.FontSizeProperty, 13.0);
        nameTemplate.VisualTree = fef;
        pick.ItemTemplate = nameTemplate;
        pick.SetBinding(ItemsControl.ItemsSourceProperty, new System.Windows.Data.Binding(nameof(AppState.Profiles)));
        pick.SetBinding(Selector.SelectedItemProperty,
            new System.Windows.Data.Binding(nameof(AppState.SelectedProfile)) { Mode = System.Windows.Data.BindingMode.TwoWay });
        Form.Children.Add(pick);

        // preset management
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        var nameBox = new TextBox { MinHeight = 28, Width = 140, Text = App.State.SelectedProfile?.Name ?? "" };
        nameBox.LostFocus += (_, _) =>
        {
            var p = App.State.SelectedProfile;
            if (p != null && !string.IsNullOrWhiteSpace(nameBox.Text) && p.Name != nameBox.Text.Trim())
            {
                App.State.RenameSelectedProfile(nameBox.Text);
                nameBox.Text = App.State.SelectedProfile?.Name ?? "";
            }
        };
        tools.Children.Add(nameBox);

        var dup = new Button { Content = "复制", MinHeight = 28, Margin = new Thickness(6, 0, 0, 0) };
        dup.Click += (_, _) =>
        {
            App.State.AddProfile((App.State.SelectedProfile?.Name ?? "profile") + " 副本");
            RefreshValues();
        };
        tools.Children.Add(dup);

        var del = new Button { Content = "删除", MinHeight = 28, Margin = new Thickness(6, 0, 0, 0) };
        del.Click += (_, _) =>
        {
            var p = App.State.SelectedProfile;
            if (p == null) return;
            App.State.RemoveProfileAt(App.State.Doc.Profiles.IndexOf(p));
            RefreshValues();
        };
        tools.Children.Add(del);

        var tip = new TextBlock
        {
            Text = "每个配置可在“设备”页按鼠标单独指定",
            FontSize = 11.5,
            Margin = new Thickness(0, 2, 0, 6),
        };
        tip.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
        Form.Children.Add(tip);
        Form.Children.Add(tools);

        ParamPanel.Children.Add(Section("加速参数（X / Y 独立）"));

        var header = new Grid { Margin = new Thickness(0, 2, 0, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        AddCell(header, 0, Label("参数", null, true));
        AddCell(header, 1, Label("水平 X", null, true));
        AddCell(header, 2, Label("垂直 Y", null, true));
        ParamPanel.Children.Add(header);

        foreach (var r in AxisRows)
        {
            var g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
            AddCell(g, 0, Label(r.Label, r.Hint, false));
            AddCell(g, 1, MakeEditor(r.Kind, r.Options,
                v => r.Set(App.State.SelectedProfile.X, v), () => r.Get(App.State.SelectedProfile.X)));
            AddCell(g, 2, MakeEditor(r.Kind, r.Options,
                v => r.Set(App.State.SelectedProfile.Y, v), () => r.Get(App.State.SelectedProfile.Y)));
            ParamPanel.Children.Add(g);
        }

        ParamPanel.Children.Add(Section("速度计算 / 输出 / 全局"));
        foreach (var r in ProfRows)
        {
            var g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(224) });
            AddCell(g, 0, Label(r.Label, r.Hint, false));
            AddCell(g, 1, MakeEditor(r.Kind, r.Options,
                v => r.Set(App.State.SelectedProfile, v), () => r.Get(App.State.SelectedProfile)));
            ParamPanel.Children.Add(g);
        }
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

    FrameworkElement Section(string title) => new TextBlock
    {
        Text = title,
        Margin = new Thickness(0, 12, 0, 6),
        FontWeight = FontWeights.SemiBold,
        FontSize = 13,
    };

    /// <summary>Editor bound to a get/set pair; commit on enter/lostfocus, then redraw.</summary>
    FrameworkElement MakeEditor(Kind kind, string[] options, Action<object> set, Func<object> get)
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
                editors.Add(Memo(c, () => (bool)get()));
                return c;
            }
            case Kind.Enum:
            {
                var cb = new ComboBox { Margin = new Thickness(4, 2, 0, 2), Height = 28 };
                foreach (var o in options) cb.Items.Add(o);
                cb.SelectionChanged += (_, _) =>
                {
                    if (!suppress && cb.SelectedItem is string s) Commit(() => set(s));
                };
                editors.Add(Memo(cb, () => (string)get()));
                return cb;
            }
            default:
            {
                var tb = new TextBox { Margin = new Thickness(4, 2, 0, 2), MinHeight = 28 };
                void CommitIt()
                {
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
                editors.Add(Memo(tb, () => (double)get()));
                return tb;
            }
        }
    }

    static FrameworkElement Memo(FrameworkElement e, Func<object> getter) { e.Tag = getter; return e; }

    void Commit(Action apply)
    {
        try { apply(); } catch { /* keep the old value on a bad assignment */ }
        App.State.Touch();
        App.State.RebuildCurves();
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
                        if (!HasFocus(tb))
                            tb.Text = Convert.ToDouble(v, CultureInfo.InvariantCulture).ToString("0.######", CultureInfo.InvariantCulture);
                        break;
                    case ComboBox cb:
                        if (v is string s && cb.SelectedItem as string != s) cb.SelectedItem = s;
                        break;
                    case CheckBox c:
                        if (v is bool b) c.IsChecked = b;
                        break;
                }
            }
        }
        finally { suppress = false; }

        UpdateNote();
    }

    static bool HasFocus(Control c) => c.IsKeyboardFocused || c.IsKeyboardFocusWithin;

    void UpdateNote()
    {
        var p = App.State.SelectedProfile;
        if (p == null) { ChartNote.Text = ""; PhysicsNote.Text = ""; return; }

        // The 起加速 / 封顶 numbers are drawn on the chart itself now (labelled X起 / X顶 / Y起 / Y顶),
        // so repeating them as a sentence below only creates a second place to keep in sync.
        ChartNote.Text = App.State.Telemetry + "（色带百分比 = 你最近的手速落在各段的占比，按 X 轴计）";
        PhysicsNote.Text = App.State.PhysicsReadout(p);
    }

    void OnToggleParams(object sender, RoutedEventArgs e)
    {
        bool open = ParamCard.Visibility != Visibility.Visible;
        ParamCard.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        BtnParams.Content = open ? "收起参数 25b4" : "调整参数 25be";
    }

    void OnSamples(object sender, RoutedEventArgs e)
    {
        App.State.ShowSamples = ChkSamples.IsChecked == true;
        AppState.Prefs.ShowSamples = App.State.ShowSamples;
        AppState.SavePrefs();
        UpdateNote();
    }

    void OnKind(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        App.State.CurveKind = RbSens.IsChecked == true ? CurveKind.Sensitivity
                             : RbGain.IsChecked == true ? CurveKind.Gain : CurveKind.Sensitivity;
        App.State.RebuildCurves();
        UpdateNote();
    }

    void OnChartScale(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(TxtChartDpi.Text, out var dpi) && dpi >= 0) App.State.ChartDpi = dpi;
        if (int.TryParse(TxtChartPoll.Text, out var pr) && pr >= 0) App.State.ChartPollRate = pr;
        AppState.Prefs.ChartDpi = App.State.ChartDpi;
        AppState.Prefs.ChartPollRate = App.State.ChartPollRate;
        AppState.SavePrefs();
        App.State.RebuildCurves();
        UpdateNote();
    }
}
