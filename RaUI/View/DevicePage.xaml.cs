using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RaCore;
using RaCore.Model;

namespace RaUI.View;

public partial class DevicePage : UserControl
{
    public DevicePage()
    {
        InitializeComponent();
        Loaded += (_, _) => Build();
    }

    void OnRefresh(object sender, RoutedEventArgs e) => Build();

    void Build()
    {
        Panel.Children.Clear();
        Panel.Children.Add(DefaultCard());

        var devices = SafeEnumerate();
        if (devices.Count == 0)
            Panel.Children.Add(Note("没有枚举到鼠标类设备。驱动未装或系统里没有可用的 HID 鼠标时会出现这种情况。"));

        foreach (var d in devices) Panel.Children.Add(DeviceCard(d));
        BuildStale(devices);
    }

    List<MouseDevice> SafeEnumerate()
    {
        // enumeration needs the raw-input device list; a failure here must not blank the page,
        // but it must not be silent either - the reason is what tells us whether it is a
        // permissions problem or simply no mouse attached.
        try { return App.State.EnumerateDevices(); }
        catch (Exception e)
        {
            App.State.Status = "枚举鼠标失败：" + e.Message + "（下面只按配置文件里已有的设备显示）";
            return new List<MouseDevice>();
        }
    }

    // ---------- cards ----------

    Border DefaultCard()
    {
        var cfg = App.State.DefaultConfig;
        var sp = new StackPanel();
        sp.Children.Add(Title("默认配置（所有未单独设置的鼠标）"));
        sp.Children.Add(Sub("改这里等于改 defaultDeviceConfig。下面某只鼠标一旦单独配置，就不再跟随这里。"));

        sp.Children.Add(Check("禁用所有设备的加速", () => cfg.Disable, v => cfg.Disable = v,
            "true = 驱动仍然加载但不动任何输入"));
        sp.Children.Add(Text("鼠标真实 DPI", () => cfg.Dpi.ToString(), s => cfg.Dpi = ParseInt(s),
            "鼠标在驱动/硬件里设的那个 DPI（不是传感器标称值）。填对了，曲线的阈值单位才是真实的物理速度（in/s），换鼠标才不会换手感。\n" +
            "注意它不会改变光标速度 —— 实际速度看曲线页的「输出 DPI ÷ 这个数」。0 = 关掉归一化，此时曲线横轴退回 counts/ms。"));
        sp.Children.Add(Text("轮询率 Hz（0=自动）", () => cfg.PollingRate.ToString(), s => cfg.PollingRate = ParseInt(s),
            "只有在开启加速时出现指针卡顿才需要固定它"));
        sp.Children.Add(Check("按轮询率使用固定时间间隔", () => cfg.PollTimeLock, v => cfg.PollTimeLock = v,
            "配合上面的轮询率；默认关闭，由驱动自己测时间"));

        return Card(sp);
    }

    Border DeviceCard(MouseDevice d)
    {
        var entry = App.State.FindDevice(d.Id);
        var sp = new StackPanel();

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel();
        left.Children.Add(Title(string.IsNullOrWhiteSpace(d.Name) ? d.Id : d.Name));
        left.Children.Add(Sub(d.Id + (d.Handles > 1 ? $"  ·  {d.Handles} 个 HID 集合" : "") +
                              (entry is null ? "  ·  跟随默认配置" : "  ·  已单独配置")));
        Grid.SetColumn(left, 0);
        head.Children.Add(left);

        var btn = new Button
        {
            Content = entry is null ? "单独配置" : "删除单独配置",
            VerticalAlignment = VerticalAlignment.Top,
            MinHeight = 28,
        };
        btn.Click += (_, _) =>
        {
            if (App.State.FindDevice(d.Id) is null) App.State.AddDevice(d);
            else App.State.RemoveDevice(d.Id);
            App.State.Touch();
            Build();
        };
        Grid.SetColumn(btn, 1);
        head.Children.Add(btn);
        sp.Children.Add(head);

        if (entry is not null)
        {
            sp.Children.Add(Text("鼠标真实 DPI", () => entry.Config.Dpi.ToString(), s => entry.Config.Dpi = ParseInt(s),
                "这只鼠标实际设的 DPI。填对它，加速阈值才按物理速度对齐，换鼠标不用重调曲线。\n" +
                "和曲线页的「输出 DPI」一起决定基础倍率：输出 ÷ 这个数。0 = 不归一化（单位退回 counts/ms）。"));
            sp.Children.Add(Text("轮询率 Hz（0=自动）", () => entry.Config.PollingRate.ToString(), s => entry.Config.PollingRate = ParseInt(s), null));
            sp.Children.Add(Check("禁用这只设备", () => entry.Config.Disable, v => entry.Config.Disable = v,
                "键盘上的指点设备 / 触控板通常应该禁用"));
            sp.Children.Add(Check("固定时间间隔", () => entry.Config.PollTimeLock, v => entry.Config.PollTimeLock = v, null));
            sp.Children.Add(Combo("使用配置", ProfileNames(), () => entry.Profile, s => entry.Profile = s,
                "这只鼠标套用哪个配置文件"));
        }

        return Card(sp);
    }

