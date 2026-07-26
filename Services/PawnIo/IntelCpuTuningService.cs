using System.IO;

namespace SystemPulse.Services.PawnIo;

internal sealed class IntelCpuTuningService
{
    private const uint PlatformInfo = 0x00CE;
    private const uint TurboRatioLimit = 0x01AD;
    private const uint RaplPowerUnit = 0x0606;
    private const uint PackagePowerLimit = 0x0610;
    private const uint PackagePowerInfo = 0x0614;
    private const ulong PackagePowerLock = 1UL << 63;
    private readonly object _syncRoot = new();

    public IntelCpuTuningState Detect(string cpuName)
    {
        lock (_syncRoot)
        {
            if (!IsUnlockedIntelName(cpuName))
                return IntelCpuTuningState.Unavailable("Intel CPU tuning is limited to unlocked K, KF, KS, HK, X, and XE models.");

            try
            {
                using var client = PawnIoClient.Load(ModulePath());
                var platform = client.ReadMsr(PlatformInfo);
                var ratioRaw = client.ReadMsr(TurboRatioLimit);
                var powerUnitRaw = client.ReadMsr(RaplPowerUnit);
                var powerRaw = client.ReadMsr(PackagePowerLimit);
                var powerInfo = client.ReadMsr(PackagePowerInfo);
                var powerUnit = Math.Pow(0.5, (int)(powerUnitRaw & 0xF));

                var programmableRatio = (platform & (1UL << 28)) != 0;
                var programmablePower = (platform & (1UL << 29)) != 0 &&
                                        (powerRaw & PackagePowerLock) == 0;
                var canWriteRatio = programmableRatio && TryWriteSameValue(client, TurboRatioLimit, ratioRaw);
                var canWritePower = programmablePower && TryWriteSameValue(client, PackagePowerLimit, powerRaw);

                var currentRatio = DecodeAllCoreRatio(ratioRaw);
                var minimumRatio = Math.Max(8, currentRatio - 5);
                var maximumRatio = Math.Min(85, currentRatio + 5);
                var currentPower = DecodePower(powerRaw, powerUnit);
                var minimumPower = DecodePower((powerInfo >> 16) & 0x7FFF, powerUnit);
                var maximumPower = DecodePower((powerInfo >> 32) & 0x7FFF, powerUnit);
                if (minimumPower <= 0 || minimumPower >= currentPower)
                    minimumPower = Math.Max(10, currentPower * 0.5);
                if (maximumPower <= currentPower || maximumPower > 500)
                    maximumPower = Math.Min(500, Math.Max(currentPower + 25, currentPower * 1.5));

                var supported = canWriteRatio || canWritePower;
                var details = supported
                    ? $"PawnIO direct Intel tuning · ratio {(canWriteRatio ? "writable" : "firmware/module locked")} · package power {(canWritePower ? "writable" : "locked")}"
                    : "The CPU is unlocked, but firmware or the signed PawnIO module rejected writable tuning controls.";

                return new IntelCpuTuningState(
                    supported,
                    canWriteRatio,
                    canWritePower,
                    currentRatio,
                    minimumRatio,
                    maximumRatio,
                    currentPower,
                    minimumPower,
                    maximumPower,
                    ratioRaw,
                    powerRaw,
                    powerUnit,
                    details);
            }
            catch (Exception exception)
            {
                return IntelCpuTuningState.Unavailable($"PawnIO Intel tuning unavailable: {exception.Message}");
            }
        }
    }

    public CpuTuningResult Apply(IntelCpuTuningState state, double coreClockMhz, double packagePowerWatts)
    {
        lock (_syncRoot)
        {
            if (!state.IsSupported)
                return new(false, state.Status);

            var ratio = (int)Math.Round(coreClockMhz / 100d);
            if ((state.CanSetRatio && ratio is < 8 or > 85) ||
                (state.CanSetPower && (packagePowerWatts < state.MinimumPowerWatts || packagePowerWatts > state.MaximumPowerWatts)))
                return new(false, "A CPU tuning value is outside the verified PawnIO range.");

            try
            {
                using var client = PawnIoClient.Load(ModulePath());
                var powerWritten = false;
                if (state.CanSetPower)
                {
                    var updatedPower = EncodePackagePower(state.OriginalPowerLimit, packagePowerWatts, state.PowerUnit);
                    client.WriteMsr(PackagePowerLimit, updatedPower);
                    if (client.ReadMsr(PackagePowerLimit) != updatedPower)
                        throw new PawnIoException("Intel package power-limit read-back did not match the requested value.");
                    powerWritten = true;
                }

                if (state.CanSetRatio)
                {
                    var updatedRatio = EncodeAllCoreRatio(state.OriginalRatioLimit, ratio);
                    try
                    {
                        client.WriteMsr(TurboRatioLimit, updatedRatio);
                        if (client.ReadMsr(TurboRatioLimit) != updatedRatio)
                            throw new PawnIoException("Intel turbo-ratio read-back did not match the requested value.");
                    }
                    catch
                    {
                        if (powerWritten)
                            client.WriteMsr(PackagePowerLimit, state.OriginalPowerLimit);
                        throw;
                    }
                }

                var controls = string.Join(" and ", new[]
                {
                    state.CanSetRatio ? $"{ratio * 100} MHz all-core turbo target" : null,
                    state.CanSetPower ? $"{packagePowerWatts:0.#} W package power limit" : null
                }.Where(value => value is not null));
                return new(true, $"Applied {controls} directly through PawnIO. Voltage and memory timing remain firmware controlled.");
            }
            catch (Exception exception)
            {
                return new(false, $"Intel CPU tuning was rejected and the session baseline was restored where possible: {exception.Message}");
            }
        }
    }

