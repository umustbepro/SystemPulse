using System.Management;

namespace SystemPulse.Services;

internal sealed class MotherboardTemperatureReader
{
    public MotherboardTemperatureSample Read()
    {
        try
        {
            var zones = new List<(string Name, float Temperature)>();
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI",
                "SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            using var results = searcher.Get();

            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    if (item["CurrentTemperature"] is null)
                        continue;

                    var temperature = Convert.ToSingle(item["CurrentTemperature"]) / 10f - 273.15f;
                    if (temperature is <= 0 or >= 130)
                        continue;

                    zones.Add((Convert.ToString(item["InstanceName"]) ?? "ACPI thermal zone", temperature));
                }
            }

            if (zones.Count == 0)
                return Unavailable();

            var preferred = zones
                .OrderByDescending(zone => IsLikelyBoardZone(zone.Name))
                .ThenByDescending(zone => zone.Temperature)
                .First();
            return new MotherboardTemperatureSample(preferred.Temperature, "Windows ACPI thermal zone");
        }
        catch
        {
            return Unavailable();
        }
    }

    private static bool IsLikelyBoardZone(string name)
    {
        var upper = name.ToUpperInvariant();
        return upper.Contains("MOTHERBOARD") || upper.Contains("MAINBOARD") ||
               upper.Contains("SYSTEM") || upper.Contains("THM") || upper.Contains("TZ");
    }

    private static MotherboardTemperatureSample Unavailable() =>
        new(null, "Not exposed by motherboard firmware");
}

internal sealed record MotherboardTemperatureSample(float? Temperature, string Source);
