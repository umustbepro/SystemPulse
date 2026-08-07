using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Threading;
using SystemPulse.Services;

namespace SystemPulse.ViewModels;

public sealed class FanControlViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly FanControlService _service = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly string _profileFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SystemPulse", "FanProfiles");
    private readonly string _calibrationPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SystemPulse", "FanCalibration.json");
    private bool _enabled;
    private bool _busy;
    private string _status = "Open Fan Control to discover writable channels.";
    private string _profileName = "Balanced";
    private string _selectedCategory = "All";
    private bool _isCalibrating;
    private bool _calibrationCompleted;
    private string _calibrationProgressText = string.Empty;

    public FanControlViewModel()
    {
        RefreshCommand = new AsyncCommand(_ => DiscoverAsync());
        ReleaseAllCommand = new RelayCommand(_ => ReleaseAll());
        SaveProfileCommand = new RelayCommand(_ => SaveProfile());
        LoadProfileCommand = new RelayCommand(_ => LoadProfile());
        CalibrateCommand = new AsyncCommand(CalibrateAsync);
        _timer.Tick += (_, _) => Tick();
        try
        {
            Directory.CreateDirectory(_profileFolder);
            ReloadProfileNames();
        }
        catch (Exception exception)
        {
            _status = $"Fan profiles are unavailable: {exception.Message}";
        }
    }

    public ObservableCollection<FanChannelViewModel> Channels { get; } = [];
    public ObservableCollection<FanChannelViewModel> FilteredChannels { get; } = [];
    public ObservableCollection<FanSensorViewModel> Sensors { get; } = [];
    public ObservableCollection<FanSensorViewModel> FilteredSensors { get; } = [];
    public ObservableCollection<string> Profiles { get; } = [];
    public IReadOnlyList<string> CurveTypes { get; } = ["Manual", "Flat", "Linear", "Graph", "Trigger", "Mix", "Sync", "Auto"];
    public ICommand RefreshCommand { get; }
    public ICommand ReleaseAllCommand { get; }
    public ICommand SaveProfileCommand { get; }
    public ICommand LoadProfileCommand { get; }
    public ICommand CalibrateCommand { get; }
    public bool IsEnabled { get => _enabled; set { if (Set(ref _enabled, value)) { if (value) _timer.Start(); else ReleaseAll(); } } }
    public bool IsBusy { get => _busy; private set => Set(ref _busy, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string ProfileName { get => _profileName; set => Set(ref _profileName, value); }
    public string SelectedCategory { get => _selectedCategory; private set => Set(ref _selectedCategory, value); }
    public bool IsCalibrating { get => _isCalibrating; private set => Set(ref _isCalibrating, value); }
    public bool CalibrationCompleted { get => _calibrationCompleted; private set => Set(ref _calibrationCompleted, value); }
    public string CalibrationProgressText { get => _calibrationProgressText; private set => Set(ref _calibrationProgressText, value); }
    public string FilterEmptyMessage => FilteredChannels.Count == 0 && Channels.Count > 0
        ? $"No {SelectedCategory.ToLowerInvariant()} fans were detected."
        : string.Empty;

    public void SelectCategory(string? category)
    {
        var normalized = category is "Case" or "CPU" or "GPU" ? category : "All";
        if (SelectedCategory != normalized) SelectedCategory = normalized;
        ApplyCategoryFilter();
    }

    public async Task DiscoverAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var result = await Task.Run(_service.Discover);
            Channels.Clear(); FilteredChannels.Clear(); Sensors.Clear(); FilteredSensors.Clear();
            foreach (var sensor in result.Temperatures) Sensors.Add(new FanSensorViewModel(sensor));
            var savedCalibrations = LoadCalibrationRecords();
            foreach (var device in result.Controls)
            {
                var channel = new FanChannelViewModel(device, Sensors, CurveTypes);
                if (savedCalibrations.TryGetValue(device.Id, out var calibration)) channel.ApplyCalibration(calibration);
                Channels.Add(channel);
            }
            ApplyCategoryFilter();
            var writable = Channels.Count(channel => channel.CanControl);
            var relatedTemperatureCount = Sensors.Count(sensor => Channels.Any(channel => channel.IsRelatedSensor(sensor)));
            Status = Channels.Count == 0
                ? "No fan RPM or control sensors were exposed by the motherboard, GPU, or controller."
                : $"Detected {Channels.Count} fan channel(s), {writable} writable, and {relatedTemperatureCount} related temperature source(s).";
            if (Channels.Count > 0) _timer.Start();
        }
        catch (Exception ex)
        {
            var dependency = ex is FileNotFoundException missing && !string.IsNullOrWhiteSpace(missing.FileName)
                ? $" Missing dependency: {missing.FileName}."
                : string.Empty;
            Status = $"Fan discovery failed safely: {ex.GetType().Name}: {(string.IsNullOrWhiteSpace(ex.Message) ? "hardware provider returned no details" : ex.Message)}{dependency}";
            WriteDiscoveryError(ex);
        }
        finally { IsBusy = false; }
    }

    private void ApplyCategoryFilter()
    {
        FilteredChannels.Clear();
        foreach (var channel in Channels.Where(channel => SelectedCategory == "All" || channel.Category == SelectedCategory))
            FilteredChannels.Add(channel);
        FilteredSensors.Clear();
        foreach (var sensor in Sensors.Where(sensor => FilteredChannels.Any(channel => channel.IsRelatedSensor(sensor))))
            FilteredSensors.Add(sensor);
        On(nameof(FilterEmptyMessage));
    }

    private void Tick()
    {
        try
        {
            _service.Refresh();
            foreach (var sensor in Sensors) sensor.Refresh();
            foreach (var channel in Channels) channel.Refresh();
            if (!IsEnabled) return;
            var syncOutput = Channels.FirstOrDefault(c => c.IsControlled)?.LastOutput;
            foreach (var channel in Channels)
            {
                if (!channel.IsControlled)
                {
                    _service.Release(channel.Device);
                    continue;
                }
                var requested = channel.CalculateOutput(syncOutput);
                if (requested.HasValue) _service.SetSoftware(channel.Device, requested.Value);
            }
        }
        catch (Exception ex) { Status = $"Fan update paused safely: {ex.Message}"; IsEnabled = false; }
    }

    private async Task CalibrateAsync(object? parameter)
    {
        if (parameter is not FanChannelViewModel channel || IsBusy) return;
        CalibrationCompleted = false;
        CalibrationProgressText = "0%";
        IsCalibrating = true;
        IsBusy = true;
        IsEnabled = false;
        var completed = false;
        try
        {
            Status = $"Calibrating {channel.Name}; do not sleep or shut down.";
            float? start = null; float maxRpm = 0;
            for (var percent = 20; percent <= 100; percent += 10)
            {
                CalibrationProgressText = $"{percent}%";
                _service.SetSoftware(channel.Device, percent);
                await Task.Delay(1200);
                _service.Refresh(); channel.Refresh();
                if (channel.RpmValue > 100 && start is null) start = percent;
                maxRpm = Math.Max(maxRpm, channel.RpmValue ?? 0);
            }
            channel.StartPercent = start ?? Math.Max(30, channel.Device.Minimum);
            channel.MinimumPercent = channel.StartPercent;
            channel.CalibratedMaximumRpm = maxRpm;
            if (maxRpm > 0 && channel.TargetRpm <= 0) channel.TargetRpm = maxRpm * 0.6;
            channel.CalibrationText = maxRpm > 0 ? $"Calibrated · starts at {channel.StartPercent:0}% · max {maxRpm:0} RPM" : "RPM unavailable · control remains available, calibration needs RPM feedback";
            channel.CalibrationState = maxRpm > 0 ? "Calibrated" : "Warning";
            channel.IsCalibrated = maxRpm > 0;
            var saveError = SaveCalibration(channel);
            Status = saveError is not null
                ? $"Calibration finished for {channel.Name}, but its result could not be saved: {saveError}"
                : maxRpm > 0
                    ? $"Calibration complete for {channel.Name}. Saved for future launches."
                    : $"RPM feedback is unavailable for {channel.Name}. Percentage control remains available.";
            completed = maxRpm > 0;
        }
        catch (Exception ex)
        {
            channel.CalibrationText = $"Calibration error · {ex.Message}";
            channel.CalibrationState = "Warning";
            Status = $"Calibration stopped safely: {ex.Message}";
        }
        finally
        {
            _service.Release(channel.Device);
            CalibrationProgressText = string.Empty;
            IsCalibrating = false;
            CalibrationCompleted = completed;
            IsBusy = false;
        }
    }

    private void ReleaseAll()
    {
        IsEnabled = false; _service.ReleaseAll();
        foreach (var channel in Channels) channel.IsControlled = false;
        Status = "All channels returned to firmware control.";
    }

    private void SaveProfile()
    {
        try
        {
            var safeName = string.Concat(ProfileName.Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim();
            if (string.IsNullOrWhiteSpace(safeName)) { Status = "Enter a valid profile name."; return; }
            Directory.CreateDirectory(_profileFolder);
            var data = Channels.Select(c => c.ToSettings()).ToArray();
            File.WriteAllText(Path.Combine(_profileFolder, safeName + ".json"), JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            ProfileName = safeName; ReloadProfileNames(); Status = $"Saved fan profile {safeName}.";
        }
        catch (Exception exception) { Status = $"Fan profile could not be saved: {exception.Message}"; }
    }

    private void LoadProfile()
    {
        var path = Path.Combine(_profileFolder, ProfileName + ".json");
        if (!File.Exists(path)) { Status = "Select a saved profile first."; return; }
        try
        {
            var data = JsonSerializer.Deserialize<FanChannelSettings[]>(File.ReadAllText(path)) ?? [];
            foreach (var channel in Channels)
                if (data.FirstOrDefault(x => x.Id == channel.Device.Id) is { } settings) channel.Apply(settings);
            Status = $"Loaded fan profile {ProfileName}.";
        }
        catch (Exception ex) { Status = $"Profile could not be loaded: {ex.Message}"; }
    }

    private void ReloadProfileNames()
    {
        Profiles.Clear();
        try
        {
            if (!Directory.Exists(_profileFolder)) return;
            foreach (var path in Directory.EnumerateFiles(_profileFolder, "*.json").OrderBy(Path.GetFileNameWithoutExtension))
                Profiles.Add(Path.GetFileNameWithoutExtension(path));
        }
        catch (Exception exception) { Status = $"Fan profiles could not be listed: {exception.Message}"; }
    }

    private Dictionary<string, FanCalibrationRecord> LoadCalibrationRecords()
    {
        try
        {
            if (!File.Exists(_calibrationPath)) return new(StringComparer.OrdinalIgnoreCase);
            return JsonSerializer.Deserialize<Dictionary<string, FanCalibrationRecord>>(File.ReadAllText(_calibrationPath))
                   ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private string? SaveCalibration(FanChannelViewModel channel)
    {
        try
        {
            var records = LoadCalibrationRecords();
            records[channel.Device.Id] = channel.ToCalibration();
            var folder = Path.GetDirectoryName(_calibrationPath)!;
            Directory.CreateDirectory(folder);
            var temporaryPath = _calibrationPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, _calibrationPath, true);
            return null;
        }
        catch (Exception exception)
        {
            return exception.Message;
        }
    }

    private static void WriteDiscoveryError(Exception exception)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SystemPulse", "FanControlLogs");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, $"FanDiscovery-{DateTime.Now:yyyyMMdd-HHmmss}.log"), exception.ToString());
        }
        catch { }
    }

    public void Dispose() { _timer.Stop(); _service.Dispose(); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void On(string name) => PropertyChanged?.Invoke(this, new(name));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; PropertyChanged?.Invoke(this, new(name)); return true; }
}

