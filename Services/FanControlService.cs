using LibreHardwareMonitor.Hardware;

namespace SystemPulse.Services;

public sealed class FanControlService : IDisposable
{
    private Computer Computer => SharedLibreHardware.Computer;
    private object Gate => SharedLibreHardware.SyncRoot;
    private readonly List<FanControlDevice> _controls = [];
    private readonly List<FanTemperatureSensor> _temperatures = [];
    private readonly FirmwareFanTelemetryProvider _firmwareTelemetry = new();
    private bool _open;
    private bool _disposed;

    public FanDiscoveryResult Discover()
    {
        lock (Gate)
        {
            ThrowIfDisposed();
            EnsureOpen();
            foreach (var existing in _controls)
                try { existing.Release(); } catch { }
            _controls.Clear();
            _temperatures.Clear();
            foreach (var hardware in Computer.Hardware)
                Collect(hardware, hardware.Name);
            PairSpeedSensors();
            ApplyFirmwareRpmFallback(force: true);
            return new FanDiscoveryResult(_controls.ToArray(), _temperatures.ToArray());
        }
    }

    public void Refresh()
    {
        lock (Gate)
        {
            if (!_open || _disposed) return;
            foreach (var hardware in Computer.Hardware)
                UpdateRecursive(hardware);
            foreach (var control in _controls) control.Refresh();
            foreach (var sensor in _temperatures) sensor.Refresh();
            ApplyFirmwareRpmFallback(force: false);
        }
    }

    public void SetSoftware(FanControlDevice device, float value)
    {
        lock (Gate)
        {
            ThrowIfDisposed();
            device.SetSoftware(value);
        }
    }

    public void Release(FanControlDevice device)
    {
        lock (Gate)
        {
            try { device.Release(); } catch { }
        }
    }

    public void ReleaseAll()
    {
        lock (Gate)
            foreach (var control in _controls)
                try { control.Release(); } catch { }
    }

    private void EnsureOpen()
    {
        if (_open) return;
        SharedLibreHardware.Acquire();
        _open = true;
    }

    private void Collect(IHardware hardware, string rootName)
    {
        hardware.Update();
        var fans = hardware.Sensors.Where(s => s.SensorType == SensorType.Fan).ToArray();
        var controls = hardware.Sensors.Where(s => s.SensorType == SensorType.Control && s.Control is not null).ToArray();
        var usedControls = new HashSet<ISensor>();
        foreach (var fan in fans)
        {
            var number = Digits(fan.Name);
            var matched = controls.FirstOrDefault(control => number.Length > 0 && Digits(control.Name) == number)
                          ?? controls.Where(control => !usedControls.Contains(control)).OrderBy(control => NameDistance(fan.Name, control.Name)).FirstOrDefault();
            if (matched is not null) usedControls.Add(matched);
            _controls.Add(new FanControlDevice(fan.Identifier.ToString(), fan.Name, rootName, hardware.HardwareType.ToString(), fan, matched, matched?.Control));
        }
        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.SensorType == SensorType.Temperature)
                _temperatures.Add(new FanTemperatureSensor(sensor.Identifier.ToString(), sensor.Name, rootName, hardware.HardwareType.ToString(), sensor));
        }
        foreach (var control in controls.Where(control => !usedControls.Contains(control)))
            _controls.Add(new FanControlDevice(control.Identifier.ToString(), control.Name, rootName, hardware.HardwareType.ToString(), null, control, control.Control));
        foreach (var child in hardware.SubHardware)
            Collect(child, rootName);
    }

    private void PairSpeedSensors()
    {
        foreach (var control in _controls)
            control.AutoPair();
    }

    private void ApplyFirmwareRpmFallback(bool force)
    {
        var readings = _firmwareTelemetry.ReadRpms(force);
        if (readings.Count == 0) return;
        var targets = _controls.Where(control => !control.HasUsableNativeRpm)
            .OrderBy(control => control.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(control => control.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        for (var index = 0; index < targets.Length; index++)
            targets[index].SetFallbackRpm(index < readings.Count ? readings[index] : null);
    }

    private static string Digits(string value) => new(value.Where(char.IsDigit).ToArray());
    private static int NameDistance(string left, string right) =>
        Math.Abs(left.Length - right.Length) + left.Zip(right).Count(pair => char.ToUpperInvariant(pair.First) != char.ToUpperInvariant(pair.Second));

    private static void UpdateRecursive(IHardware hardware)
    {
        hardware.Update();
        foreach (var child in hardware.SubHardware) UpdateRecursive(child);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FanControlService));
    }

    public void Dispose()
    {
        if (_disposed) return;
        ReleaseAll();
        if (_open) SharedLibreHardware.Release();
        _disposed = true;
    }
}

