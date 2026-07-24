using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SystemPulse.Models;
using SystemPulse.Services;
using SystemPulse.Services.PawnIo;

namespace SystemPulse.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private const int HistoryLimit = 60;
    private readonly HardwareMonitorService _monitor;
    private readonly ProcessTelemetryService _processTelemetry = new();
    private readonly NetworkTelemetryService _networkTelemetry = new();
    private readonly TelemetryHistoryService _history = new();
    private readonly AlertService _alerts = new();
    private readonly SettingsService _settingsService = new();
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, Queue<double>> _storageHistoryByDevice = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Queue<double>> _storageLoadHistoryByDevice = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, Queue<double>> _frameHistoryByProcess = new();
    private bool _isReading;
    private bool _disposed;
    private int _refreshSeconds = 2;
    private string _lastUpdated = "Waiting for first reading";
    private string _statusMessage = "Starting PawnIO sensor monitor…";
    private string _cpuName = "CPU";
    private string _gpuName = "GPU";
    private string _memoryName = "System memory";
    private string _cpuTemperature = "—";
    private string _cpuTemperatureSource = "Detecting";
    private string _gpuTemperature = "—";
    private string _cpuVoltage = "Unavailable";
    private string _cpuPower = "Unavailable";
    private string _cpuElectricalSource = "Detecting CPU electrical sensors";
    private string _gpuVoltage = "Unavailable";
    private string _gpuPower = "Unavailable";
    private string _gpuElectricalSource = "Detecting GPU electrical sensors";
    private string _memoryTemperature = "—";
    private string _cpuLoad = "—";
    private string _gpuLoad = "—";
    private string _memoryLoad = "—";
    private string _fanSpeed = "—";
    private string _cpuStatus = "Detecting";
    private string _gpuStatus = "Detecting";
    private string _memoryStatus = "Detecting";
    private string _cpuStatusColor = "#959DAF";
    private string _gpuStatusColor = "#959DAF";
    private string _memoryStatusColor = "#959DAF";
    private StorageDeviceItem? _selectedStorageDevice;
    private FrameApplicationItem? _selectedFrameApplication;
    private string _storageTemperature = "Unavailable";
    private string _storageDetails = "No physical storage detected";
    private string _storageStatus = "Detecting";
    private string _storageStatusColor = "#959DAF";
    private string _storageLoad = "Unavailable";
    private string _storageReadRate = "Unavailable";
    private string _storageWriteRate = "Unavailable";
    private string _frameTime = "Unavailable";
    private string _framesPerSecond = "—";
    private string _frameProcess = "No active 3D presentation";
    private string _systemHealthHeadline = "LIVE MONITORING";
    private string _systemHealthMessage = "System is nominal";
    private string _systemHealthColor = "#58D6C7";
    private string _systemHealthBackground = "#0B58D6C7";
    private string _systemHealthBorder = "#3058D6C7";
    private string _motherboardTemperature = "Unavailable";
    private string _motherboardSource = "Detecting ACPI thermal zones";
    private string _motherboardStatus = "Detecting";
    private string _motherboardStatusColor = "#959DAF";
    private bool _hasElevatedAccess;
    private bool _isPawnIoReady;
    private string _driverStatus = "Detecting PawnIO";
    private string _cpuSensorSummary = "Detecting logical processors";
    private string _historyStatus = "Waiting for the first persistent sample";
    private string _processSummary = "Collecting process activity";
    private string _networkSummary = "Collecting adapter activity";
    private string _storageSummary = "Detecting physical drives";

    public MainViewModel()
    {
        _settings = _settingsService.Load();
        StorageCleanup = new StorageCleanupViewModel();
        _monitor = new HardwareMonitorService();
        _refreshSeconds = Math.Clamp(_settings.RefreshSeconds, 1, 30);
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(_refreshSeconds)
        };
        _timer.Tick += async (_, _) => await RefreshAsync();

        RefreshCommand = new RelayCommand(async _ => await RefreshAsync(), _ => !_isReading);
        RestartElevatedCommand = new RelayCommand(_ => RestartElevated());
        InstallPawnIoCommand = new RelayCommand(async _ => await InstallPawnIoAsync(force: true));
        ExportHistoryCommand = new RelayCommand(_ => ExportHistory());
        OpenHistoryFolderCommand = new RelayCommand(_ => OpenHistoryFolder());
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<MonitorAlert>? AlertRaised;

    public StorageCleanupViewModel StorageCleanup { get; }

    public ObservableCollection<double> CpuTemperatureHistory { get; } = new();
    public ObservableCollection<double> GpuTemperatureHistory { get; } = new();
    public ObservableCollection<double> MemoryLoadHistory { get; } = new();
    public ObservableCollection<double> StorageTemperatureHistory { get; } = new();
    public ObservableCollection<double> MotherboardTemperatureHistory { get; } = new();
    public ObservableCollection<double> CpuLoadHistory { get; } = new();
    public ObservableCollection<double> GpuLoadHistory { get; } = new();
    public ObservableCollection<double> FrameTimeHistory { get; } = new();
    public ObservableCollection<double> StorageLoadHistory { get; } = new();
    public ObservableCollection<StorageDeviceItem> StorageDevices { get; } = new();
    public ObservableCollection<FrameApplicationItem> FrameApplications { get; } = new();
    public ObservableCollection<ProcessTelemetryItem> Processes { get; } = new();
    public ObservableCollection<NetworkAdapterItem> NetworkAdapters { get; } = new();
    public ObservableCollection<HistoryItem> RecentHistory { get; } = new();
    public ObservableCollection<AlertItem> RecentAlerts { get; } = new();

    public ICommand RefreshCommand { get; }
    public ICommand RestartElevatedCommand { get; }
    public ICommand InstallPawnIoCommand { get; }
    public ICommand ExportHistoryCommand { get; }
    public ICommand OpenHistoryFolderCommand { get; }

    public string LastUpdated { get => _lastUpdated; private set => Set(ref _lastUpdated, value); }
    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }
    public string CpuName { get => _cpuName; private set => Set(ref _cpuName, value); }
    public string GpuName { get => _gpuName; private set => Set(ref _gpuName, value); }
    public string MemoryName { get => _memoryName; private set => Set(ref _memoryName, value); }
    public string CpuTemperature { get => _cpuTemperature; private set => Set(ref _cpuTemperature, value); }
    public string CpuTemperatureSource { get => _cpuTemperatureSource; private set => Set(ref _cpuTemperatureSource, value); }
    public string GpuTemperature { get => _gpuTemperature; private set => Set(ref _gpuTemperature, value); }
    public string CpuVoltage { get => _cpuVoltage; private set => Set(ref _cpuVoltage, value); }
    public string CpuPower { get => _cpuPower; private set => Set(ref _cpuPower, value); }
    public string CpuElectricalSource { get => _cpuElectricalSource; private set => Set(ref _cpuElectricalSource, value); }
    public string GpuVoltage { get => _gpuVoltage; private set => Set(ref _gpuVoltage, value); }
    public string GpuPower { get => _gpuPower; private set => Set(ref _gpuPower, value); }
    public string GpuElectricalSource { get => _gpuElectricalSource; private set => Set(ref _gpuElectricalSource, value); }
    public string MemoryTemperature { get => _memoryTemperature; private set => Set(ref _memoryTemperature, value); }
    public string CpuLoad { get => _cpuLoad; private set => Set(ref _cpuLoad, value); }
    public string GpuLoad { get => _gpuLoad; private set => Set(ref _gpuLoad, value); }
    public string MemoryLoad { get => _memoryLoad; private set => Set(ref _memoryLoad, value); }
    public string FanSpeed { get => _fanSpeed; private set => Set(ref _fanSpeed, value); }
    public string CpuStatus { get => _cpuStatus; private set => Set(ref _cpuStatus, value); }
    public string GpuStatus { get => _gpuStatus; private set => Set(ref _gpuStatus, value); }
    public string MemoryStatus { get => _memoryStatus; private set => Set(ref _memoryStatus, value); }
    public string CpuStatusColor { get => _cpuStatusColor; private set => Set(ref _cpuStatusColor, value); }
    public string GpuStatusColor { get => _gpuStatusColor; private set => Set(ref _gpuStatusColor, value); }
    public string MemoryStatusColor { get => _memoryStatusColor; private set => Set(ref _memoryStatusColor, value); }
    public FrameApplicationItem? SelectedFrameApplication
    {
        get => _selectedFrameApplication;
        set
        {
            if (Set(ref _selectedFrameApplication, value))
                ApplySelectedFrameApplication();
        }
    }
    public StorageDeviceItem? SelectedStorageDevice
    {
        get => _selectedStorageDevice;
        set
        {
            if (Set(ref _selectedStorageDevice, value))
                ApplySelectedStorageDevice(addHistory: false);
        }
    }
    public string StorageTemperature { get => _storageTemperature; private set => Set(ref _storageTemperature, value); }
    public string StorageDetails { get => _storageDetails; private set => Set(ref _storageDetails, value); }
    public string StorageStatus { get => _storageStatus; private set => Set(ref _storageStatus, value); }
    public string StorageStatusColor { get => _storageStatusColor; private set => Set(ref _storageStatusColor, value); }
    public string StorageLoad { get => _storageLoad; private set => Set(ref _storageLoad, value); }
    public string StorageReadRate { get => _storageReadRate; private set => Set(ref _storageReadRate, value); }
    public string StorageWriteRate { get => _storageWriteRate; private set => Set(ref _storageWriteRate, value); }
    public string FrameTime { get => _frameTime; private set => Set(ref _frameTime, value); }
    public string FramesPerSecond { get => _framesPerSecond; private set => Set(ref _framesPerSecond, value); }
    public string FrameProcess { get => _frameProcess; private set => Set(ref _frameProcess, value); }
    public string SystemHealthHeadline { get => _systemHealthHeadline; private set => Set(ref _systemHealthHeadline, value); }
    public string SystemHealthMessage { get => _systemHealthMessage; private set => Set(ref _systemHealthMessage, value); }
    public string SystemHealthColor { get => _systemHealthColor; private set => Set(ref _systemHealthColor, value); }
    public string SystemHealthBackground { get => _systemHealthBackground; private set => Set(ref _systemHealthBackground, value); }
    public string SystemHealthBorder { get => _systemHealthBorder; private set => Set(ref _systemHealthBorder, value); }
    public string MotherboardTemperature { get => _motherboardTemperature; private set => Set(ref _motherboardTemperature, value); }
    public string MotherboardSource { get => _motherboardSource; private set => Set(ref _motherboardSource, value); }
    public string MotherboardStatus { get => _motherboardStatus; private set => Set(ref _motherboardStatus, value); }
    public string MotherboardStatusColor { get => _motherboardStatusColor; private set => Set(ref _motherboardStatusColor, value); }
    public bool HasElevatedAccess { get => _hasElevatedAccess; private set => Set(ref _hasElevatedAccess, value); }
    public bool IsPawnIoReady { get => _isPawnIoReady; private set => Set(ref _isPawnIoReady, value); }
    public string DriverStatus { get => _driverStatus; private set => Set(ref _driverStatus, value); }
    public string CpuSensorSummary { get => _cpuSensorSummary; private set => Set(ref _cpuSensorSummary, value); }
    public string AccessLabel => IsPawnIoReady ? "PawnIO sensor driver ready" : "PawnIO sensor driver required";
    public string DriverActionLabel => IsPawnIoReady ? "Reinstall PawnIO" : "Install PawnIO";
    public string RefreshLabel => $"Every {RefreshSeconds} seconds";
    public string HistoryStatus { get => _historyStatus; private set => Set(ref _historyStatus, value); }
    public string ProcessSummary { get => _processSummary; private set => Set(ref _processSummary, value); }
    public string NetworkSummary { get => _networkSummary; private set => Set(ref _networkSummary, value); }
    public string StorageSummary { get => _storageSummary; private set => Set(ref _storageSummary, value); }

    public bool AlertsEnabled
    {
        get => _settings.AlertsEnabled;
        set { if (_settings.AlertsEnabled == value) return; _settings.AlertsEnabled = value; SaveSetting(); OnPropertyChanged(); }
    }

    public bool MinimizeToTray
    {
        get => _settings.MinimizeToTray;
        set { if (_settings.MinimizeToTray == value) return; _settings.MinimizeToTray = value; SaveSetting(); OnPropertyChanged(); }
    }

    public bool StartMinimized
    {
        get => _settings.StartMinimized;
        set { if (_settings.StartMinimized == value) return; _settings.StartMinimized = value; SaveSetting(); OnPropertyChanged(); }
    }

    public int CpuAlertThreshold
    {
        get => _settings.CpuTemperatureAlert;
        set { value = Math.Clamp(value, 40, 110); if (_settings.CpuTemperatureAlert == value) return; _settings.CpuTemperatureAlert = value; SaveSetting(); OnPropertyChanged(); }
    }

    public int GpuAlertThreshold
    {
        get => _settings.GpuTemperatureAlert;
        set { value = Math.Clamp(value, 40, 110); if (_settings.GpuTemperatureAlert == value) return; _settings.GpuTemperatureAlert = value; SaveSetting(); OnPropertyChanged(); }
    }

    public int StorageAlertThreshold
    {
        get => _settings.StorageTemperatureAlert;
        set { value = Math.Clamp(value, 35, 100); if (_settings.StorageTemperatureAlert == value) return; _settings.StorageTemperatureAlert = value; SaveSetting(); OnPropertyChanged(); }
    }

    public int HistoryRetentionDays
    {
        get => _settings.HistoryRetentionDays;
        set { value = Math.Clamp(value, 1, 90); if (_settings.HistoryRetentionDays == value) return; _settings.HistoryRetentionDays = value; SaveSetting(); OnPropertyChanged(); }
    }

    public int RefreshSeconds
    {
        get => _refreshSeconds;
        set
        {
            value = Math.Clamp(value, 1, 30);
            if (!Set(ref _refreshSeconds, value))
                return;
            _timer.Interval = TimeSpan.FromSeconds(value);
            _settings.RefreshSeconds = value;
            SaveSetting();
            OnPropertyChanged(nameof(RefreshLabel));
        }
    }

    public async Task StartAsync()
    {
        LoadRecentHistory();
        var installation = await PawnIoInstaller.EnsureInstalledAsync();
        await RefreshAsync();
        if (!installation.Success || installation.RebootRequired)
            StatusMessage = installation.Message;
        _timer.Start();
    }

    private async Task RefreshAsync()
    {
        if (_isReading || _disposed)
            return;

        _isReading = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            var snapshotTask = Task.Run(_monitor.Read);
            var processTask = Task.Run(_processTelemetry.Read);
            var networkTask = Task.Run(_networkTelemetry.Read);
            await Task.WhenAll(snapshotTask, processTask, networkTask);
            Apply(snapshotTask.Result);
            UpdateProcesses(processTask.Result);
            UpdateNetworkAdapters(networkTask.Result);
        }
        finally
        {
            _isReading = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void Apply(SensorSnapshot snapshot)
    {
        CpuName = snapshot.CpuName;
        GpuName = snapshot.GpuName;
        MemoryName = snapshot.MemoryName;
        CpuTemperature = FormatTemperature(snapshot.CpuTemperature);
        CpuTemperatureSource = snapshot.CpuTemperatureSource;
        GpuTemperature = FormatTemperature(snapshot.GpuTemperature);
        CpuVoltage = FormatVoltage(snapshot.CpuVoltage);
        CpuPower = FormatPower(snapshot.CpuPowerWatts);
        CpuElectricalSource = snapshot.CpuElectricalSource;
        GpuVoltage = FormatVoltage(snapshot.GpuVoltage);
        GpuPower = FormatPower(snapshot.GpuPowerWatts);
        GpuElectricalSource = snapshot.GpuElectricalSource;
        MemoryTemperature = FormatTemperature(snapshot.MemoryTemperature, "Not exposed");
        CpuLoad = FormatPercent(snapshot.CpuLoad);
        GpuLoad = FormatPercent(snapshot.GpuLoad);
        MemoryLoad = FormatPercent(snapshot.MemoryLoad);
        UpdateStorageDevices(snapshot.StorageDevices, snapshot.StoragePerformance);
        UpdateFrameApplications(snapshot.FrameApplications, snapshot.FrameProcess);
        MotherboardTemperature = FormatTemperature(snapshot.MotherboardTemperature);
        MotherboardSource = snapshot.MotherboardTemperatureSource;
        FanSpeed = snapshot.CpuFanRpm.HasValue ? $"{snapshot.CpuFanRpm:0} RPM" : "Not exposed";
        LastUpdated = $"Updated {snapshot.Timestamp:HH:mm:ss}";
        HasElevatedAccess = snapshot.HasElevatedAccess;
        IsPawnIoReady = snapshot.IsPawnIoReady;
        DriverStatus = snapshot.DriverStatus;
        CpuSensorSummary = snapshot.CpuSensorCount > 0
            ? $"{snapshot.CpuSensorCount} CPU temperature sensor(s) returned data"
            : "No CPU temperature sensors returned data";
        OnPropertyChanged(nameof(AccessLabel));
        OnPropertyChanged(nameof(DriverActionLabel));

        (CpuStatus, CpuStatusColor) = GetTemperatureStatus(snapshot.CpuTemperature, 75, 90);
        (GpuStatus, GpuStatusColor) = GetTemperatureStatus(snapshot.GpuTemperature, 76, 88);
        (MemoryStatus, MemoryStatusColor) = GetTemperatureStatus(snapshot.MemoryTemperature, 60, 75);
        (MotherboardStatus, MotherboardStatusColor) = GetTemperatureStatus(snapshot.MotherboardTemperature, 65, 80);
        OnPropertyChanged(nameof(CpuStatus));
        OnPropertyChanged(nameof(CpuStatusColor));
        OnPropertyChanged(nameof(GpuStatus));
        OnPropertyChanged(nameof(GpuStatusColor));
        OnPropertyChanged(nameof(MemoryStatus));
        OnPropertyChanged(nameof(MemoryStatusColor));
        OnPropertyChanged(nameof(MotherboardStatus));
        OnPropertyChanged(nameof(MotherboardStatusColor));

        AddHistory(CpuTemperatureHistory, snapshot.CpuTemperature);
        AddHistory(GpuTemperatureHistory, snapshot.GpuTemperature);
        AddHistory(MemoryLoadHistory, snapshot.MemoryLoad);
        AddHistory(CpuLoadHistory, snapshot.CpuLoad);
        AddHistory(GpuLoadHistory, snapshot.GpuLoad);
        UpdateStorageHistories(snapshot.StorageDevices);
        UpdateStorageLoadHistories(snapshot.StoragePerformance);
        ApplySelectedStorageDevice(addHistory: false);
        AddHistory(MotherboardTemperatureHistory, snapshot.MotherboardTemperature);
        EvaluateSystemHealth(snapshot);

        var historySample = new HistorySample(
            snapshot.Timestamp, snapshot.CpuTemperature, snapshot.CpuLoad, snapshot.GpuTemperature,
            snapshot.GpuLoad, snapshot.MemoryLoad, SelectedStorageDevice?.Temperature, SelectedStorageDevice?.Load);
        if (_history.TryAppend(historySample, HistoryRetentionDays))
        {
            RecentHistory.Add(new HistoryItem(historySample));
            while (RecentHistory.Count > 120) RecentHistory.RemoveAt(0);
            HistoryStatus = $"Saved {RecentHistory.Count} recent samples · retaining {HistoryRetentionDays} day(s)";
        }

        foreach (var alert in _alerts.Evaluate(snapshot, _settings))
        {
            RecentAlerts.Insert(0, new AlertItem(alert));
            while (RecentAlerts.Count > 25) RecentAlerts.RemoveAt(RecentAlerts.Count - 1);
            AlertRaised?.Invoke(this, alert);
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Error))
            StatusMessage = $"Sensor read error: {snapshot.Error}";
        else if (!snapshot.IsPawnIoReady)
            StatusMessage = snapshot.HasElevatedAccess
                ? "Install the bundled PawnIO sensor driver, then restart SystemPulse."
                : "Install PawnIO, then run SystemPulse as administrator for hardware sensor access.";
        else if (snapshot.CpuTemperature is null)
            StatusMessage = "PawnIO is ready, but this CPU did not return a valid temperature value.";
        else if (snapshot.StorageDevices.Count == 0)
            StatusMessage = "Core monitoring is active, but Windows did not expose any physical storage devices.";
        else
            StatusMessage = "All available sensors are reporting normally.";
    }

    private void UpdateFrameApplications(
        IReadOnlyList<FrameApplicationSnapshot> applications,
        string defaultProcess)
    {
        var selectedProcessId = SelectedFrameApplication?.ProcessId;
        foreach (var application in applications)
        {
            if (!_frameHistoryByProcess.TryGetValue(application.ProcessId, out var history))
            {
                history = new Queue<double>();
                _frameHistoryByProcess[application.ProcessId] = history;
            }

            if (application.FrameTimeMilliseconds.HasValue)
                history.Enqueue(application.FrameTimeMilliseconds.Value);
            while (history.Count > HistoryLimit)
                history.Dequeue();
        }

        FrameApplications.Clear();
        foreach (var application in applications)
            FrameApplications.Add(new FrameApplicationItem(application));

        SelectedFrameApplication = FrameApplications.FirstOrDefault(item => item.ProcessId == selectedProcessId)
            ?? FrameApplications.FirstOrDefault(item => item.DisplayName.Equals(defaultProcess, StringComparison.OrdinalIgnoreCase))
            ?? FrameApplications.FirstOrDefault();

        if (SelectedFrameApplication is null)
            ApplySelectedFrameApplication();
    }

    private void ApplySelectedFrameApplication()
    {
        var application = SelectedFrameApplication;
        FrameTimeHistory.Clear();
        if (application is null)
        {
            FrameTime = "Unavailable";
            FramesPerSecond = "—";
            FrameProcess = "No active 3D presentation";
            return;
        }

        FrameTime = application.FrameTimeMilliseconds.HasValue
            ? $"{application.FrameTimeMilliseconds:0.0} ms"
            : "Unavailable";
        FramesPerSecond = application.FrameTimeMilliseconds is > 0
            ? $"{1000f / application.FrameTimeMilliseconds.Value:0} FPS"
            : "—";
        FrameProcess = application.DisplayName;
        if (_frameHistoryByProcess.TryGetValue(application.ProcessId, out var history))
        {
            foreach (var value in history)
                FrameTimeHistory.Add(value);
        }
    }

    private void EvaluateSystemHealth(SensorSnapshot snapshot)
    {
        var issues = new List<string>();

        if (snapshot.CpuLoad is >= 90 || snapshot.CpuTemperature is >= 90)
            issues.Add($"CPU heavy load: {FormatPercent(snapshot.CpuLoad)} · {FormatHealthTemperature(snapshot.CpuTemperature)}");

        if (snapshot.GpuLoad is >= 95 || snapshot.GpuTemperature is >= 88)
            issues.Add($"GPU heavy load: {FormatPercent(snapshot.GpuLoad)} · {FormatHealthTemperature(snapshot.GpuTemperature)}");

        var deviceNames = snapshot.StorageDevices.ToDictionary(device => device.DeviceId, device => device.DisplayName, StringComparer.OrdinalIgnoreCase);
        foreach (var device in snapshot.StoragePerformance.Where(device => device.Load is >= 95))
        {
            var name = deviceNames.TryGetValue(device.DeviceId, out var displayName)
                ? displayName
                : $"Physical disk {device.DeviceId}";
            issues.Add($"{name} is under heavy load ({device.Load:0}%)");
        }

        if (issues.Count == 0)
        {
            SystemHealthHeadline = "LIVE MONITORING";
            SystemHealthMessage = "System is nominal";
            SystemHealthColor = "#58D6C7";
            SystemHealthBackground = "#0B58D6C7";
            SystemHealthBorder = "#3058D6C7";
            return;
        }

        SystemHealthHeadline = "SYSTEM ALERT";
        SystemHealthMessage = string.Join(Environment.NewLine, issues);
        SystemHealthColor = "#FF6B7A";
        SystemHealthBackground = "#18FF6B7A";
        SystemHealthBorder = "#50FF6B7A";
    }

    private void UpdateStorageDevices(
        IReadOnlyList<StorageDeviceSnapshot> devices,
        IReadOnlyList<StoragePerformanceSnapshot> performance)
    {
        var selectedId = SelectedStorageDevice?.DeviceId;
        var performanceById = performance.ToDictionary(item => item.DeviceId, StringComparer.OrdinalIgnoreCase);
        StorageDevices.Clear();

        foreach (var device in devices)
        {
            performanceById.TryGetValue(device.DeviceId, out var devicePerformance);
            StorageDevices.Add(new StorageDeviceItem(device, devicePerformance));
        }

        var warnings = devices.Count(device => device.Health is "Warning" or "Unhealthy");
        var directSmart = devices.Count(device => device.HealthDataSource.StartsWith("Direct", StringComparison.OrdinalIgnoreCase));
        StorageSummary = devices.Count == 0
            ? "Windows did not expose any physical drives"
            : warnings == 0
                ? $"{devices.Count} physical drive(s) · {directSmart} direct SMART · no health warnings"
                : $"{devices.Count} physical drive(s) · {directSmart} direct SMART · {warnings} health warning(s)";

        SelectedStorageDevice = StorageDevices.FirstOrDefault(device => device.DeviceId == selectedId)
            ?? StorageDevices.FirstOrDefault();
    }

    private void UpdateProcesses(IReadOnlyList<ProcessTelemetrySnapshot> snapshots)
    {
        Processes.Clear();
        foreach (var snapshot in snapshots)
            Processes.Add(new ProcessTelemetryItem(snapshot));

        var busiest = snapshots.FirstOrDefault();
        ProcessSummary = busiest is null
            ? "No process telemetry is available"
            : $"{snapshots.Count} active entries · highest CPU: {busiest.Name} ({busiest.CpuPercent:0.0}%)";
    }

    private void UpdateNetworkAdapters(IReadOnlyList<NetworkAdapterSnapshot> snapshots)
    {
        NetworkAdapters.Clear();
        foreach (var snapshot in snapshots)
            NetworkAdapters.Add(new NetworkAdapterItem(snapshot));

        var active = snapshots.Where(item => item.Status == "Up").ToList();
        var down = active.Aggregate(0UL, (total, item) => total + item.ReceivedBytesPerSecond);
        var up = active.Aggregate(0UL, (total, item) => total + item.SentBytesPerSecond);
        NetworkSummary = $"{active.Count} active adapter(s) · down {FormatRate(down)} · up {FormatRate(up)}";
    }

    private void LoadRecentHistory()
    {
        try
        {
            RecentHistory.Clear();
            foreach (var sample in _history.ReadRecent())
                RecentHistory.Add(new HistoryItem(sample));
            HistoryStatus = RecentHistory.Count == 0
                ? "History is enabled; a sample is saved every 10 seconds"
                : $"Loaded {RecentHistory.Count} recent samples · retaining {HistoryRetentionDays} day(s)";
        }
        catch (Exception exception)
        {
            HistoryStatus = $"History could not be loaded: {exception.Message}";
        }
    }

    private void ExportHistory()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export SystemPulse history",
            Filter = "CSV file (*.csv)|*.csv",
            FileName = $"SystemPulse-history-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            AddExtension = true,
            DefaultExt = ".csv"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            _history.Export(dialog.FileName);
            HistoryStatus = $"History exported to {dialog.FileName}";
        }
        catch (Exception exception)
        {
            HistoryStatus = $"Export failed: {exception.Message}";
        }
    }

    private void OpenHistoryFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_history.Folder) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            HistoryStatus = $"Could not open history folder: {exception.Message}";
        }
    }

    private void SaveSetting() => _settingsService.Save(_settings);

    private void ApplySelectedStorageDevice(bool addHistory)
    {
        var device = SelectedStorageDevice;
        if (device is null)
        {
            StorageTemperature = "Unavailable";
            StorageDetails = "No physical storage detected";
            StorageStatus = "Unavailable";
            StorageStatusColor = "#959DAF";
            StorageLoad = "Unavailable";
            StorageReadRate = "Unavailable";
            StorageWriteRate = "Unavailable";
            StorageLoadHistory.Clear();
            return;
        }

        StorageTemperature = FormatTemperature(device.Temperature);
        StorageDetails = $"{device.MediaType} · {device.BusType} · {FormatSize(device.SizeBytes)} · {device.Health}";
        (StorageStatus, StorageStatusColor) = device.Health switch
        {
            "Unhealthy" => ("Unhealthy", "#FF6B7A"),
            "Warning" => ("Warning", "#FFB454"),
            _ => GetTemperatureStatus(device.Temperature, 55, 70)
        };
        OnPropertyChanged(nameof(StorageStatus));
        OnPropertyChanged(nameof(StorageStatusColor));
        StorageLoad = FormatPercent(device.Load);
        StorageReadRate = FormatRate(device.ReadBytesPerSecond);
        StorageWriteRate = FormatRate(device.WriteBytesPerSecond);

        StorageTemperatureHistory.Clear();
        if (_storageHistoryByDevice.TryGetValue(device.DeviceId, out var history))
        {
            foreach (var value in history)
                StorageTemperatureHistory.Add(value);
        }

        StorageLoadHistory.Clear();
        if (_storageLoadHistoryByDevice.TryGetValue(device.DeviceId, out var loadHistory))
        {
            foreach (var value in loadHistory)
                StorageLoadHistory.Add(value);
        }
    }

    private void UpdateStorageLoadHistories(IReadOnlyList<StoragePerformanceSnapshot> performance)
    {
        foreach (var device in performance)
        {
            if (!_storageLoadHistoryByDevice.TryGetValue(device.DeviceId, out var history))
            {
                history = new Queue<double>();
                _storageLoadHistoryByDevice[device.DeviceId] = history;
            }

            if (device.Load.HasValue)
                history.Enqueue(Math.Clamp(device.Load.Value, 0, 100));
            else if (history.Count > 0)
                history.Enqueue(history.Last());

            while (history.Count > HistoryLimit)
                history.Dequeue();
        }
    }

    private void UpdateStorageHistories(IReadOnlyList<StorageDeviceSnapshot> devices)
    {
        foreach (var device in devices)
        {
            if (!_storageHistoryByDevice.TryGetValue(device.DeviceId, out var history))
            {
                history = new Queue<double>();
                _storageHistoryByDevice[device.DeviceId] = history;
            }

            if (device.Temperature.HasValue)
                history.Enqueue(device.Temperature.Value);
            else if (history.Count > 0)
                history.Enqueue(history.Last());

            while (history.Count > HistoryLimit)
                history.Dequeue();
        }
    }

    private static string FormatSize(ulong? bytes)
    {
        if (!bytes.HasValue)
            return "Size unavailable";

        var tebibytes = bytes.Value / Math.Pow(1024, 4);
        return tebibytes >= 1
            ? $"{tebibytes:0.##} TB"
            : $"{bytes.Value / Math.Pow(1024, 3):0} GB";
    }

    private static string FormatRate(ulong? bytesPerSecond)
    {
        if (!bytesPerSecond.HasValue)
            return "Unavailable";

        var value = bytesPerSecond.Value;
        if (value >= 1024UL * 1024 * 1024)
            return $"{value / Math.Pow(1024, 3):0.0} GB/s";
        if (value >= 1024UL * 1024)
            return $"{value / Math.Pow(1024, 2):0.0} MB/s";
        if (value >= 1024)
            return $"{value / 1024d:0.0} KB/s";
        return $"{value} B/s";
    }

    private static (string Label, string Color) GetTemperatureStatus(float? value, float warm, float hot)
    {
        if (!value.HasValue)
            return ("Unavailable", "#959DAF");
        if (value >= hot)
            return ("Hot", "#FF6B7A");
        if (value >= warm)
            return ("Warm", "#FFB454");
        return ("Normal", "#58D6C7");
    }

    private static string FormatTemperature(float? value, string unavailable = "Unavailable") =>
        value.HasValue ? $"{value:0}°" : unavailable;

    private static string FormatPercent(float? value) =>
        value.HasValue ? $"{Math.Clamp(value.Value, 0, 100):0}%" : "Unavailable";

    private static string FormatVoltage(float? value) =>
        value.HasValue ? $"{value.Value:0.000} V" : "Unavailable";

    private static string FormatPower(float? value) =>
        value.HasValue ? $"{value.Value:0.0} W" : "Unavailable";

    private static string FormatHealthTemperature(float? value) =>
        value.HasValue ? $"{value:0}°C" : "temperature unavailable";

    private static void AddHistory(ObservableCollection<double> history, float? value)
    {
        if (value.HasValue)
            history.Add(value.Value);
        else if (history.Count > 0)
            history.Add(history[^1]);

        while (history.Count > HistoryLimit)
            history.RemoveAt(0);
    }

    private async Task InstallPawnIoAsync(bool force)
    {
        StatusMessage = "Installing the signed PawnIO sensor driver…";
        var result = force
            ? await PawnIoInstaller.InstallAsync()
            : await PawnIoInstaller.EnsureInstalledAsync();
        StatusMessage = result.Message;

        if (result.Success && !result.RebootRequired)
            await RefreshAsync();
    }

    private void RestartElevated()
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
                return;
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas" });
            System.Windows.Application.Current.Shutdown();
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            StatusMessage = "Administrator access was canceled.";
        }
        catch (Exception exception)
        {
            StatusMessage = $"Could not restart: {exception.Message}";
        }
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        if (_disposed)
            return;
        _timer.Stop();
        _monitor.Dispose();
        _disposed = true;
    }

    public sealed class StorageDeviceItem
    {
        public StorageDeviceItem(StorageDeviceSnapshot device, StoragePerformanceSnapshot? performance)
        {
            DeviceId = device.DeviceId;
            DisplayName = device.DisplayName;
            SizeBytes = device.SizeBytes;
            MediaType = device.MediaType;
            BusType = device.BusType;
            Temperature = device.Temperature;
            Health = device.Health;
            Wear = device.Wear;
            TemperatureMaximum = device.TemperatureMaximum;
            PowerOnHours = device.PowerOnHours;
            ReadErrorsTotal = device.ReadErrorsTotal;
            ReadErrorsUncorrected = device.ReadErrorsUncorrected;
            WriteErrorsTotal = device.WriteErrorsTotal;
            WriteErrorsUncorrected = device.WriteErrorsUncorrected;
            SerialNumber = device.SerialNumber;
            FirmwareVersion = device.FirmwareVersion;
            OperationalStatus = device.OperationalStatus;
            PhysicalLocation = device.PhysicalLocation;
            UnsafeShutdowns = device.UnsafeShutdowns;
            HealthDataSource = device.HealthDataSource;
            NvmeMediaErrors = device.NvmeMediaErrors;
            NvmeErrorLogEntries = device.NvmeErrorLogEntries;
            ReallocatedSectors = device.ReallocatedSectors;
            ReallocationEvents = device.ReallocationEvents;
            PendingSectors = device.PendingSectors;
            OfflineUncorrectable = device.OfflineUncorrectable;
            ReportedUncorrectable = device.ReportedUncorrectable;
            CrcErrors = device.CrcErrors;
            Load = performance?.Load;
            ReadBytesPerSecond = performance?.ReadBytesPerSecond;
            WriteBytesPerSecond = performance?.WriteBytesPerSecond;
        }

        public string DeviceId { get; }
        public string DisplayName { get; }
        public ulong? SizeBytes { get; }
        public string MediaType { get; }
        public string BusType { get; }
        public float? Temperature { get; }
        public string Health { get; }
        public byte? Wear { get; }
        public float? TemperatureMaximum { get; }
        public ulong? PowerOnHours { get; }
        public ulong? ReadErrorsTotal { get; }
        public ulong? ReadErrorsUncorrected { get; }
        public ulong? WriteErrorsTotal { get; }
        public ulong? WriteErrorsUncorrected { get; }
        public string SerialNumber { get; }
        public string FirmwareVersion { get; }
        public string OperationalStatus { get; }
        public string PhysicalLocation { get; }
        public ulong? UnsafeShutdowns { get; }
        public string HealthDataSource { get; }
        public ulong? NvmeMediaErrors { get; }
        public ulong? NvmeErrorLogEntries { get; }
        public ulong? ReallocatedSectors { get; }
        public ulong? ReallocationEvents { get; }
        public ulong? PendingSectors { get; }
        public ulong? OfflineUncorrectable { get; }
        public ulong? ReportedUncorrectable { get; }
        public ulong? CrcErrors { get; }
        public float? Load { get; }
        public ulong? ReadBytesPerSecond { get; }
        public ulong? WriteBytesPerSecond { get; }
        private bool SupportsWearIndicator => MediaType is "SSD" or "Storage-class memory" || BusType == "NVMe";
        public string RemainingLife => !SupportsWearIndicator
            ? "Not applicable"
            : PowerOnHours is > 50_000 && Wear == 0
                ? "Unverified"
                : Wear.HasValue ? $"{Math.Max(0, 100 - Wear.Value)}% reported" : "Not reported";
        public string WearText => !SupportsWearIndicator
            ? "HDD health uses sector errors"
            : Wear.HasValue
                ? PowerOnHours is > 50_000 && Wear == 0
                    ? "Drive reports 100% · conflicts with high hours"
                    : $"{Wear.Value}% used · usage-based"
                : "Drive did not expose wear";
        public string PowerOnHoursText => PowerOnHours.HasValue ? $"{PowerOnHours:N0} hours" : "Not reported";
        public string MaximumTemperatureText => TemperatureMaximum.HasValue ? $"{TemperatureMaximum:0} °C" : "Not reported";
        private bool HasNvmeReliability => NvmeMediaErrors.HasValue || NvmeErrorLogEntries.HasValue;
        private bool HasAtaReliability => ReallocatedSectors.HasValue || ReallocationEvents.HasValue || PendingSectors.HasValue ||
                                          OfflineUncorrectable.HasValue || ReportedUncorrectable.HasValue || CrcErrors.HasValue;
        private ulong AtaUncorrectable => (OfflineUncorrectable ?? 0) + (ReportedUncorrectable ?? 0);
        public string ReliabilityErrorHeadline => HasNvmeReliability
            ? $"Media errors {(NvmeMediaErrors ?? 0):N0} · error-log entries {(NvmeErrorLogEntries ?? 0):N0}"
            : HasAtaReliability
                ? $"Reallocated {(ReallocatedSectors ?? 0):N0} · pending {(PendingSectors ?? 0):N0} · uncorrectable {AtaUncorrectable:N0}"
                : ReadErrorsTotal.HasValue || WriteErrorsTotal.HasValue || ReadErrorsUncorrected.HasValue || WriteErrorsUncorrected.HasValue
                    ? $"Provider read {(ReadErrorsTotal ?? 0):N0} · write {(WriteErrorsTotal ?? 0):N0} · uncorrected {(ReadErrorsUncorrected ?? 0) + (WriteErrorsUncorrected ?? 0):N0}"
                    : "Protocol-native counters not exposed";
        public string ReliabilityErrorDetails => HasNvmeReliability
            ? $"NVMe lifetime log; error-log entries can be non-fatal · {UnsafeShutdownsText}"
            : HasAtaReliability
                ? $"Reallocation events {(ReallocationEvents ?? 0):N0} · interface CRC {(CrcErrors ?? 0):N0} · {UnsafeShutdownsText}"
                : "The drive/controller did not provide NVMe or ATA SMART error data.";
        public string ReliabilityColor => (NvmeMediaErrors ?? 0) > 0 || (ReallocatedSectors ?? 0) > 0 ||
                                          (PendingSectors ?? 0) > 0 || AtaUncorrectable > 0
            ? "#FF6B7A"
            : (NvmeErrorLogEntries ?? 0) > 0 || (ReallocationEvents ?? 0) > 0 || (CrcErrors ?? 0) > 0
                ? "#FFB454"
                : HasNvmeReliability || HasAtaReliability ? "#58D6C7" : "#959DAF";
        public string ErrorSummary => ReliabilityErrorHeadline;
        public string UnsafeShutdownsText => UnsafeShutdowns.HasValue ? $"{UnsafeShutdowns:N0} unsafe shutdown(s)" : "Unsafe shutdowns not reported";
        public string HealthColor => Health switch { "Unhealthy" => "#FF6B7A", "Warning" => "#FFB454", "Healthy" => "#58D6C7", _ => "#959DAF" };
        public string CapacityText => FormatSize(SizeBytes);
        public string InterfaceText => $"{MediaType} · {BusType}";
        public string TemperatureText => FormatTemperature(Temperature);
        public string ActivityText => FormatPercent(Load);
        public string ReadRateText => FormatRate(ReadBytesPerSecond);
        public string WriteRateText => FormatRate(WriteBytesPerSecond);
        public string ReliabilitySummary => Health == "Healthy"
            ? "Windows reports this drive is healthy."
            : Health == "Warning"
                ? "Windows reports a reliability warning. Back up important data and review the error counters."
                : Health == "Unhealthy"
                    ? "Windows reports this drive is unhealthy. Back up important data immediately."
                    : "The storage provider did not expose a definitive health state.";
    }

    public sealed class FrameApplicationItem
    {
        public FrameApplicationItem(FrameApplicationSnapshot application)
        {
            ProcessId = application.ProcessId;
            DisplayName = application.DisplayName;
            FrameTimeMilliseconds = application.FrameTimeMilliseconds;
        }

        public int ProcessId { get; }
        public string DisplayName { get; }
        public float? FrameTimeMilliseconds { get; }
    }

    public sealed class ProcessTelemetryItem
    {
        public ProcessTelemetryItem(ProcessTelemetrySnapshot process)
        {
            ProcessId = process.ProcessId;
            DisplayName = process.Name;
            Cpu = $"{process.CpuPercent:0.0}%";
            Memory = FormatBytes(process.WorkingSetBytes);
            DiskRead = FormatRate(process.ReadBytesPerSecond);
            DiskWrite = FormatRate(process.WriteBytesPerSecond);
        }

        public int ProcessId { get; }
        public string DisplayName { get; }
        public string Cpu { get; }
        public string Memory { get; }
        public string DiskRead { get; }
        public string DiskWrite { get; }
    }

    public sealed class NetworkAdapterItem
    {
        public NetworkAdapterItem(NetworkAdapterSnapshot adapter)
        {
            DisplayName = adapter.Name;
            Description = adapter.Description;
            Status = adapter.Status;
            StatusColor = adapter.Status == "Up" ? "#58D6C7" : "#959DAF";
            Addresses = adapter.Addresses;
            LinkSpeed = adapter.LinkSpeedBitsPerSecond > 0 ? $"{adapter.LinkSpeedBitsPerSecond / 1_000_000d:0.#} Mbps" : "Unknown";
            Download = FormatRate(adapter.ReceivedBytesPerSecond);
            Upload = FormatRate(adapter.SentBytesPerSecond);
            TotalReceived = FormatBytes(adapter.TotalReceivedBytes);
            TotalSent = FormatBytes(adapter.TotalSentBytes);
        }

        public string DisplayName { get; }
        public string Description { get; }
        public string Status { get; }
        public string StatusColor { get; }
        public string Addresses { get; }
        public string LinkSpeed { get; }
        public string Download { get; }
        public string Upload { get; }
        public string TotalReceived { get; }
        public string TotalSent { get; }
    }

    public sealed class HistoryItem
    {
        public HistoryItem(HistorySample sample)
        {
            Timestamp = sample.Timestamp.ToString("MMM d, HH:mm:ss");
            Cpu = $"{FormatTemperature(sample.CpuTemperature)} · {FormatPercent(sample.CpuLoad)}";
            Gpu = $"{FormatTemperature(sample.GpuTemperature)} · {FormatPercent(sample.GpuLoad)}";
            Memory = FormatPercent(sample.MemoryLoad);
            Storage = $"{FormatTemperature(sample.StorageTemperature)} · {FormatPercent(sample.StorageLoad)}";
        }
        public string Timestamp { get; }
        public string Cpu { get; }
        public string Gpu { get; }
        public string Memory { get; }
        public string Storage { get; }
    }

    public sealed class AlertItem
    {
        public AlertItem(MonitorAlert alert)
        {
            Time = alert.Timestamp.ToString("HH:mm:ss");
            Title = alert.Title;
            Message = alert.Message;
        }
        public string Time { get; }
        public string Title { get; }
        public string Message { get; }
    }

    private static string FormatBytes(ulong bytes)
    {
        if (bytes >= 1024UL * 1024 * 1024) return $"{bytes / Math.Pow(1024, 3):0.0} GB";
        if (bytes >= 1024UL * 1024) return $"{bytes / Math.Pow(1024, 2):0.0} MB";
        if (bytes >= 1024) return $"{bytes / 1024d:0.0} KB";
        return $"{bytes} B";
    }

    private sealed class RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
        public void Execute(object? parameter) => execute(parameter);
    }
}
