using SystemPulse.Models;

namespace SystemPulse.Services;

internal sealed class AlertService
{
    private readonly Dictionary<string, DateTime> _lastRaised = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);

    public IReadOnlyList<MonitorAlert> Evaluate(SensorSnapshot snapshot, AppSettings settings)
    {
        if (!settings.AlertsEnabled)
        {
            _active.Clear();
            return Array.Empty<MonitorAlert>();
        }

        var alerts = new List<MonitorAlert>();
        if (settings.CpuAlertsEnabled)
            EvaluateTemperature(alerts, "cpu-temp", "CPU temperature", snapshot.CpuTemperature, settings.CpuTemperatureAlert, snapshot.Timestamp);
        else
            DeactivatePrefix("cpu-temp");
        if (settings.GpuAlertsEnabled)
            EvaluateTemperature(alerts, "gpu-temp", "GPU temperature", snapshot.GpuTemperature, settings.GpuTemperatureAlert, snapshot.Timestamp);
        else
            DeactivatePrefix("gpu-temp");
        foreach (var drive in snapshot.StorageDevices)
        {
            if (settings.StorageTemperatureAlertsEnabled)
                EvaluateTemperature(alerts, $"drive-temp:{drive.DeviceId}", $"{drive.DisplayName} temperature", drive.Temperature, settings.StorageTemperatureAlert, snapshot.Timestamp);
            if (settings.StorageHealthAlertsEnabled && drive.Health is "Warning" or "Unhealthy")
                Raise(alerts, $"drive-health:{drive.DeviceId}", "Drive health warning", $"{drive.DisplayName} reports {drive.Health.ToLowerInvariant()} health.", snapshot.Timestamp);
        }
        if (!settings.StorageTemperatureAlertsEnabled)
            DeactivatePrefix("drive-temp:");
        if (!settings.StorageHealthAlertsEnabled)
            DeactivatePrefix("drive-health:");
        return alerts;
    }

    private void DeactivatePrefix(string prefix) =>
        _active.RemoveWhere(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private void EvaluateTemperature(List<MonitorAlert> alerts, string key, string title, float? value, int threshold, DateTime now)
    {
        if (value >= threshold)
            Raise(alerts, key, title, $"{title} reached {value:0} °C (limit {threshold} °C).", now);
        else if (value < threshold - 5)
            _active.Remove(key);
    }

    private void Raise(List<MonitorAlert> alerts, string key, string title, string message, DateTime now)
    {
        if (_active.Contains(key) && _lastRaised.TryGetValue(key, out var last) && now - last < Cooldown)
            return;
        _active.Add(key);
        _lastRaised[key] = now;
        alerts.Add(new MonitorAlert(key, title, message, now));
    }
}
