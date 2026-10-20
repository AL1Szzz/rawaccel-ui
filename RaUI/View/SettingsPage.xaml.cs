using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace RaUI.View;

public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    void Refresh()
    {
        TxtDriver.Text = App.State.DriverInfo;
        TxtPath.Text = "设置文件：" + App.State.SettingsPath +
                       "\n数据目录（日志/审计/偏好）：" + RaCore.AppData.Location +
                       (UiPrefs.LastError is { Length: > 0 } err ? "\n偏好：" + err : "");

        var legacy = Autostart.LegacyWriterShortcut();
        TxtLegacy.Text = legacy is null
            ? "未检测到官方 writer.exe 开机快捷方式。"
            : $"检测到官方 writer.exe 开机快捷方式：{legacy.Value.path}（参数 {legacy.Value.args}）。启用下面的开关后建议删掉它，避免两次写入。";

        ChkAutostart.IsChecked = Autostart.IsEnabled;
        var cmd = Autostart.CurrentCommand() ?? "";
        ChkWriteOnStart.IsChecked = cmd.Contains("--write");
        ChkWriteOnStart.IsEnabled = Autostart.IsEnabled;

        FillModes();
    }

    bool fillingModes;

    void FillModes()
    {
        fillingModes = true;
        try
        {
            var pr = AppState.Prefs;
            var modes = RaCore.Display.AvailableModes();

            ChkCustomMode.IsChecked = pr.UseCustomFullscreenMode;
            CmbRes.Items.Clear();
            foreach (var g in modes.GroupBy(m => (m.Width, m.Height))
                                  .OrderByDescending(g => (long)g.Key.Width * g.Key.Height))
                CmbRes.Items.Add($"{g.Key.Width}×{g.Key.Height}");

            var cur = RaCore.Display.Current();
            string fallback = $"{cur.Width}×{cur.Height}";
            var want = $"{pr.FullscreenWidth}×{pr.FullscreenHeight}";
            if (CmbRes.Items.Count > 0)
                CmbRes.SelectedItem = CmbRes.Items.Contains(want) ? want
                                    : CmbRes.Items.Contains(fallback) ? fallback
                                    : CmbRes.Items[0];
            FillHz();
            if (modes.Any(m => $"{m.Width}×{m.Height}" == (CmbRes.SelectedItem?.ToString() ?? "") && m.Refresh == pr.FullscreenRefresh))
                CmbHz.SelectedItem = pr.FullscreenRefresh.ToString();
        }
        finally
        {
            // merely opening the page must not rewrite the prefs file
            fillingModes = false;
            UpdateWarn();
        }
    }

    void UpdateWarn()
    {
        var pr = AppState.Prefs;
        ModeWarn.Text = pr.UseCustomFullscreenMode
            ? $"测试将切到 {pr.FullscreenWidth}×{pr.FullscreenHeight}" +
              (pr.FullscreenRefresh > 0 ? $" @ {pr.FullscreenRefresh}Hz" : "") + "，关闭测试窗口后自动还原；还原失败会在本轮日志里写出来。"
            : "测试保持当前桌面分辨率，只开无边框全屏。";
    }

    void FillHz()
    {
        var sel = CmbRes.SelectedItem?.ToString() ?? "";
        var (w, h) = ParseRes(sel);
        CmbHz.Items.Clear();
        foreach (var m in RaCore.Display.AvailableModes().Where(m => m.Width == w && m.Height == h)
                                              .Select(m => m.Refresh).Distinct().OrderByDescending(x => x))
            CmbHz.Items.Add(m.ToString());
        if (CmbHz.Items.Count > 0) CmbHz.SelectedIndex = 0;
    }

    static (int w, int h) ParseRes(string s)
    {
        var parts = s.Split('×');
        return parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) ? (w, h) : (0, 0);
    }

    void SaveModes()
    {
        if (fillingModes) return;
        var pr = AppState.Prefs;
        pr.UseCustomFullscreenMode = ChkCustomMode.IsChecked == true;
        var (w, h) = ParseRes(CmbRes.SelectedItem?.ToString() ?? "");
        if (w > 0) { pr.FullscreenWidth = w; pr.FullscreenHeight = h; }
        if (int.TryParse(CmbHz.SelectedItem?.ToString(), out var hz)) pr.FullscreenRefresh = hz;
        AppState.SavePrefs();
        UpdateWarn();
    }

    void OnCustomMode(object sender, RoutedEventArgs e) => SaveModes();
    void OnRes(object sender, SelectionChangedEventArgs e) { if (CmbRes.SelectedItem != null) FillHz(); SaveModes(); }
    void OnHz(object sender, SelectionChangedEventArgs e) => SaveModes();

    void OnAutostartChanged(object sender, RoutedEventArgs e)
    {
        if (ChkAutostart.IsChecked == true)
            Autostart.Enable(ChkWriteOnStart.IsChecked == true, true);
        else
            Autostart.Disable();
        App.State.Status = Autostart.IsEnabled
            ? "已注册登录自启：" + Autostart.CurrentCommand()
            : "已移除登录自启（不影响驱动，驱动里的设置仍在）";
        ChkWriteOnStart.IsEnabled = Autostart.IsEnabled;
    }

    void OnWriteOnStartChanged(object sender, RoutedEventArgs e)
    {
        if (Autostart.IsEnabled)
            Autostart.Enable(ChkWriteOnStart.IsChecked == true, true);
        Refresh();
    }

    async void OnDisable(object sender, RoutedEventArgs e)
    {
        var r = MessageBox.Show(Window.GetWindow(this),
            "清空驱动后所有鼠标立刻恢复原始输入（不加速）。确定？",
            "清空驱动", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (r == MessageBoxResult.OK) await App.State.DisableDriverAsync();
    }

    void OnRestore(object sender, RoutedEventArgs e)
    {
        var bak = App.State.SettingsPath + ".bak";
        if (!File.Exists(bak)) { App.State.Status = "没有 .bak 可恢复"; return; }
        try
        {
            // keep what is there now, so a wrong restore is itself undoable
            if (File.Exists(App.State.SettingsPath))
                File.Copy(App.State.SettingsPath, App.State.SettingsPath + ".prev", true);
            File.Copy(bak, App.State.SettingsPath, true);
            App.State.StartupLoad();
            App.State.Status = "已从 .bak 恢复设置文件（当前版本留在 .prev；还需点“写入驱动”才生效）";
        }
        catch (Exception ex)
        {
            App.State.Status = "恢复失败：" + ex.Message;
        }
    }
}
