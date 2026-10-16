using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ResumeRelay;

/// <summary>
/// Finds Zidoo players the same way Z-Keeper does: try TCP port 9529 on every address of each local /24,
/// then confirm with getModel (which also gives us the MAC we identify the player by).
/// </summary>
public static class Discovery
{
    public const int ZidooPort = 9529;

    public sealed record Found(string Ip, ModelInfo Info);

    public static async Task<List<Found>> ScanAsync(CancellationToken ct = default)
    {
        var hosts = LocalSubnets().SelectMany(Hosts).Distinct().ToList();
        var found = new List<Found>();
        using var gate = new SemaphoreSlim(96);
        await Task.WhenAll(hosts.Select(async ip =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (!await IsPortOpenAsync(ip, ct)) return;
                var info = await new ZidooClient(ip).GetModelAsync(TimeSpan.FromSeconds(4), ct);
                if (info != null && info.Identity.Length > 0)
                    lock (found) found.Add(new Found(ip, info));
            }
            finally { gate.Release(); }
        }));
        return found.OrderBy(f => IPAddress.Parse(f.Ip).GetAddressBytes(), new BytesComparer()).ToList();
    }

    /// <summary>The /24 networks this machine is on (larger networks are narrowed to the machine's own /24).</summary>
    public static List<string> LocalSubnets()
    {
        var nets = new List<string>();
        // Some adapters (VPNs, virtual switches, half-configured Wi-Fi) throw when asked for their details.
        // One odd adapter must not stop the scan, so each is read on its own.
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
        catch { nics = Array.Empty<NetworkInterface>(); }
        foreach (var nic in nics)
        {
            try
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                string name = nic.Name.ToLowerInvariant();
                if (name.StartsWith("docker") || name.StartsWith("br-") || name.StartsWith("veth") || name.StartsWith("virbr")) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                    AddIfUsable(nets, ua.Address);
            }
            catch { /* skip this adapter */ }
        }

        // Fallback: the address this computer would use to reach the internet. Opening a UDP socket sends nothing.
        if (nets.Count == 0)
        {
            try
            {
                using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                s.Connect("8.8.8.8", 53);
                if (s.LocalEndPoint is IPEndPoint ep) AddIfUsable(nets, ep.Address);
            }
            catch { }
        }
        // Extra networks for unusual setups or testing, e.g. RESUMERELAY_SCAN_EXTRA="192.168.50,127.0.0"
        foreach (var extra in (Environment.GetEnvironmentVariable("RESUMERELAY_SCAN_EXTRA") ?? "")
                     .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var octets = extra.Split('/')[0].Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (octets.Length >= 3) nets.Add(string.Join('.', octets.Take(3)));
        }
        return nets.Distinct().ToList();
    }

    private static void AddIfUsable(List<string> nets, IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return;
        var b = address.GetAddressBytes();
        if (b[0] == 127 || (b[0] == 169 && b[1] == 254)) return; // loopback, link-local
        nets.Add($"{b[0]}.{b[1]}.{b[2]}");
    }

    private static IEnumerable<string> Hosts(string prefix24) =>
        Enumerable.Range(1, 254).Select(i => $"{prefix24}.{i}");

    private static async Task<bool> IsPortOpenAsync(string ip, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(600));
        using var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(IPAddress.Parse(ip), ZidooPort, cts.Token);
            return true;
        }
        catch { return false; }
    }

    private sealed class BytesComparer : IComparer<byte[]>
    {
        public int Compare(byte[]? x, byte[]? y)
        {
            for (int i = 0; i < Math.Min(x!.Length, y!.Length); i++)
                if (x[i] != y[i]) return x[i].CompareTo(y[i]);
            return x.Length.CompareTo(y.Length);
        }
    }
}
