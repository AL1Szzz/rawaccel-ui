using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using RaCore;

namespace RaUI.View;

/// <summary>
/// Archive-based log cleanup. Groups are (config, setup); a group is PRE-CHECKED when every
/// round in it came from a build whose open-loop reconstruction was broken, or when it
/// produced no clicks at all - those rounds cannot contribute to a tuning verdict anyway.
/// Archiving moves files and prunes index.csv; it never deletes.
/// </summary>
public partial class CleanupWindow : Window
{
    public event Action? Changed;

    sealed class GroupBox : CheckBox
    {
        public required List<RoundSummary> Rounds;
        public required long Bytes;
        public required bool PreChecked;
        public required string Reason;
    }

    public CleanupWindow(List<RoundSummary> rounds)
    {
        InitializeComponent();
        foreach (var g in History.Group(rounds))
        {
            long bytes = g.Rounds.Sum(r => TrySize(r.File));
            bool allStale = g.Rounds.All(r => !r.OpenLoopUsable);
            bool allEmpty = g.Rounds.All(r => r.Clicks == 0);
            string reason = allStale ? "开环数字不可用（旧构建），判参用不上"
                          : allEmpty ? "一次点击都没有的空轮"
                          : g.Rounds.Any(r => !r.OpenLoopUsable) ? "混有旧构建轮次（未全选）" : "";
            var box = new GroupBox
            {
                Rounds = g.Rounds,
                Bytes = bytes,
                PreChecked = allStale || allEmpty,
                Reason = reason,
                Margin = new Thickness(0, 2, 0, 2),
            };
            var head = new TextBlock
            {
                Inlines =
                {
                    new System.Windows.Documents.Bold(new Run(
                        (g.Sha.Length > 6 ? g.Sha[..6] : g.Sha) + " · " + g.Cond() + " · " + g.Rounds.Count + "轮 · " + g.TimeRange())),
                    new Run($"  {bytes / 1024.0:0} KB") { Foreground = Brushes.Gray },
                },
                TextWrapping = TextWrapping.Wrap,
            };
            if (reason.Length > 0)
            {
                head.Inlines.Add(new Run("   " + reason)
                {
                    Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xC2, 0x4C)),
                });
            }
            box.Content = head;
            box.IsChecked = box.PreChecked;
            GroupList.Children.Add(box);
        }
        if (GroupList.Children.Count == 0)
            GroupList.Children.Add(new TextBlock { Text = "没有可分组的日志。" });
    }

    static long TrySize(string f) { try { return new FileInfo(f).Length; } catch { return 0; } }

    void OnToggleAll(object sender, RoutedEventArgs e)
    {
        bool anyUnchecked = GroupList.Children.OfType<CheckBox>().Any(c => c.IsChecked != true);
        foreach (var c in GroupList.Children.OfType<CheckBox>()) c.IsChecked = anyUnchecked;
    }

    void OnArchive(object sender, RoutedEventArgs e)
    {
        var files = GroupList.Children.OfType<GroupBox>()
            .Where(b => b.IsChecked == true)
            .SelectMany(b => b.Rounds)
            .Select(r => r.File)
            .Where(File.Exists)
            .ToList();
        if (files.Count == 0) { Result.Text = "没有勾选任何组。"; return; }

        BtnArchive.IsEnabled = false;
        var res = LogArchive.Archive(files);
        string msg = $"已归档 {res.Moved} 份日志 → {res.ArchiveDir}";
        if (res.Failed.Count > 0) msg += "；失败 " + res.Failed.Count + " 份：" + string.Join("；", res.Failed.Take(3));
        Result.Text = msg;
        App.State.Status = msg;
        Changed?.Invoke();
        BtnArchive.IsEnabled = true;
    }

    void OnClose(object sender, RoutedEventArgs e) => Close();
}
