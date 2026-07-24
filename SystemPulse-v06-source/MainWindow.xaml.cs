using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using SystemPulse.ViewModels;
using SystemPulse.Services;

namespace SystemPulse;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly TrayIconService _trayIcon;
    private bool _isDarkTheme = true;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        _trayIcon = new TrayIconService(ShowFromTray, ExitFromTray);
        _viewModel.AlertRaised += (_, alert) => _trayIcon.Notify(alert.Title, alert.Message);

        Loaded += OnLoaded;
        Closed += OnClosed;
        StateChanged += (_, _) => UpdateMaximizeGlyph();
        SourceInitialized += (_, _) => ApplyWindowAppearance();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.StartAsync();
        if (_viewModel.StartMinimized && _viewModel.MinimizeToTray)
            Hide();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _trayIcon.Dispose();
        _viewModel.Dispose();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.MinimizeToTray)
            Hide();
        else
            WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void UpdateMaximizeGlyph()
    {
        var isMaximized = WindowState == WindowState.Maximized;
        MaximizeIcon.Visibility = isMaximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreIcon.Visibility = isMaximized ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OverviewNav_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(OverviewPage);
        DashboardScroller.ScrollToTop();
    }

    private void PerformanceNav_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(PerformancePage);
        PerformanceScroller.ScrollToTop();
    }

    private void SensorsNav_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(SensorDetailsPage);
    }

    private void CleanupNav_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(StorageCleanupPage);
        CleanupScroller.ScrollToTop();
    }

    private void ProcessesNav_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(ProcessesPage);
        ProcessesScroller.ScrollToTop();
    }

    private void NetworkNav_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(NetworkPage);
        NetworkScroller.ScrollToTop();
    }

    private void StorageNav_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(StorageHealthPage);
        StorageHealthScroller.ScrollToTop();
    }

    private void HistoryNav_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(HistoryPage);
        HistoryScroller.ScrollToTop();
    }

    private void ShowPage(UIElement page)
    {
        foreach (var item in new UIElement[] { OverviewPage, PerformancePage, ProcessesPage, NetworkPage, StorageHealthPage, HistoryPage, SensorDetailsPage, StorageCleanupPage })
            item.Visibility = ReferenceEquals(item, page) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        });
    }

    private void ExitFromTray() => Dispatcher.Invoke(Close);

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        _isDarkTheme = !_isDarkTheme;

        if (_isDarkTheme)
        {
            SetThemeResource("WindowBackgroundColor", "WindowBackground", "#0C0E13");
            SetThemeResource("SurfaceColor", "Surface", "#13161D");
            SetThemeResource("SurfaceRaisedColor", "SurfaceRaised", "#191D26");
            SetThemeResource("BorderColor", "BorderBrush", "#292E3A");
            SetThemeResource("TextPrimaryColor", "TextPrimary", "#F6F7FB");
            SetThemeResource("TextSecondaryColor", "TextSecondary", "#959DAF");
        }
        else
        {
            SetThemeResource("WindowBackgroundColor", "WindowBackground", "#F3F5F7");
            SetThemeResource("SurfaceColor", "Surface", "#FFFFFF");
            SetThemeResource("SurfaceRaisedColor", "SurfaceRaised", "#E9EDF1");
            SetThemeResource("BorderColor", "BorderBrush", "#D5DAE1");
            SetThemeResource("TextPrimaryColor", "TextPrimary", "#171A20");
            SetThemeResource("TextSecondaryColor", "TextSecondary", "#626A78");
        }

        ApplyWindowAppearance();
    }

    private void ChangelogButton_Click(object sender, RoutedEventArgs e)
    {
        var changelog = new ChangelogWindow { Owner = this };
        _ = changelog.ShowDialog();
    }

    private static void SetThemeResource(string colorKey, string brushKey, string value)
    {
        var color = (Color)ColorConverter.ConvertFromString(value);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        System.Windows.Application.Current.Resources[colorKey] = color;
        System.Windows.Application.Current.Resources[brushKey] = brush;
    }

    private void ApplyWindowAppearance()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;

        var darkMode = _isDarkTheme ? 1 : 0;
        var roundedCorners = 2;
        var backdropType = 2;
        _ = DwmSetWindowAttribute(handle, 20, ref darkMode, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 33, ref roundedCorners, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 38, ref backdropType, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr windowHandle, int attribute, ref int value, int valueSize);
}
