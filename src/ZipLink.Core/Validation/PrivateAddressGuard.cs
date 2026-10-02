using System.Net;
using System.Net.Sockets;

namespace ZipLink.Core.Validation;

/// <summary>
/// Pure, offline guard that recognises hosts pointing at private or otherwise internal
/// network locations. Used to stop server-side request forgery style targets from being
/// shortened. No DNS resolution is performed, so results are deterministic.
/// </summary>
public static class PrivateAddressGuard
{
    private static readonly string[] BlockedHostSuffixes =
    {
        ".localhost",
        ".local",
        ".internal",
        ".home.arpa"
    };

    /// <summary>
    /// Returns <c>true</c> when the host of <paramref name="uri"/> is a literal private or
    /// internal IP address, or an obviously internal host name such as <c>localhost</c>.
    /// </summary>
    /// <param name="uri">The absolute URI to inspect.</param>
    public static bool IsPrivateOrInternal(Uri uri)
    {
        if (uri == null)
        {
            throw new ArgumentNullException(nameof(uri));
        }

        return IsBlockedHost(uri.DnsSafeHost);
    }

    /// <summary>
    /// Returns <c>true</c> when the supplied host is a literal private or internal IP address,
    /// or an obviously internal host name such as <c>localhost</c>.
    /// </summary>
    /// <param name="host">The host component of a URL. IPv6 literals may be bracketed.</param>
    public static bool IsBlockedHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return true;
        }

        var candidate = host.Trim();

        if (candidate.Length > 1 && candidate[0] == '[' && candidate[^1] == ']')
        {
            candidate = candidate.Substring(1, candidate.Length - 2);
        }

        var zoneIndex = candidate.IndexOf('%');
        if (zoneIndex >= 0)
        {
            candidate = candidate.Substring(0, zoneIndex);
        }

        if (candidate.Length == 0)
        {
            return true;
        }

        if (IPAddress.TryParse(candidate, out var address))
        {
            return IsBlockedAddress(address);
        }

        return IsBlockedHostName(candidate);
    }

    private static bool IsBlockedAddress(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsBlockedIPv4(address.GetAddressBytes()),
            AddressFamily.InterNetworkV6 => IsBlockedIPv6(address),
            _ => true
        };
    }

    private static bool IsBlockedIPv4(byte[] bytes)
    {
        // 0.0.0.0/8 - "this network", includes the unspecified address.
        if (bytes[0] == 0)
        {
            return true;
        }

        // 10.0.0.0/8
        if (bytes[0] == 10)
        {
            return true;
        }

        // 127.0.0.0/8 - loopback.
        if (bytes[0] == 127)
        {
            return true;
        }

        // 169.254.0.0/16 - link local, includes the cloud metadata endpoint.
        if (bytes[0] == 169 && bytes[1] == 254)
        {
            return true;
        }

        // 172.16.0.0/12
        if (bytes[0] == 172 && (bytes[1] & 0xF0) == 16)
        {
            return true;
        }

        // 192.168.0.0/16
        if (bytes[0] == 192 && bytes[1] == 168)
        {
            return true;
        }

        // 100.64.0.0/10 - carrier grade NAT.
        if (bytes[0] == 100 && (bytes[1] & 0xC0) == 64)
        {
            return true;
        }

        // 255.255.255.255 - broadcast.
        if (bytes[0] == 255 && bytes[1] == 255 && bytes[2] == 255 && bytes[3] == 255)
        {
            return true;
        }

        return false;
    }

    private static bool IsBlockedIPv6(IPAddress address)
    {
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
        {
            return true;
        }

        var bytes = address.GetAddressBytes();

        // fc00::/7 - unique local addresses.
        if ((bytes[0] & 0xFE) == 0xFC)
        {
            return true;
        }

        // fe80::/10 - link local (also covered by IsIPv6LinkLocal, kept explicit).
        if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)
        {
            return true;
        }

        // :: (unspecified) and ::1 (loopback).
        var allZeroExceptLast = true;
        for (var i = 0; i < bytes.Length - 1; i++)
        {
            if (bytes[i] != 0)
            {
                allZeroExceptLast = false;
                break;
            }
        }

        if (allZeroExceptLast && (bytes[^1] == 0 || bytes[^1] == 1))
        {
            return true;
        }

        return false;
    }

    private static bool IsBlockedHostName(string host)
    {
        var trimmed = host.TrimEnd('.');

        if (trimmed.Length == 0)
        {
            return true;
        }

        if (string.Equals(trimmed, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var suffix in BlockedHostSuffixes)
        {
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
