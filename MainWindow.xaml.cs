using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using TextBox = System.Windows.Controls.TextBox;
using MessageBox = System.Windows.MessageBox;
using SystemPulse.ViewModels;
using SystemPulse.Services;

namespace SystemPulse;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly TrayIconService _trayIcon;
    private readonly UpdateService _updateService = new();
    private UpdateRelease? _availableUpdate;
    private bool _isDarkTheme = true;

    public MainWindow()
    {
        InitializeComponent();
        SetUpdateIconAnimation(updateAvailable: false);
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
        await CheckForUpdatesAsync(showCurrentResult: false);
        if (_viewModel.StartMinimized && _viewModel.MinimizeToTray)
            Hide();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _trayIcon.Dispose();
        _updateService.Dispose();
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

    private void CleanupTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox textBox)
            return;

        _ = textBox.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            textBox.CaretIndex = textBox.Text.Length;
            textBox.ScrollToEnd();
        }));
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        var release = _availableUpdate ?? await CheckForUpdatesAsync(showCurrentResult: true);
        if (release is null)
            return;

        var notes = string.IsNullOrWhiteSpace(release.Notes)
            ? "No release notes were provided."
            : release.Notes.Length > 900 ? release.Notes[..900] + "…" : release.Notes;
        var choice = MessageBox.Show(
            this,
            $"SystemPulse {release.Tag} is available.\n\n{notes}\n\nDownload SystemPulse.exe from GitHub and install it now?",
            "SystemPulse update",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (choice != MessageBoxResult.Yes)
            return;

        UpdateButton.IsEnabled = false;
        UpdateButton.ToolTip = "Downloading update…";
        try
        {
            var progress = new Progress<double>(value => UpdateButton.ToolTip = $"Downloading update… {value:0}%");
            var downloaded = await _updateService.DownloadAsync(release, progress);
            UpdateButton.ToolTip = "Installing update…";
            UpdateService.LaunchInstaller(downloaded);
            Close();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "SystemPulse update", MessageBoxButton.OK, MessageBoxImage.Error);
            UpdateButton.ToolTip = "Update failed; click to try again";
            UpdateButton.IsEnabled = true;
        }
    }

    private async Task<UpdateRelease?> CheckForUpdatesAsync(bool showCurrentResult)
    {
        UpdateButton.IsEnabled = false;
        UpdateButton.ToolTip = "Checking GitHub for updates…";
        try
        {
            var result = await _updateService.CheckAsync();
            if (!result.Success)
            {
                SetUpdateIconAnimation(updateAvailable: false);
                UpdateButton.ToolTip = result.Error;
                if (showCurrentResult)
                    MessageBox.Show(this, result.Error, "SystemPulse update", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }

            if (!result.IsUpdateAvailable || result.Release is null)
            {
                _availableUpdate = null;
                UpdateBadge.Visibility = Visibility.Collapsed;
                SetUpdateIconAnimation(updateAvailable: false);
                UpdateButton.ToolTip = $"SystemPulse {UpdateService.CurrentVersion.ToString(3)} is up to date";
                if (showCurrentResult)
                    MessageBox.Show(this, "You already have the latest version.", "SystemPulse update", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }

            _availableUpdate = result.Release;
            UpdateBadge.Visibility = Visibility.Visible;
            SetUpdateIconAnimation(updateAvailable: true);
            UpdateButton.ToolTip = $"Update {result.Release.Tag} is available";
            return result.Release;
        }
        catch (Exception exception)
        {
            SetUpdateIconAnimation(updateAvailable: false);
            UpdateButton.ToolTip = $"Update check unavailable: {exception.Message}";
            if (showCurrentResult)
                MessageBox.Show(this, $"The update check could not be completed.\n\n{exception.Message}", "SystemPulse update", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        finally
        {
            UpdateButton.IsEnabled = true;
        }
    }

    private void SetUpdateIconAnimation(bool updateAvailable)
    {
        var rotation = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = TimeSpan.FromSeconds(updateAvailable ? 3 : 8),
            RepeatBehavior = RepeatBehavior.Forever
        };
        UpdateIconRotation.BeginAnimation(RotateTransform.AngleProperty, rotation, HandoffBehavior.SnapshotAndReplace);
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
