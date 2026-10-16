using System.Net;
using System.Net.Sockets;

namespace ResumeRelay;

/// <summary>
/// Wake-on-LAN, sent the way Z-Keeper sends it: the standard "magic packet" (6 × 0xFF, then the MAC 16 times),
/// to the network-wide broadcast address and to the player's own subnet broadcast (x.y.z.255), on UDP ports 9 and 7.
/// It only wakes a player that is in standby with its network wake option on; most players need a wired connection.
/// </summary>
public static class WakeOnLan
{
    private static int[] Ports =>
        (Environment.GetEnvironmentVariable("RESUMERELAY_WOL_PORTS") ?? "9,7")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => int.TryParse(p, out int n) ? n : 0).Where(n => n is > 0 and < 65536).DefaultIfEmpty(9).ToArray();

    public static bool Send(string mac, string lastKnownIp)
    {
        var macBytes = ParseMac(mac);
        if (macBytes == null) return false;

        var packet = new byte[102];
        for (int i = 0; i < 6; i++) packet[i] = 0xFF;
        for (int i = 0; i < 16; i++) Buffer.BlockCopy(macBytes, 0, packet, 6 + i * 6, 6);

        var targets = new List<IPAddress> { IPAddress.Broadcast };
        if (IPAddress.TryParse(lastKnownIp, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            targets.Add(new IPAddress(new byte[] { b[0], b[1], b[2], 255 }));
        }

        bool sent = false;
        using var udp = new UdpClient { EnableBroadcast = true };
        foreach (var t in targets)
            foreach (int port in Ports)
            {
                try { udp.Send(packet, packet.Length, new IPEndPoint(t, port)); sent = true; }
                catch { /* one unreachable target must not stop the others */ }
            }
        return sent;
    }

    public static byte[]? ParseMac(string? mac)
    {
        string hex = (mac ?? "").Replace(":", "").Replace("-", "").Trim();
        if (hex.Length != 12) return null;
        try { return Convert.FromHexString(hex); } catch { return null; }
    }
}