public sealed class FanControlDevice
{
    private readonly ISensor? _fanSensor;
    private readonly ISensor? _controlSensor;
    private readonly IControl? _control;
    private float? _fallbackRpm;

    internal FanControlDevice(string id, string name, string hardware, string hardwareType, ISensor? fanSensor, ISensor? controlSensor, IControl? control)
    {
        Id = id; Name = name; Hardware = hardware; HardwareType = hardwareType; _fanSensor = fanSensor; _controlSensor = controlSensor; _control = control;
        Minimum = control is not null && float.IsFinite(control.MinSoftwareValue) ? control.MinSoftwareValue : 0;
        Maximum = control is not null && float.IsFinite(control.MaxSoftwareValue) && control.MaxSoftwareValue > Minimum ? control.MaxSoftwareValue : 100;
        Refresh();
    }

    public string Id { get; }
    public string Name { get; }
    public string Hardware { get; }
    public string HardwareType { get; }
    public float Minimum { get; }
    public float Maximum { get; }
    public float? CurrentPercent { get; private set; }
    public float? Rpm { get; private set; }
    internal bool HasUsableNativeRpm => _fanSensor?.Value is float value && float.IsFinite(value) && value > 0;
    public bool CanControl => _control is not null;
    public string PairedSensorName => HasUsableNativeRpm && _fanSensor?.Name is { Length: > 0 } name
        ? $"RPM sensor: {name}"
        : _fallbackRpm.HasValue ? "RPM source: firmware telemetry fallback"
        : _fanSensor?.Name is { Length: > 0 } detectedName ? $"RPM sensor unavailable: {detectedName}" : "No RPM sensor paired";

    internal void AutoPair()
    {
        Refresh();
    }

    internal void Refresh()
    {
        CurrentPercent = _controlSensor?.Value ?? _control?.SoftwareValue;
        var nativeRpm = _fanSensor?.Value;
        Rpm = nativeRpm is float value && float.IsFinite(value) && value > 0 ? value : _fallbackRpm ?? nativeRpm;
    }

    internal void SetFallbackRpm(float? value)
    {
        _fallbackRpm = value is > 0 and < 50000 ? value : null;
        Refresh();
    }

    internal void SetSoftware(float value)
    {
        if (_control is null) throw new InvalidOperationException($"{Name} reports RPM but its firmware does not expose software control.");
        _control.SetSoftware(Math.Clamp(value, Minimum, Maximum));
    }
    internal void Release() { if (_control is not null) _control.SetDefault(); }
}

public sealed class FanTemperatureSensor
{
    private readonly ISensor _sensor;
    internal FanTemperatureSensor(string id, string name, string hardware, string hardwareType, ISensor sensor)
    { Id = id; Name = name; Hardware = hardware; HardwareType = hardwareType; _sensor = sensor; Refresh(); }
    public string Id { get; }
    public string Name { get; }
    public string Hardware { get; }
    public string HardwareType { get; }
    public float? Value { get; private set; }
    internal void Refresh() => Value = _sensor.Value is float value && float.IsFinite(value) && value is > -30 and < 160 ? value : null;
}

public sealed record FanDiscoveryResult(IReadOnlyList<FanControlDevice> Controls, IReadOnlyList<FanTemperatureSensor> Temperatures);
