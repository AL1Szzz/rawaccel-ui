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

    /// <summary>Render hook (--dump-ui --open-rename): open the rename box on load so the layout it
    /// pushes the card into can be looked at without a mouse.</summary>
    public static bool DebugOpenRename;

    public PlansPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Refresh();
            if (DebugOpenRename)
            {
                DebugOpenRename = false;
                OnRename(this, new RoutedEventArgs());
            }
        };
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
            string liveSha = App.State.LiveProfileSha;

            // Several presets can hold byte-identical parameters - saving the current feel under a
            // new name is exactly how that happens - and the driver cannot tell them apart, because
            // it only ever reports the profile contents. So the sha narrows it to a set and the name
            // we last applied picks one; when that name is gone (renamed, deleted) nobody gets to
            // claim "当前使用" and the header says why instead of guessing.
            var tied = liveSha.Length > 0 ? list.Where(p => p.Sha == liveSha).ToList() : new List<Preset>();
            var active = tied.Count == 1 ? tied[0]
                       : tied.FirstOrDefault(p => string.Equals(p.Name, AppState.Prefs.LastAppliedPlan,
                                                                StringComparison.OrdinalIgnoreCase));

            // After a restart nothing has been clicked yet: highlight the plan that is ACTUALLY
            // running, not row 1. Falling back to row 1 made a successful switch look like the
            // machine had reverted it on boot.
            select ??= (active ?? tied.FirstOrDefault())?.Name;
            var rows = list.Select(p =>
            {
                var profile = Presets.TryLoad(p.File);
                string info = profile == null ? "文件不可读取" :
                    $"垂直倍率 {profile.YxRatio.ToString("0.##", CultureInfo.InvariantCulture)} · 水平加速 {profile.X.Acceleration.ToString("0.###", CultureInfo.InvariantCulture)}";
                string tag = p == active ? "  · 当前使用"
                           : tied.Contains(p) ? "  · 参数与现在这条一样" : "";
                return new PlanRow(p, Label(p) + tag, info);
            }).ToList();
            PlanList.ItemsSource = rows;
            PlanList.SelectedItem = rows.FirstOrDefault(r => r.Plan.Name == select) ?? rows.FirstOrDefault();
            Current.Text = !App.State.DriverReachable ? "驱动未连接"
                : tied.Count == 0 ? "当前使用：自定义参数"
                : active != null ? "当前使用：" + Label(active)
                : $"当前使用：{tied.Count} 份方案参数相同，驱动分不出是哪一份";
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
        // renaming is allowed even for the baseline: it changes nothing about the curve, and a
        // player who wants to call their starting feel something better than "原来的手感" should
        // not have to save a second copy to do it
        Rename.IsEnabled = selected && idle;
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
        try
        {
            if (await App.State.ApplyPresetAsync(name))
            {
                // Only the name we applied can separate this preset from the others that share its
                // numbers. Written down because the driver's answer will be identical for all of them.
                AppState.Prefs.LastAppliedPlan = name;
                AppState.SavePrefs();
            }
        }
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

    /// <summary>
    /// Rename the selected plan. A name is the one thing about a preset that can be changed without
    /// touching what the mouse does, and until now the only way to change one was to save the
    /// current parameters again - which leaves two copies of the same curve behind and no way to
    /// tell which was the one being renamed.
    /// </summary>
    void OnRename(object sender, RoutedEventArgs e)
    {
        var selected = Picked;
        if (selected == null) return;
        Cancel();
        RenameName.Text = selected.Plan.Name;
        RenamePanel.Visibility = Visibility.Visible;
        Notice.Text = Original(selected.Plan)
            ? "「原来的手感」改名后就不再是保留项了 —— 之后它可以被覆盖或删除。"
            : "";
        RenameName.Focus();
        RenameName.SelectAll();
    }

    void OnRenameCancel(object sender, RoutedEventArgs e)
    {
        RenamePanel.Visibility = Visibility.Collapsed;
        Notice.Text = "";
    }

    void OnRenameKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) OnRenameConfirm(sender, e);
        else if (e.Key == System.Windows.Input.Key.Escape) OnRenameCancel(sender, e);
    }

    void OnRenameConfirm(object sender, RoutedEventArgs e)
    {
        var selected = Picked;
        if (selected == null) return;
        string to = Presets.SafeName(RenameName.Text);
        if (to.Length == 0) { Notice.Text = "名字里没留下可用字符（中文、字母、数字都行）。"; return; }

        bool live = string.Equals(AppState.Prefs.LastAppliedPlan, selected.Plan.Name, StringComparison.OrdinalIgnoreCase);
        if (!Presets.Rename(selected.Plan.Name, to, out string error)) { Notice.Text = error; return; }
        if (live)
        {
            // the memory of which plan is live has to follow the curve to its new name
            AppState.Prefs.LastAppliedPlan = to;
            AppState.SavePrefs();
        }

        RenamePanel.Visibility = Visibility.Collapsed;
        Notice.Text = "";
        App.State.Status = "方案已改名为「" + to + "」，曲线没动";
        Refresh(to);
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
        // renaming belongs to one selection: opening another row must not keep the old name box
        if (RenamePanel != null) RenamePanel.Visibility = Visibility.Collapsed;
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
