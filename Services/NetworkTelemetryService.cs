using System.Net.NetworkInformation;
using System.Net.Sockets;
using SystemPulse.Models;

namespace SystemPulse.Services;

internal sealed class NetworkTelemetryService
{
    private readonly Dictionary<string, PreviousSample> _previous = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastRead = DateTime.UtcNow;

    public IReadOnlyList<NetworkAdapterSnapshot> Read()
    {
        var now = DateTime.UtcNow;
        var elapsed = Math.Max((now - _lastRead).TotalSeconds, 0.1);
        _lastRead = now;
        var current = new Dictionary<string, PreviousSample>(StringComparer.OrdinalIgnoreCase);
        var results = new List<ClassifiedAdapter>();

        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(item => item.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            try
            {
                var stats = adapter.GetIPv4Statistics();
                var received = (ulong)Math.Max(stats.BytesReceived, 0);
                var sent = (ulong)Math.Max(stats.BytesSent, 0);
                current[adapter.Id] = new PreviousSample(received, sent);
                _previous.TryGetValue(adapter.Id, out var previous);
                var addresses = string.Join(" · ", adapter.GetIPProperties().UnicastAddresses
                    .Where(item => item.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                    .Select(item => item.Address.ToString()).Take(3));
                var snapshot = new NetworkAdapterSnapshot(
                    adapter.Id, adapter.Name, adapter.Description, adapter.OperationalStatus.ToString(),
                    string.IsNullOrWhiteSpace(addresses) ? "No address assigned" : addresses,
                    adapter.Speed,
                    previous is null ? 0 : Rate(received, previous.Received, elapsed),
                    previous is null ? 0 : Rate(sent, previous.Sent, elapsed),
                    received, sent);
                results.Add(new ClassifiedAdapter(Classify(adapter), snapshot));
            }
            catch
            {
                // Some virtual adapters disappear while being enumerated.
            }
        }

        _previous.Clear();
        foreach (var pair in current) _previous[pair.Key] = pair.Value;
        return results
            .GroupBy(item => item.Category)
            .Select(group => Combine(group.Key, group.Select(item => item.Snapshot).ToList()))
            .OrderByDescending(item => item.Status == "Up")
            .ThenBy(item => CategoryOrder(item.Id))
            .ThenByDescending(item => item.ReceivedBytesPerSecond + item.SentBytesPerSecond)
            .ToList();
    }

    private static ulong Rate(ulong current, ulong previous, double elapsed) => current >= previous ? (ulong)((current - previous) / elapsed) : 0;

    private static string Classify(NetworkInterface adapter)
    {
        var identity = $"{adapter.Name} {adapter.Description}";
        if (ContainsAny(identity, "bluetooth"))
            return "bluetooth";
        if (ContainsAny(identity, "vpn", "tap", "tun", "wireguard", "openvpn", "virtual", "hyper-v", "vmware", "virtualbox", "vethernet", "wsl"))
            return "virtual";

        return adapter.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Wireless80211 => "wifi",
            NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel => "virtual",
            NetworkInterfaceType.Wman or NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "cellular",
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.Ethernet3Megabit or
            NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT or
            NetworkInterfaceType.GigabitEthernet => "ethernet",
            _ => "other"
        };
    }

    private static NetworkAdapterSnapshot Combine(string category, IReadOnlyList<NetworkAdapterSnapshot> adapters)
    {
        var active = adapters.Where(item => item.Status == "Up").ToList();
        var linkCandidates = active.Count > 0 ? active : adapters;
        var addresses = adapters
            .SelectMany(item => item.Addresses == "No address assigned"
                ? Array.Empty<string>()
                : item.Addresses.Split('·', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();
        var label = category switch
        {
            "ethernet" => "Ethernet",
            "wifi" => "Wi-Fi",
            "cellular" => "Cellular",
            "bluetooth" => "Bluetooth network",
            "virtual" => "VPN / virtual network",
            _ => "Other network"
        };
        var description = adapters.Count == 1
            ? SimplifyDescription(adapters[0].Description, label)
            : $"Combined live totals from {adapters.Count} {label.ToLowerInvariant()} interfaces";

        return new NetworkAdapterSnapshot(
            category,
            label,
            description,
            active.Count > 0 ? "Up" : "Disconnected",
            addresses.Count == 0 ? "No address assigned" : string.Join(" · ", addresses),
            linkCandidates.Max(item => Math.Max(item.LinkSpeedBitsPerSecond, 0)),
            Sum(adapters, item => item.ReceivedBytesPerSecond),
            Sum(adapters, item => item.SentBytesPerSecond),
            Sum(adapters, item => item.TotalReceivedBytes),
            Sum(adapters, item => item.TotalSentBytes));
    }

    private static string SimplifyDescription(string description, string fallback)
    {
        var cleaned = description?.Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? $"{fallback} connection" : cleaned;
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static ulong Sum(IEnumerable<NetworkAdapterSnapshot> adapters, Func<NetworkAdapterSnapshot, ulong> selector)
    {
        ulong total = 0;
        foreach (var adapter in adapters)
        {
            var value = selector(adapter);
            total = ulong.MaxValue - total < value ? ulong.MaxValue : total + value;
        }
        return total;
    }

    private static int CategoryOrder(string category) => category switch
    {
        "ethernet" => 0,
        "wifi" => 1,
        "cellular" => 2,
        "virtual" => 3,
        "bluetooth" => 4,
        _ => 5
    };

    private sealed record PreviousSample(ulong Received, ulong Sent);
    private sealed record ClassifiedAdapter(string Category, NetworkAdapterSnapshot Snapshot);
}
