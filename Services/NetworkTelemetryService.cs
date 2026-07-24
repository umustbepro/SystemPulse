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
        var results = new List<NetworkAdapterSnapshot>();

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
                results.Add(new NetworkAdapterSnapshot(
                    adapter.Id, adapter.Name, adapter.Description, adapter.OperationalStatus.ToString(),
                    string.IsNullOrWhiteSpace(addresses) ? "No address assigned" : addresses,
                    adapter.Speed,
                    previous is null ? 0 : Rate(received, previous.Received, elapsed),
                    previous is null ? 0 : Rate(sent, previous.Sent, elapsed),
                    received, sent));
            }
            catch
            {
                // Some virtual adapters disappear while being enumerated.
            }
        }

        _previous.Clear();
        foreach (var pair in current) _previous[pair.Key] = pair.Value;
        return results.OrderByDescending(item => item.Status == "Up").ThenByDescending(item => item.ReceivedBytesPerSecond + item.SentBytesPerSecond).ToList();
    }

    private static ulong Rate(ulong current, ulong previous, double elapsed) => current >= previous ? (ulong)((current - previous) / elapsed) : 0;
    private sealed record PreviousSample(ulong Received, ulong Sent);
}