    public CpuTuningResult Reset(IntelCpuTuningState state)
    {
        lock (_syncRoot)
        {
            if (!state.IsSupported)
                return new(false, state.Status);

            try
            {
                using var client = PawnIoClient.Load(ModulePath());
                if (state.CanSetPower)
                    client.WriteMsr(PackagePowerLimit, state.OriginalPowerLimit);
                if (state.CanSetRatio)
                    client.WriteMsr(TurboRatioLimit, state.OriginalRatioLimit);

                if ((state.CanSetPower && client.ReadMsr(PackagePowerLimit) != state.OriginalPowerLimit) ||
                    (state.CanSetRatio && client.ReadMsr(TurboRatioLimit) != state.OriginalRatioLimit))
                    return new(false, "Intel CPU baseline read-back did not match. Restart and restore defaults in BIOS before further tuning.");

                return new(true, "Intel CPU ratio and package power were restored to the values captured when SystemPulse started.");
            }
            catch (Exception exception)
            {
                return new(false, $"Intel CPU baseline could not be restored: {exception.Message}");
            }
        }
    }

    private static bool TryWriteSameValue(PawnIoClient client, uint register, ulong value)
    {
        try
        {
            client.WriteMsr(register, value);
            return client.ReadMsr(register) == value;
        }
        catch
        {
            return false;
        }
    }

    private static int DecodeAllCoreRatio(ulong value)
    {
        var ratios = Enumerable.Range(0, 8)
            .Select(index => (int)(value >> (index * 8) & 0xFF))
            .Where(ratio => ratio is >= 8 and <= 85)
            .ToArray();
        return ratios.Length == 0 ? 0 : ratios.Min();
    }

    private static ulong EncodeAllCoreRatio(ulong original, int ratio)
    {
        var updated = original;
        for (var index = 0; index < 8; index++)
        {
            var shift = index * 8;
            var current = (int)(original >> shift & 0xFF);
            if (current == 0)
                continue;
            updated = (updated & ~(0xFFUL << shift)) | ((ulong)ratio << shift);
        }
        return updated;
    }

    private static double DecodePower(ulong encoded, double unit) => (encoded & 0x7FFF) * unit;

    private static ulong EncodePackagePower(ulong original, double watts, double unit)
    {
        var encoded = (ulong)Math.Clamp(Math.Round(watts / unit), 1, 0x7FFF);
        var updated = original & ~0x7FFFUL & ~(0x7FFFUL << 32);
        updated |= encoded | 1UL << 15;
        updated |= encoded << 32 | 1UL << 47;
        return updated;
    }

    private static bool IsUnlockedIntelName(string name)
    {
        var token = name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(part => part.Any(char.IsDigit) &&
                                    (part.Contains("K", StringComparison.OrdinalIgnoreCase) ||
                                     part.Contains("X", StringComparison.OrdinalIgnoreCase)));
        return token is not null &&
               (token.EndsWith("K", StringComparison.OrdinalIgnoreCase) ||
                token.EndsWith("KF", StringComparison.OrdinalIgnoreCase) ||
                token.EndsWith("KS", StringComparison.OrdinalIgnoreCase) ||
                token.EndsWith("HK", StringComparison.OrdinalIgnoreCase) ||
                token.EndsWith("X", StringComparison.OrdinalIgnoreCase) ||
                token.EndsWith("XE", StringComparison.OrdinalIgnoreCase));
    }

    private static string ModulePath() =>
        Path.Combine(AppContext.BaseDirectory, "PawnIO", "Modules", "IntelMSR.bin");
}

internal sealed record IntelCpuTuningState(
    bool IsSupported,
    bool CanSetRatio,
    bool CanSetPower,
    int CurrentRatio,
    int MinimumRatio,
    int MaximumRatio,
    double CurrentPowerWatts,
    double MinimumPowerWatts,
    double MaximumPowerWatts,
    ulong OriginalRatioLimit,
    ulong OriginalPowerLimit,
    double PowerUnit,
    string Status)
{
    public static IntelCpuTuningState Unavailable(string status) =>
        new(false, false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, status);
}

internal sealed record CpuTuningResult(bool Success, string Message);
