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
    private readonly bool _startInBackground;
    private readonly MainViewModel _viewModel;
    private readonly TrayIconService _trayIcon;
    private readonly UpdateService _updateService = new();
    private readonly OverclockService _overclockService = new();
    private readonly GameModeService _gameModeService = new();
    private readonly DispatcherTimer _updateCheckTimer = new()
    {
        Interval = TimeSpan.FromSeconds(120)
    };
    private UpdateRelease? _availableUpdate;
    private OverclockCapabilities? _overclockCapabilities;
    private bool _isCheckingForUpdates;
    private bool _isInstallingUpdate;
    private bool _isPromptingUpdate;
    private bool _isDetectingOverclock;
    private bool _isApplyingOverclock;
    private bool _isChangingGameMode;
    private bool _isDarkTheme = true;

    public MainWindow(bool startInBackground = false)
    {
        _startInBackground = startInBackground;
        InitializeComponent();
        if (_startInBackground)
        {
            ShowActivated = false;
            ShowInTaskbar = false;
            WindowState = WindowState.Minimized;
        }
        FitInitialWindowToWorkArea();
        SetUpdateIconAnimation(updateAvailable: false);
        RefreshGameModeUi();
        _gameModeService.SetSessionAwake(_gameModeService.IsEnabled);
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        _trayIcon = new TrayIconService(ShowFromTray, ExitFromTray);
        _viewModel.AlertRaised += (_, alert) => _trayIcon.Notify(alert.Title, alert.Message);
        _updateCheckTimer.Tick += UpdateCheckTimer_Tick;

        Loaded += OnLoaded;
        Closed += OnClosed;
        StateChanged += (_, _) => UpdateMaximizeGlyph();
        SourceInitialized += (_, _) => ApplyWindowAppearance();
    }

    private void FitInitialWindowToWorkArea()
    {
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(Width, Math.Max(MinWidth, workArea.Width - 48));
        Height = Math.Min(Height, Math.Max(MinHeight, workArea.Height - 48));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_startInBackground)
                Hide();
            await _viewModel.StartAsync();
            var startupUpdate = await CheckForUpdatesAsync(showCurrentResult: false);
            if (startupUpdate is not null)
            {
                await HandleDetectedUpdateAsync(startupUpdate);
                if (_isInstallingUpdate)
                    return;
            }
            await InitializeOverclockPageAsync();
            _updateCheckTimer.Start();
            if (_viewModel.StartMinimized && _viewModel.MinimizeToTray)
                Hide();
        }
        catch (Exception exception)
        {
            _updateCheckTimer.Start();
            UpdateButton.ToolTip = $"A startup service could not initialize: {exception.Message}";
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _updateCheckTimer.Stop();
        _trayIcon.Dispose();
        _updateService.Dispose();
        FanControlPage.Dispose();
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

    private async void FanControlNav_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(FanControlPage);
        if (FanControlPage.ViewModel.Channels.Count == 0)
            await FanControlPage.ViewModel.DiscoverAsync();
    }

    private async void OverclockNav_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(OverclockPage);
        OverclockScroller.ScrollToTop();
        if (_overclockCapabilities is null)
        {
            try
            {
                await InitializeOverclockPageAsync();
            }
            catch (Exception exception)
            {
                OverclockStatusText.Text = $"Tuning detection unavailable: {exception.Message}";
            }
        }
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
        foreach (var item in new UIElement[] { OverviewPage, PerformancePage, FanControlPage, OverclockPage, ProcessesPage, NetworkPage, StorageHealthPage, HistoryPage, SensorDetailsPage, StorageCleanupPage })
            item.Visibility = ReferenceEquals(item, page) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            var restoredState = WindowState == WindowState.Minimized
                ? WindowState.Normal
                : WindowState;

            // Background update launches intentionally disable activation. Re-enable it
            // before showing the window because WPF cannot show a maximized window while
            // ShowActivated is false.
            ShowActivated = true;
            ShowInTaskbar = true;
            WindowState = restoredState;
            if (!IsVisible)
                Show();
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

    private async void RamCleanupButton_Click(object sender, RoutedEventArgs e)
    {
        RamCleanupButton.IsEnabled = false;
        RamCleanupButtonLabel.Text = "Clearing…";
        RamCleanupButton.ToolTip = "Trimming reclaimable memory from user applications…";

        try
        {
            var result = await Task.Run(RamCleanupService.TrimUserWorkingSets);
            var freed = FormatMemorySize(result.AvailableIncreaseBytes);
            RamCleanupButtonLabel.Text = result.AvailableIncreaseBytes > 0
                ? $"Freed {freed}"
                : $"Trimmed {result.TrimmedProcesses}";
            RamCleanupButton.ToolTip = result.AvailableIncreaseBytes > 0
                ? $"Windows made {freed} more physical memory available after trimming {result.TrimmedProcesses} user applications."
                : $"Trimmed {result.TrimmedProcesses} user applications. Windows already had their memory available for reuse.";
            await Task.Delay(TimeSpan.FromSeconds(4));
        }
        catch (Exception exception)
        {
            RamCleanupButtonLabel.Text = "Could not clear";
            RamCleanupButton.ToolTip = $"RAM cleanup could not finish: {exception.Message}";
            await Task.Delay(TimeSpan.FromSeconds(4));
        }
        finally
        {
            RamCleanupButtonLabel.Text = "Free unused RAM";
            RamCleanupButton.ToolTip = "Trim reclaimable physical memory from user applications";
            RamCleanupButton.IsEnabled = true;
        }
    }

    private async void GameModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isChangingGameMode)
            return;

        _isChangingGameMode = true;
        GameModeButton.IsEnabled = false;
        var wasEnabled = _gameModeService.IsEnabled;
        GameModeButtonLabel.Text = wasEnabled ? "Restoring…" : "Enabling…";
        try
        {
            var result = wasEnabled
                ? await _gameModeService.DisableAsync()
                : await _gameModeService.EnableAsync();
            if (result.Success)
                _gameModeService.SetSessionAwake(!wasEnabled);
            GameModeButton.ToolTip = result.Message;
            if (!result.Success)
                MessageBox.Show(this, result.Message, "SystemPulse Game Mode", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception exception)
        {
            GameModeButton.ToolTip = $"Game Mode could not be changed: {exception.Message}";
            MessageBox.Show(this, GameModeButton.ToolTip.ToString(), "SystemPulse Game Mode", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _isChangingGameMode = false;
            GameModeButton.IsEnabled = true;
            RefreshGameModeUi();
        }
    }

    private void RefreshGameModeUi()
    {
        if (GameModeButton is null || GameModeButtonLabel is null)
            return;

        var enabled = _gameModeService.IsEnabled;
        GameModeButtonLabel.Text = enabled ? "Game Mode: On" : "Game Mode: Off";
        GameModeButton.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, enabled ? "Purple" : "SurfaceRaised");
        GameModeButton.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, enabled ? "WindowBackground" : "TextPrimary");
        GameModeButton.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, enabled ? "Purple" : "BorderBrush");
    }

    private static string FormatMemorySize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / 1024d / 1024d / 1024d:0.#} GB";
        return $"{bytes / 1024d / 1024d:0} MB";
    }

    private async Task InitializeOverclockPageAsync()
    {
        if (_isDetectingOverclock)
            return;

        _isDetectingOverclock = true;
        OverclockStatusText.Text = "Detecting tuning support…";
        try
        {
            var capabilities = await _overclockService.DetectAsync();
            _overclockCapabilities = capabilities;

            OverclockCpuName.Text = capabilities.CpuName;
            OverclockGpuName.Text = capabilities.GpuName;
            CpuTuningBackendText.Text = capabilities.CpuBackend;
            GpuTuningBackendText.Text = capabilities.GpuBackend;
            CpuVendorTunerButtonLabel.Text = $"Open {capabilities.CpuToolLabel}";
            GpuVendorTunerButtonLabel.Text = $"Open {capabilities.GpuToolLabel}";

            SetTuningSlider(
                CpuCoreClockSlider, CpuCoreClockValue,
                capabilities.CanSetCpuCoreClock,
                capabilities.CpuCoreMinimum, capabilities.CpuCoreMaximum, capabilities.CpuCoreCurrent,
                100, "MHz", "Firmware controlled");
            SetTuningSlider(
                CpuMemoryClockSlider, CpuMemoryClockValue,
                capabilities.CanSetCpuMemoryClock,
                0, 0, 0, 100, "MHz", "Firmware controlled");
            SetTuningSlider(
                CpuVoltageSlider, CpuVoltageValue,
                capabilities.CanSetCpuVoltage,
                0, 0, 0, 5, "mV", "Firmware controlled");
            SetTuningSlider(
                CpuPowerSlider, CpuPowerValue,
                capabilities.CanSetCpuPower,
                capabilities.CpuPowerMinimum, capabilities.CpuPowerMaximum, capabilities.CpuPowerCurrent,
                1, "W", "Firmware controlled");

            SetTuningSlider(
                GpuCoreClockSlider, GpuCoreClockValue,
                capabilities.CanSetGpuCoreClock,
                capabilities.GpuCoreMinimum, capabilities.GpuCoreMaximum, capabilities.GpuCoreCurrent,
                15, "MHz", "Driver controlled");
            SetTuningSlider(
                GpuMemoryClockSlider, GpuMemoryClockValue,
                capabilities.CanSetGpuMemoryClock,
                capabilities.GpuMemoryMinimum, capabilities.GpuMemoryMaximum, capabilities.GpuMemoryCurrent,
                25, "MHz", "Driver controlled");
            SetTuningSlider(
                GpuVoltageSlider, GpuVoltageValue,
                capabilities.CanSetGpuVoltage,
                capabilities.GpuVoltageMinimum, capabilities.GpuVoltageMaximum, capabilities.GpuVoltageCurrent,
                5, "mV", "Driver controlled");
            SetTuningSlider(
                GpuPowerSlider, GpuPowerValue,
                capabilities.CanSetGpuPower,
                capabilities.GpuPowerMinimum, capabilities.GpuPowerMaximum, capabilities.GpuPowerCurrent,
                1, "W", "Driver controlled");

            var canApplyGpu = capabilities.CanSetGpuCoreClock ||
                              capabilities.CanSetGpuMemoryClock ||
                              capabilities.CanSetGpuVoltage ||
                              capabilities.CanSetGpuPower;
            ApplyGpuTuningButton.IsEnabled = canApplyGpu;
            ResetGpuTuningButton.IsEnabled = canApplyGpu;
            var canApplyCpu = capabilities.CanSetCpuCoreClock || capabilities.CanSetCpuPower;
            ApplyCpuTuningButton.IsEnabled = canApplyCpu;
            ResetCpuTuningButton.IsEnabled = canApplyCpu;
            OverclockStatusText.Text = $"{capabilities.CpuVendor} CPU · {capabilities.GpuVendor} GPU · capability check complete";
        }
        catch (Exception exception)
        {
            OverclockStatusText.Text = "Tuning capability check failed";
            GpuTuningResultText.Text = exception.Message;
        }
        finally
        {
            _isDetectingOverclock = false;
        }
    }

    private void CpuTuningSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CpuCoreClockValue is null || CpuMemoryClockValue is null ||
            CpuVoltageValue is null || CpuPowerValue is null)
            return;

        if (CpuCoreClockSlider.IsEnabled)
            CpuCoreClockValue.Text = $"{CpuCoreClockSlider.Value:0} MHz";
        if (CpuMemoryClockSlider.IsEnabled)
            CpuMemoryClockValue.Text = $"{CpuMemoryClockSlider.Value:0} MHz";
        if (CpuVoltageSlider.IsEnabled)
            CpuVoltageValue.Text = $"{CpuVoltageSlider.Value:0} mV";
        if (CpuPowerSlider.IsEnabled)
            CpuPowerValue.Text = $"{CpuPowerSlider.Value:0.#} W";
    }

    private async void ApplyCpuTuningButton_Click(object sender, RoutedEventArgs e)
    {
        if (_overclockCapabilities is null || _isApplyingOverclock)
            return;

        var profile = new OverclockProfile(
            CpuCoreClockSlider.Value,
            CpuMemoryClockSlider.Value,
            CpuVoltageSlider.Value,
            CpuPowerSlider.Value);
        var confirmation = MessageBox.Show(
            this,
            $"Apply the supported Intel tuning values to {_overclockCapabilities.CpuName}?\n\n" +
            $"All-core turbo target: {CpuCoreClockValue.Text}\n" +
            $"Package power limit: {CpuPowerValue.Text}\n\n" +
            "Only controls verified through PawnIO will be written. Overclocking can cause crashes, extra heat, data loss, or hardware damage. Continue only if cooling and power delivery are adequate.",
            "Apply Intel CPU profile",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
            return;

        SetCpuTuningBusy(true, "Applying verified Intel CPU controls...");
        try
        {
            var result = await _overclockService.ApplyCpuAsync(_overclockCapabilities, profile);
            CpuTuningResultText.Text = result.Message;
            OverclockStatusText.Text = result.Success ? "CPU profile applied" : "CPU profile rejected";
        }
        catch (Exception exception)
        {
            CpuTuningResultText.Text = $"CPU tuning failed safely: {exception.Message}";
            OverclockStatusText.Text = "CPU profile rejected";
        }
        finally
        {
            SetCpuTuningBusy(false, null);
        }
    }

    private async void ResetCpuTuningButton_Click(object sender, RoutedEventArgs e)
    {
        if (_overclockCapabilities is null || _isApplyingOverclock)
            return;

        var confirmation = MessageBox.Show(
            this,
            "Restore the Intel CPU ratio and package power controls to the values captured when this tuning session began?",
            "Restore CPU baseline",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes)
            return;

        SetCpuTuningBusy(true, "Restoring Intel CPU baseline...");
        try
        {
            var result = await _overclockService.ResetCpuAsync(_overclockCapabilities);
            CpuTuningResultText.Text = result.Message;
            OverclockStatusText.Text = result.Success ? "CPU baseline restored" : "CPU reset needs attention";
            if (result.Success)
                await InitializeOverclockPageAsync();
        }
        catch (Exception exception)
        {
            CpuTuningResultText.Text = $"CPU baseline restore failed safely: {exception.Message}";
            OverclockStatusText.Text = "CPU reset needs attention";
        }
        finally
        {
            SetCpuTuningBusy(false, null);
        }
    }

    private void SetCpuTuningBusy(bool busy, string? status)
    {
        _isApplyingOverclock = busy;
        var capabilities = _overclockCapabilities;
        var supported = capabilities is not null &&
                        (capabilities.CanSetCpuCoreClock || capabilities.CanSetCpuPower);
        ApplyCpuTuningButton.IsEnabled = !busy && supported;
        ResetCpuTuningButton.IsEnabled = !busy && supported;
        CpuVendorTunerButton.IsEnabled = !busy;
        if (status is not null)
            CpuTuningResultText.Text = status;
    }

    private static void SetTuningSlider(
        Slider slider,
        TextBlock valueLabel,
        bool enabled,
        double minimum,
        double maximum,
        double current,
        double step,
        string unit,
        string unavailableText)
    {
        slider.IsEnabled = enabled;
        if (!enabled || maximum <= minimum)
        {
            valueLabel.Text = unavailableText;
            return;
        }

        slider.Minimum = Math.Floor(minimum);
        slider.Maximum = Math.Ceiling(maximum);
        slider.TickFrequency = step;
        slider.SmallChange = step;
        slider.LargeChange = step * 4;
        slider.IsSnapToTickEnabled = true;
        slider.Value = Math.Clamp(current, slider.Minimum, slider.Maximum);
        valueLabel.Text = unit == "W" ? $"{slider.Value:0.#} {unit}" : $"{slider.Value:0} {unit}";
    }

    private void GpuTuningSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GpuCoreClockValue is null || GpuMemoryClockValue is null ||
            GpuVoltageValue is null || GpuPowerValue is null)
            return;

        if (GpuCoreClockSlider.IsEnabled)
            GpuCoreClockValue.Text = $"{GpuCoreClockSlider.Value:0} MHz";
        if (GpuMemoryClockSlider.IsEnabled)
            GpuMemoryClockValue.Text = $"{GpuMemoryClockSlider.Value:0} MHz";
        if (GpuVoltageSlider.IsEnabled)
            GpuVoltageValue.Text = $"{GpuVoltageSlider.Value:0} mV";
        if (GpuPowerSlider.IsEnabled)
            GpuPowerValue.Text = $"{GpuPowerSlider.Value:0.#} W";
    }

    private async void ApplyGpuTuningButton_Click(object sender, RoutedEventArgs e)
    {
        if (_overclockCapabilities is null || _isApplyingOverclock)
            return;

        var profile = new OverclockProfile(
            GpuCoreClockSlider.Value,
            GpuMemoryClockSlider.Value,
            GpuVoltageSlider.Value,
            GpuPowerSlider.Value);
        var confirmation = MessageBox.Show(
            this,
            $"Apply the supported tuning values to {_overclockCapabilities.GpuName}?\n\n" +
            $"Core target: {GpuCoreClockValue.Text}\n" +
            $"Memory target: {GpuMemoryClockValue.Text}\n" +
            $"Voltage: {GpuVoltageValue.Text}\n" +
            $"Power limit: {GpuPowerValue.Text}\n\n" +
            "Overclocking can cause instability, extra heat, data loss, or hardware damage. Continue only if cooling and power delivery are adequate.",
            "Apply overclock profile",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
            return;

        SetGpuTuningBusy(true, "Applying supported GPU controls…");
        try
        {
            var result = await _overclockService.ApplyGpuAsync(_overclockCapabilities, profile);
            GpuTuningResultText.Text = result.Message;
            OverclockStatusText.Text = result.Success ? "GPU profile applied" : "GPU profile rejected";
        }
        catch (Exception exception)
        {
            GpuTuningResultText.Text = $"GPU tuning failed safely: {exception.Message}";
            OverclockStatusText.Text = "GPU profile rejected";
        }
        finally
        {
            SetGpuTuningBusy(false, null);
        }
    }

    private async void ResetGpuTuningButton_Click(object sender, RoutedEventArgs e)
    {
        if (_overclockCapabilities is null || _isApplyingOverclock)
            return;

        var confirmation = MessageBox.Show(
            this,
            "Restore the GPU clock controls and power limit to the values reported by the driver?",
            "Restore GPU defaults",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes)
            return;

        SetGpuTuningBusy(true, "Restoring GPU defaults…");
        try
        {
            var result = await _overclockService.ResetGpuAsync(_overclockCapabilities);
            GpuTuningResultText.Text = result.Message;
            OverclockStatusText.Text = result.Success ? "GPU defaults restored" : "GPU reset needs attention";
            if (result.Success)
                await InitializeOverclockPageAsync();
        }
        catch (Exception exception)
        {
            GpuTuningResultText.Text = $"GPU default restore failed safely: {exception.Message}";
            OverclockStatusText.Text = "GPU reset needs attention";
        }
        finally
        {
            SetGpuTuningBusy(false, null);
        }
    }

    private void SetGpuTuningBusy(bool busy, string? status)
    {
        _isApplyingOverclock = busy;
        var capabilities = _overclockCapabilities;
        var supported = capabilities is not null &&
                        (capabilities.CanSetGpuCoreClock || capabilities.CanSetGpuMemoryClock ||
                         capabilities.CanSetGpuVoltage || capabilities.CanSetGpuPower);
        ApplyGpuTuningButton.IsEnabled = !busy && supported;
        ResetGpuTuningButton.IsEnabled = !busy && supported;
        GpuVendorTunerButton.IsEnabled = !busy;
        if (status is not null)
            GpuTuningResultText.Text = status;
    }

    private void CpuVendorTunerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_overclockCapabilities is null)
            return;
        var result = OverclockService.OpenVendorTuner(
            _overclockCapabilities.CpuToolPath,
            _overclockCapabilities.CpuToolUrl,
            _overclockCapabilities.CpuToolLabel);
        CpuTuningBackendText.Text = result.Message;
    }

    private void GpuVendorTunerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_overclockCapabilities is null)
            return;
        var result = OverclockService.OpenVendorTuner(
            _overclockCapabilities.GpuToolPath,
            _overclockCapabilities.GpuToolUrl,
            _overclockCapabilities.GpuToolLabel);
        GpuTuningResultText.Text = result.Message;
    }

    private void ChangelogButton_Click(object sender, RoutedEventArgs e)
    {
        var changelog = new ChangelogWindow { Owner = this };
        _ = changelog.ShowDialog();
    }

    private void SuggestionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.PerformanceSuggestions.Count == 0)
            return;

        var suggestions = new SuggestionsWindow(
            _viewModel.PerformanceSuggestionsTitle,
            _viewModel.PerformanceSuggestions.ToArray(),
            _viewModel.PerformanceCloseCandidates.ToArray())
        {
            Owner = this
        };
        _ = suggestions.ShowDialog();
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
        try
        {
            if (_isInstallingUpdate)
                return;

            var release = _availableUpdate ?? await CheckForUpdatesAsync(showCurrentResult: true);
            if (release is null)
                return;

            await InstallUpdateAsync(release, showErrors: true);
        }
        catch (Exception exception)
        {
            UpdateButton.IsEnabled = true;
            UpdateButton.ToolTip = $"Update check failed: {exception.Message}";
        }
    }

    private async Task InstallUpdateAsync(UpdateRelease release, bool showErrors)
    {
        if (_isInstallingUpdate)
            return;

        _isInstallingUpdate = true;
        UpdateButton.IsEnabled = false;
        UpdateButton.ToolTip = "Downloading update…";
        try
        {
            _viewModel.SaveSettings();
            var progress = new Progress<double>(value => UpdateButton.ToolTip = $"Downloading update… {value:0}%");
            var downloaded = await _updateService.DownloadAsync(release, progress);
            UpdateButton.ToolTip = "Installing update…";
            _viewModel.SaveSettings();
            UpdateService.LaunchInstaller(downloaded);
            Close();
        }
        catch (Exception exception)
        {
            if (showErrors)
                MessageBox.Show(this, exception.Message, "SystemPulse update", MessageBoxButton.OK, MessageBoxImage.Error);
            UpdateButton.ToolTip = "Update failed; click to try again";
            UpdateButton.IsEnabled = true;
            _isInstallingUpdate = false;
        }
    }

    private async Task HandleDetectedUpdateAsync(UpdateRelease release)
    {
        if (_isInstallingUpdate || _isPromptingUpdate || _viewModel.IsUpdateIgnored(release.Version))
            return;

        _isPromptingUpdate = true;
        try
        {
            var prompt = new UpdatePromptWindow(release) { Owner = this };
            _ = prompt.ShowDialog();
            if (prompt.Choice == UpdatePromptChoice.Skip)
            {
                _viewModel.IgnoreUpdate(release.Version);
                UpdateButton.ToolTip = $"Update {release.Tag} was skipped; click to install it manually";
                SetUpdateIconAnimation(updateAvailable: true);
                return;
            }

            await InstallUpdateAsync(release, showErrors: false);
        }
        finally
        {
            _isPromptingUpdate = false;
        }
    }

    private async void UpdateCheckTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            if (!_isInstallingUpdate)
            {
                var release = await CheckForUpdatesAsync(showCurrentResult: false);
                if (release is not null)
                    await HandleDetectedUpdateAsync(release);
            }
        }
        catch (Exception exception)
        {
            UpdateButton.ToolTip = $"Automatic update check failed: {exception.Message}";
        }
    }

    private async Task<UpdateRelease?> CheckForUpdatesAsync(bool showCurrentResult)
    {
        if (_isCheckingForUpdates || _isInstallingUpdate)
            return _availableUpdate;

        _isCheckingForUpdates = true;
        UpdateButton.IsEnabled = false;
        UpdateButton.ToolTip = "Checking GitHub for updates…";
        try
        {
            var result = await _updateService.CheckAsync();
            if (!result.Success)
            {
                SetUpdateIconAnimation(updateAvailable: _availableUpdate is not null);
                UpdateButton.ToolTip = result.Error;
                if (showCurrentResult)
                    MessageBox.Show(this, result.Error, "SystemPulse update", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }

            if (!result.IsUpdateAvailable || result.Release is null)
            {
                _availableUpdate = null;
                SetUpdateIconAnimation(updateAvailable: false);
                UpdateButton.ToolTip = $"SystemPulse {UpdateService.FormatVersion(UpdateService.CurrentVersion)} is up to date";
                if (showCurrentResult)
                    MessageBox.Show(this, "You already have the latest version.", "SystemPulse update", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }

            _availableUpdate = result.Release;
            SetUpdateIconAnimation(updateAvailable: true);
            UpdateButton.ToolTip = $"Update {result.Release.Tag} is available";
            return result.Release;
        }
        catch (Exception exception)
        {
            SetUpdateIconAnimation(updateAvailable: _availableUpdate is not null);
            UpdateButton.ToolTip = $"Update check unavailable: {exception.Message}";
            if (showCurrentResult)
                MessageBox.Show(this, $"The update check could not be completed.\n\n{exception.Message}", "SystemPulse update", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        finally
        {
            _isCheckingForUpdates = false;
            if (!_isInstallingUpdate)
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

        UpdateBadge.BeginAnimation(UIElement.OpacityProperty, null);
        UpdateBadge.Visibility = updateAvailable ? Visibility.Visible : Visibility.Collapsed;
        UpdateAvailableText.Visibility = updateAvailable ? Visibility.Visible : Visibility.Collapsed;
        if (!updateAvailable)
            return;

        var badgeToggle = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(2.4),
            RepeatBehavior = RepeatBehavior.Forever
        };
        badgeToggle.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        badgeToggle.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromPercent(0.5)));
        UpdateBadge.BeginAnimation(UIElement.OpacityProperty, badgeToggle, HandoffBehavior.SnapshotAndReplace);
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