public sealed class FanChannelViewModel : INotifyPropertyChanged
{
    private readonly ObservableCollection<FanSensorViewModel> _sensors;
    private bool _controlled; private string _curve = "Linear"; private FanSensorViewModel? _sensor;
    private double _manual = 50, _minTemp = 35, _maxTemp = 80, _min = 25, _max = 100, _start = 35, _stop = 20, _offset, _stepUp = 10, _stepDown = 5, _hysteresis = 2, _response = 2, _targetRpm, _calibratedMaximumRpm;
    private bool _useRpmMode;
    private bool _isCalibrated;
    private string _calibrationState = "Uncalibrated";
    private string _graph = "30:20,40:30,55:50,70:75,85:100", _avoid = "", _calibration = "Not calibrated";
    private DateTime _lastChange = DateTime.MinValue; private float? _lastTemperature; private float _lastOutput;

    public FanChannelViewModel(FanControlDevice device, IEnumerable<FanSensorViewModel> sensors, IReadOnlyList<string> curves)
    {
        Device = device;
        CurveTypes = curves;
        _sensors = new ObservableCollection<FanSensorViewModel>(sensors.Where(IsRelatedSensor));
        _sensor = Category == "CPU"
            ? _sensors.FirstOrDefault(s => s.Category == "CPU" && (s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase))) ?? _sensors.FirstOrDefault()
            : _sensors.FirstOrDefault();
    }
    public FanControlDevice Device { get; }
    public string Name => Device.Name;
    public string Hardware => Device.Hardware;
    public string Category => Classify(Device);
    public string Rpm => Device.Rpm.HasValue ? $"{Device.Rpm:0} RPM" : "RPM unavailable";
    public float? RpmValue => Device.Rpm;
    public string Current => Device.CurrentPercent.HasValue ? $"{Device.CurrentPercent:0}%" : "Firmware";
    public string Pairing => Device.PairedSensorName;
    public bool CanControl => Device.CanControl;
    public string ControlAvailability => CanControl ? "Software curve control available" : "RPM monitoring only · firmware controlled";
    public IReadOnlyList<string> CurveTypes { get; }
    public ObservableCollection<FanSensorViewModel> Sensors => _sensors;
    public bool IsControlled { get => _controlled; set => Set(ref _controlled, CanControl && value); }
    public string CurveType { get => _curve; set => Set(ref _curve, value); }
    public FanSensorViewModel? SelectedSensor { get => _sensor; set => Set(ref _sensor, value); }
    public double ManualPercent { get => _manual; set => Set(ref _manual, value); }
    public double MinimumTemperature { get => _minTemp; set => Set(ref _minTemp, value); }
    public double MaximumTemperature { get => _maxTemp; set => Set(ref _maxTemp, value); }
    public double MinimumPercent { get => _min; set => Set(ref _min, value); }
    public double MaximumPercent { get => _max; set => Set(ref _max, value); }
    public double StartPercent { get => _start; set => Set(ref _start, value); }
    public double StopPercent { get => _stop; set => Set(ref _stop, value); }
    public double OffsetPercent { get => _offset; set => Set(ref _offset, value); }
    public double StepUpPercent { get => _stepUp; set => Set(ref _stepUp, value); }
    public double StepDownPercent { get => _stepDown; set => Set(ref _stepDown, value); }
    public double Hysteresis { get => _hysteresis; set => Set(ref _hysteresis, value); }
    public double ResponseSeconds { get => _response; set => Set(ref _response, value); }
    public string GraphPoints { get => _graph; set => Set(ref _graph, value); }
    public string AvoidRanges { get => _avoid; set => Set(ref _avoid, value); }
    public string CalibrationText { get => _calibration; set => Set(ref _calibration, value); }
    public bool UseRpmMode { get => _useRpmMode; set => Set(ref _useRpmMode, value); }
    public double TargetRpm { get => _targetRpm; set => Set(ref _targetRpm, value); }
    public double CalibratedMaximumRpm { get => _calibratedMaximumRpm; set => Set(ref _calibratedMaximumRpm, value); }
    public bool HasRpmCalibration => CalibratedMaximumRpm > 0;
    public bool IsCalibrated { get => _isCalibrated; set => Set(ref _isCalibrated, value); }
    public string CalibrationState { get => _calibrationState; set => Set(ref _calibrationState, value); }
    public float LastOutput => _lastOutput;

    public bool IsRelatedSensor(FanSensorViewModel sensor)
    {
        if (sensor.Category == "Storage") return false;
        if (string.Equals(Hardware, sensor.Hardware, StringComparison.OrdinalIgnoreCase)) return true;
        return Category switch
        {
            "CPU" => sensor.Category == "CPU",
            "GPU" => sensor.Category == "GPU",
            _ => sensor.Category == "Case"
        };
    }

    private static string Classify(FanControlDevice device)
    {
        var value = $"{device.Id} {device.Name} {device.Hardware} {device.HardwareType}".ToLowerInvariant();
        if (device.HardwareType.StartsWith("Gpu", StringComparison.OrdinalIgnoreCase) || ContainsAny(value, "/gpu-", "gpu", "graphics", "geforce", "radeon", "nvidia", "intel arc")) return "GPU";
        if (ContainsAny(value, "cpu fan", "cpu_fan", "cpu-fan", "processor fan", "cpu pump", "cpu opt", "cpu_opt")) return "CPU";
        return "Case";
    }

    private static bool ContainsAny(string value, params string[] terms) => terms.Any(value.Contains);

    public float? CalculateOutput(float? syncOutput)
    {
        var temp = SelectedSensor?.Value;
        if (CurveType != "Manual" && CurveType != "Flat" && CurveType != "Sync" && !temp.HasValue) return null;
        if (_lastTemperature.HasValue && temp.HasValue && Math.Abs(temp.Value - _lastTemperature.Value) < Hysteresis && (DateTime.Now - _lastChange).TotalSeconds < ResponseSeconds) return _lastOutput;
        var target = UseRpmMode && CalibratedMaximumRpm > 0
            ? TargetRpm / CalibratedMaximumRpm * 100
            : CurveType switch
        {
            "Manual" or "Flat" => ManualPercent,
            "Graph" => GraphValue(temp!.Value),
            "Trigger" => temp < MinimumTemperature ? MinimumPercent : temp > MaximumTemperature ? MaximumPercent : _lastOutput,
            "Mix" => LinearValue(Sensors.Where(s => s.Value.HasValue).Select(s => s.Value!.Value).DefaultIfEmpty(temp!.Value).Max()),
            "Sync" => syncOutput ?? ManualPercent,
            "Auto" => _lastOutput + (temp > MaximumTemperature ? StepUpPercent : temp < MaximumTemperature - Hysteresis ? -StepDownPercent / 2 : 0),
            _ => LinearValue(temp!.Value)
        } + OffsetPercent;
        target = Math.Clamp(target, MinimumPercent, MaximumPercent);
        if (target < StopPercent) target = 0;
        else if (_lastOutput <= 0 && target > 0) target = Math.Max(target, StartPercent);
        target = ApplyAvoidRanges(target);
        var delta = target - _lastOutput;
        target = _lastOutput + Math.Clamp(delta, -StepDownPercent, StepUpPercent);
        _lastOutput = (float)Math.Clamp(target, Device.Minimum, Device.Maximum); _lastTemperature = temp; _lastChange = DateTime.Now;
        return _lastOutput;
    }

    private double LinearValue(float temperature) => MinimumPercent + Math.Clamp((temperature - MinimumTemperature) / Math.Max(1, MaximumTemperature - MinimumTemperature), 0, 1) * (MaximumPercent - MinimumPercent);
    private double GraphValue(float temperature)
    {
        var points = GraphPoints.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split(':')).Where(p => p.Length == 2 && double.TryParse(p[0], out _) && double.TryParse(p[1], out _)).Select(p => (T: double.Parse(p[0]), P: double.Parse(p[1]))).OrderBy(p => p.T).ToArray();
        if (points.Length < 2) return LinearValue(temperature); if (temperature <= points[0].T) return points[0].P; if (temperature >= points[^1].T) return points[^1].P;
        for (var i = 1; i < points.Length; i++) if (temperature <= points[i].T) { var a = points[i - 1]; var b = points[i]; return a.P + (temperature - a.T) / (b.T - a.T) * (b.P - a.P); }
        return points[^1].P;
    }
    private double ApplyAvoidRanges(double value)
    {
        foreach (var item in AvoidRanges.Split(',', StringSplitOptions.RemoveEmptyEntries)) { var p = item.Split('-'); if (p.Length == 2 && double.TryParse(p[0], out var low) && double.TryParse(p[1], out var high) && value >= low && value <= high) value = Math.Abs(value - low) < Math.Abs(high - value) ? Math.Max(0, low - 1) : Math.Min(100, high + 1); }
        return value;
    }
    public void Refresh() { On(nameof(Rpm)); On(nameof(RpmValue)); On(nameof(Current)); }
    public FanCalibrationRecord ToCalibration() => new(StartPercent, MinimumPercent, CalibratedMaximumRpm, TargetRpm);
    public void ApplyCalibration(FanCalibrationRecord calibration)
    {
        StartPercent = calibration.StartPercent;
        MinimumPercent = calibration.MinimumPercent;
        CalibratedMaximumRpm = calibration.MaximumRpm;
        TargetRpm = calibration.TargetRpm;
        IsCalibrated = calibration.MaximumRpm > 0;
        CalibrationState = calibration.MaximumRpm > 0 ? "Calibrated" : "Warning";
        CalibrationText = calibration.MaximumRpm > 0
            ? $"Saved · starts at {calibration.StartPercent:0}% · max {calibration.MaximumRpm:0} RPM"
            : $"Saved · starts at {calibration.StartPercent:0}%";
        On(nameof(HasRpmCalibration));
    }
    public FanChannelSettings ToSettings() => new(Device.Id, IsControlled, CurveType, SelectedSensor?.Id, ManualPercent, MinimumTemperature, MaximumTemperature, MinimumPercent, MaximumPercent, StartPercent, StopPercent, OffsetPercent, StepUpPercent, StepDownPercent, Hysteresis, ResponseSeconds, GraphPoints, AvoidRanges, UseRpmMode, TargetRpm, CalibratedMaximumRpm);
    public void Apply(FanChannelSettings s) { IsControlled=s.Enabled; CurveType=s.CurveType; SelectedSensor=Sensors.FirstOrDefault(x=>x.Id==s.SensorId) ?? Sensors.FirstOrDefault(); ManualPercent=s.Manual; MinimumTemperature=s.MinTemp; MaximumTemperature=s.MaxTemp; MinimumPercent=s.MinPercent; MaximumPercent=s.MaxPercent; StartPercent=s.StartPercent; StopPercent=s.StopPercent; OffsetPercent=s.Offset; StepUpPercent=s.StepUp; StepDownPercent=s.StepDown; Hysteresis=s.Hysteresis; ResponseSeconds=s.ResponseSeconds; GraphPoints=s.GraphPoints; AvoidRanges=s.AvoidRanges; UseRpmMode=s.UseRpmMode; TargetRpm=s.TargetRpm; CalibratedMaximumRpm=s.CalibratedMaximumRpm; On(nameof(HasRpmCalibration)); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T f,T v,[CallerMemberName]string? n=null){if(EqualityComparer<T>.Default.Equals(f,v))return false;f=v;On(n);return true;} private void On(string? n)=>PropertyChanged?.Invoke(this,new(n));
}