    void BuildStale(List<MouseDevice> live)
    {
        var liveIds = new HashSet<string>(live.Select(x => x.Id), StringComparer.OrdinalIgnoreCase);
        var stale = App.State.Doc.Devices.Where(x => !liveIds.Contains(x.Id)).ToList();
        if (stale.Count == 0) return;

        var sp = new StackPanel();
        sp.Children.Add(Title("已配置但当前不在线"));
        foreach (var s in stale)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var t = new TextBlock
            {
                Text = (string.IsNullOrWhiteSpace(s.Name) ? s.Id : s.Name) + "   " + s.Id,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12.5,
            };
            t.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            Grid.SetColumn(t, 0);
            row.Children.Add(t);

            var del = new Button { Content = "删除", MinHeight = 26 };
            del.Click += (_, _) => { App.State.RemoveDevice(s.Id); Build(); };
            Grid.SetColumn(del, 1);
            row.Children.Add(del);
            sp.Children.Add(row);
        }
        Panel.Children.Add(Card(sp));
    }

    // ---------- little builders ----------

    static List<string> ProfileNames() => App.State.Doc.Profiles.Select(p => p.Name).ToList();

    Border Card(UIElement content) => new Border
    {
        Style = (Style)FindResource("Card"),
        Child = content,
    };

    static FrameworkElement Title(string s) => new TextBlock
    {
        Text = s,
        FontWeight = FontWeights.SemiBold,
        FontSize = 13.5,
        Margin = new Thickness(0, 0, 0, 4),
        TextWrapping = TextWrapping.Wrap,
    };

    static FrameworkElement Sub(string s)
    {
        var t = new TextBlock { Text = s, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
        return t;
    }

    FrameworkElement Note(string s)
    {
        var sp = new StackPanel();
        sp.Children.Add(Title("提示"));
        sp.Children.Add(Sub(s));
        return Card(sp);
    }

    FrameworkElement Row(string label, string? hint, FrameworkElement editor)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });

        var t = new TextBlock
        {
            Text = label,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = hint,
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        Grid.SetColumn(t, 0);
        g.Children.Add(t);

        Grid.SetColumn(editor, 1);
        g.Children.Add(editor);
        return g;
    }

    FrameworkElement Text(string label, Func<string> get, Action<string> set, string? hint)
    {
        var tb = new TextBox { MinHeight = 28, Margin = new Thickness(6, 2, 0, 2), Text = get() };
        void Commit()
        {
            try { set(tb.Text); App.State.Touch(); }
            catch { tb.Text = get(); }
        }
        tb.LostFocus += (_, _) => Commit();
        tb.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { Commit(); e.Handled = true; } };
        return Row(label, hint, tb);
    }

    FrameworkElement Check(string label, Func<bool> get, Action<bool> set, string? hint)
    {
        var c = new CheckBox
        {
            IsChecked = get(),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 4, 0, 4),
        };
        c.Click += (_, _) => { set(c.IsChecked == true); App.State.Touch(); };
        return Row(label, hint, c);
    }

    FrameworkElement Combo(string label, List<string> items, Func<string> get, Action<string> set, string? hint)
    {
        var cb = new ComboBox { Height = 28, Margin = new Thickness(6, 2, 0, 2) };
        foreach (var i in items) cb.Items.Add(i);
        cb.SelectedItem = items.Contains(get()) ? get() : items.FirstOrDefault();
        cb.SelectionChanged += (_, _) =>
        {
            if (cb.SelectedItem is string s) { set(s); App.State.Touch(); }
        };
        return Row(label, hint, cb);
    }

    static int ParseInt(string s) =>
        int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : 0;
}
