using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using RaCore;
using RaCore.Model;

namespace RaUI.View;

/// <summary>Only user actions save or apply a plan. Selection is a preview, never a driver write.</summary>
public partial class PlansPage : UserControl
{
    public sealed record PlanRow(Preset Plan, string Label, string Description);
    Action pending;
    bool refreshing;

    public PlansPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
        App.State.PropertyChanged += (_, e) =>
        {
            if (!IsLoaded) return;
            if (e.PropertyName is nameof(AppState.CanWrite) or nameof(AppState.CanUndoSettings))
                UpdateButtons();
        };
    }

    PlanRow Picked => PlanList.SelectedItem as PlanRow;
    static bool Original(Preset p) => p.Name == "基线";
    static string Label(Preset p) => Original(p) ? "原来的手感" : p.Name;

    void Refresh(string select = null)
    {
        if (refreshing) return;
        refreshing = true;
        try
        {
            select ??= Picked?.Plan.Name;
            App.State.RefreshLiveSha();
            var list = App.State.ListPresets().OrderBy(p => Original(p) ? 0 : 1).ThenBy(p => p.Name).ToList();
            var rows = list.Select(p =>
            {
                var profile = Presets.TryLoad(p.File);
                string info = profile == null ? "文件不可读取" :
                    $"垂直倍率 {profile.YxRatio.ToString("0.##", CultureInfo.InvariantCulture)} · 水平加速 {profile.X.Acceleration.ToString("0.###", CultureInfo.InvariantCulture)}";
                bool live = p.Sha == App.State.LiveProfileSha;
                return new PlanRow(p, Label(p) + (live ? "  · 当前使用" : ""), info);
            }).ToList();
            PlanList.ItemsSource = rows;
            PlanList.SelectedItem = rows.FirstOrDefault(r => r.Plan.Name == select) ?? rows.FirstOrDefault();
            var active = list.FirstOrDefault(p => p.Sha == App.State.LiveProfileSha);
            Current.Text = !App.State.DriverReachable ? "驱动未连接" :
                active == null ? "当前使用：自定义参数" : "当前使用：" + Label(active);
            EmptyNote.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateButtons();
        }
        catch (Exception e) { App.State.Status = "方案列表读取失败：" + e.Message; }
        finally { refreshing = false; }
    }

    void UpdateButtons()
    {
        bool selected = Picked != null;
        bool idle = !App.State.Busy;
        Apply.IsEnabled = selected && App.State.CanWrite;
        Undo.IsEnabled = App.State.CanUndoSettings && App.State.DriverReachable;
        Save.IsEnabled = idle;
        Overwrite.IsEnabled = selected && idle && !Original(Picked.Plan);
        Delete.IsEnabled = selected && idle && !Original(Picked.Plan);
        SelectedName.Text = selected ? Label(Picked.Plan) : "先从左侧选一份方案";
        if (selected && Original(Picked.Plan)) SelectedName.Text += "（保留原版，不能覆盖或删除）";
    }

    void OnSelect(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedName == null) return;
        Cancel();
        UpdateButtons();
    }

    void OnRefresh(object sender, RoutedEventArgs e) { Cancel(); Refresh(); }

    async void OnApply(object sender, RoutedEventArgs e)
    {
        var selected = Picked;
        if (selected == null || !App.State.CanWrite) return;
        Cancel();
        if (App.State.IsDirty)
        {
            Ask("当前有未保存的参数。确认替换为「" + Label(selected.Plan) + "」？",
                () => ApplyPlan(selected.Plan.Name));
            return;
        }
        await ApplyPlanAsync(selected.Plan.Name);
    }

    async void ApplyPlan(string name) => await ApplyPlanAsync(name);

    async Task ApplyPlanAsync(string name)
    {
        try { await App.State.ApplyPresetAsync(name); }
        catch (Exception e) { App.State.Status = "应用失败：" + e.Message; }
        Refresh(name);
    }

    async void OnUndo(object sender, RoutedEventArgs e)
    {
        Cancel();
        try { await App.State.UndoLastSettingsAsync(); }
        catch (Exception ex) { App.State.Status = "撤回失败：" + ex.Message; }
        Refresh();
    }

    void OnSave(object sender, RoutedEventArgs e)
    {
        Cancel();
        string name = Presets.SafeName(PlanName.Text);
        if (name.Length == 0) { Notice.Text = "请先给方案起个名字。"; return; }
        var existing = App.State.ListPresets().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            if (Original(existing)) { Notice.Text = "原来的手感保留不动，请用新名称保存。"; return; }
            Ask("「" + name + "」已存在，确认用当前参数覆盖？旧版会备份。", () => SaveCurrent(name, true));
        }
        else SaveCurrent(name, false);
    }

    void OnOverwrite(object sender, RoutedEventArgs e)
    {
        var selected = Picked;
        if (selected == null || Original(selected.Plan)) return;
        Ask("确认更新「" + Label(selected.Plan) + "」？旧版会备份。", () => SaveCurrent(selected.Plan.Name, true));
    }

    void SaveCurrent(string name, bool overwrite)
    {
        try
        {
            var current = App.State.SelectedProfile;
            if (current == null) { Notice.Text = "当前没有可保存的参数。"; return; }
            var existing = App.State.ListPresets().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (!overwrite || Original(existing)) { Notice.Text = "名称已存在，请换一个名字。"; return; }
                Backup(existing);
            }
            string file = Presets.Save(name, current.Clone(), out string error);
            if (file == null) { Notice.Text = error; return; }
            // New values invalidate a legacy handwritten description of old parameters.
            string note = Path.ChangeExtension(file, ".txt");
            if (File.Exists(note)) File.Delete(note);
            App.State.Status = "方案「" + name + "」已保存，当前手感未改变";
            Refresh(name);
            Notice.Text = "已保存。选择后点应用即可切换。";
        }
        catch (Exception e) { Notice.Text = "保存失败：" + e.Message; }
    }

    void OnDelete(object sender, RoutedEventArgs e)
    {
        var selected = Picked;
        if (selected == null || Original(selected.Plan)) return;
        Ask("确认删除「" + Label(selected.Plan) + "」？不影响正在使用的手感，文件会备份。", () =>
        {
            try
            {
                Backup(selected.Plan);
                if (!Presets.Delete(selected.Plan.Name, out string error)) { Notice.Text = error; return; }
                App.State.Status = "方案已删除，当前手感未改变";
                Refresh();
            }
            catch (Exception ex) { Notice.Text = "删除失败：" + ex.Message; }
        });
    }

    static void Backup(Preset plan)
    {
        string folder = Path.Combine(Presets.Dir, "history", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        File.Copy(plan.File, Path.Combine(folder, Path.GetFileName(plan.File)));
        if (plan.NoteFile.Length > 0 && File.Exists(plan.NoteFile))
            File.Copy(plan.NoteFile, Path.Combine(folder, Path.GetFileName(plan.NoteFile)));
    }

    void Ask(string message, Action action)
    {
        pending = action;
        Notice.Text = message;
        ConfirmPanel.Visibility = Visibility.Visible;
    }

    void Cancel()
    {
        pending = null;
        if (ConfirmPanel == null) return;
        ConfirmPanel.Visibility = Visibility.Collapsed;
        Notice.Text = "";
    }

    void OnCancel(object sender, RoutedEventArgs e) => Cancel();
    void OnConfirm(object sender, RoutedEventArgs e)
    {
        var action = pending;
        Cancel();
        if (!App.State.Busy) action?.Invoke();
    }
}
