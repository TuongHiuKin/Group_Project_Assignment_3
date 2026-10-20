using System.Net;

namespace AIVES.BLL.Services.Ai;

/// <summary>
/// Helper for inspecting AI service endpoints (detecting loopback and RFC 1918 private LAN addresses).
/// </summary>
public static class NetworkEndpointHelper
{
    /// <summary>
    /// Checks if the given URL points to a local machine or private LAN address (localhost, 127.0.0.1, 10.x, 192.168.x, 172.16-31.x).
    /// </summary>
    public static bool IsLocalOrLan(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        if (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IPAddress.TryParse(uri.Host, out var ip))
        {
            if (IPAddress.IsLoopback(ip)) return true;

            var bytes = ip.GetAddressBytes();
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                // 10.0.0.0/8
                if (bytes[0] == 10) return true;
                // 172.16.0.0/12 (172.16.0.0 - 172.31.255.255)
                if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
                // 192.168.0.0/16
                if (bytes[0] == 192 && bytes[1] == 168) return true;
            }
        }

        return false;
    }
}
