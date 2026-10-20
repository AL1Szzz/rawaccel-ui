using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RaUI.View;

namespace RaUI;

public partial class MainWindow : Window
{
    UserControl[] pages;

    public MainWindow()
    {
        InitializeComponent();

        pages = new UserControl[]
        {
            new CurvePage(),
            new DevicePage(),
            new TestPage(),
            new SettingsPage(),
        };
        Host.Content = pages[0];

        var st = App.State;
        st.PropertyChanged += (_, e) => Dispatcher.Invoke(() => SyncStatus(e.PropertyName));
        SyncStatus(null);

        Loaded += (_, _) =>
        {
            var r = Win.TryApplyMica(this);
            if (!r)
            {
                // Windows 10: no Mica, keep the solid dark surface
                ((Border)Content).Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F));
            }
            if (!App.Headless) App.State.AttachMonitor(this);
        };
    }

    void SyncStatus(string prop)
    {
        var st = App.State;
        DriverText.Text = st.DriverInfo;
        Dot.Fill = st.DriverReachable ? (Brush)FindResource("OkBrush") : (Brush)FindResource("WarningBrush");
        StatusText.Text = st.IsDirty ? st.Status + "  ·  有未写入的改动" : st.Status;
        BtnWrite.IsEnabled = st.CanWrite;
        BtnReload.IsEnabled = st.DriverReachable && !st.Busy;
        BtnWrite.Content = st.Busy ? "写入中…" : "写入驱动";
    }

    void OnNav(object sender, SelectionChangedEventArgs e)
    {
        if (Host != null && Nav.SelectedIndex >= 0) Host.Content = pages[Nav.SelectedIndex];
    }

    /// <summary>Used by --dump-ui to render a specific page offscreen.</summary>
    public void GoToPage(int index)
    {
        if (pages == null || index < 0 || index >= pages.Length) return;
        Nav.SelectedIndex = index;
        Host.Content = pages[index];
    }

    void OnMinimize(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void OnMaximize(object s, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void OnClose(object s, RoutedEventArgs e) => Close();

    void OnSave(object s, RoutedEventArgs e) => App.State.SaveToFile();
    void OnReload(object s, RoutedEventArgs e) => App.State.ReloadFromDriver();
    async void OnWrite(object s, RoutedEventArgs e) => await App.State.WriteToDriverAsync();
}
