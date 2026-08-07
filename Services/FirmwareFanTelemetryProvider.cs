using System.Management;

namespace SystemPulse.Services;

/// <summary>
/// Safe, read-only firmware fallback for machines whose Super I/O control channels
/// are exposed through PawnIO but whose tachometer values are omitted by the primary provider.
/// OEM firmware may publish RPM through CIM tachometers or Win32_Fan DesiredSpeed.
/// </summary>
internal sealed class FirmwareFanTelemetryProvider
{
    private readonly object _gate = new();
    private DateTime _lastReadUtc = DateTime.MinValue;
    private IReadOnlyList<float> _cached = Array.Empty<float>();

    internal IReadOnlyList<float> ReadRpms(bool force)
    {
        lock (_gate)
        {
            if (!force && DateTime.UtcNow - _lastReadUtc < TimeSpan.FromSeconds(2)) return _cached;
            _lastReadUtc = DateTime.UtcNow;
            var readings = ReadValues("SELECT CurrentReading FROM CIM_NumericSensor WHERE SensorType = 5", "CurrentReading");
            if (readings.Count == 0)
                readings = ReadValues("SELECT DesiredSpeed FROM Win32_Fan WHERE ActiveCooling = TRUE", "DesiredSpeed");
            _cached = readings;
            return _cached;
        }
    }

    private static IReadOnlyList<float> ReadValues(string query, string property)
    {
        var values = new List<float>();
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\cimv2", query);
            using var results = searcher.Get();
            foreach (ManagementBaseObject item in results)
            {
                if (item[property] is null) continue;
                if (!float.TryParse(Convert.ToString(item[property], System.Globalization.CultureInfo.InvariantCulture), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rpm)) continue;
                if (float.IsFinite(rpm) && rpm is > 0 and < 50000) values.Add(rpm);
            }
        }
        catch
        {
            // Most consumer firmware does not publish these optional CIM classes.
        }
        return values;
    }
}