public sealed class FanSensorViewModel(FanTemperatureSensor sensor) : INotifyPropertyChanged
{
    public string Id => sensor.Id; public string Name => sensor.Name; public string Hardware => sensor.Hardware; public string HardwareType => sensor.HardwareType; public float? Value => sensor.Value; public string Reading => Value.HasValue ? $"{Value:0.#}°C" : "Unavailable"; public string DisplayName => $"{Name} · {Hardware}";
    public string Category => HardwareType.StartsWith("Gpu", StringComparison.OrdinalIgnoreCase) ? "GPU"
        : HardwareType.Equals("Cpu", StringComparison.OrdinalIgnoreCase) ? "CPU"
        : HardwareType.Equals("Storage", StringComparison.OrdinalIgnoreCase) ? "Storage"
        : HardwareType.Equals("Motherboard", StringComparison.OrdinalIgnoreCase) || HardwareType.Equals("SuperIO", StringComparison.OrdinalIgnoreCase) || HardwareType.Equals("EmbeddedController", StringComparison.OrdinalIgnoreCase) || HardwareType.Equals("Cooling", StringComparison.OrdinalIgnoreCase) ? "Case"
        : "Other";
    public void Refresh(){PropertyChanged?.Invoke(this,new(nameof(Value)));PropertyChanged?.Invoke(this,new(nameof(Reading)));}
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record FanCalibrationRecord(double StartPercent, double MinimumPercent, double MaximumRpm, double TargetRpm);
public sealed record FanChannelSettings(string Id,bool Enabled,string CurveType,string? SensorId,double Manual,double MinTemp,double MaxTemp,double MinPercent,double MaxPercent,double StartPercent,double StopPercent,double Offset,double StepUp,double StepDown,double Hysteresis,double ResponseSeconds,string GraphPoints,string AvoidRanges,bool UseRpmMode=false,double TargetRpm=0,double CalibratedMaximumRpm=0);
internal sealed class RelayCommand(Action<object?> action) : ICommand { public event EventHandler? CanExecuteChanged { add{} remove{} } public bool CanExecute(object? p)=>true; public void Execute(object? p){try{action(p);}catch{}} }
internal sealed class AsyncCommand(Func<object?,Task> action) : ICommand { public event EventHandler? CanExecuteChanged { add{} remove{} } public bool CanExecute(object? p)=>true; public async void Execute(object? p){try{await action(p);}catch{}} }
