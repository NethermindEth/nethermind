// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Sockets;

namespace Nethermind.Bootnode;

internal sealed record BootnodeExternalIps(IPAddress PrimaryIp, IPAddress? IpV4, IPAddress? IpV6)
{
    public static BootnodeExternalIps Create(BootnodeOptions options, IPAddress fallbackIp)
    {
        IPAddress? externalIp = TryParse("--external-ip", options.ExternalIp, expectedFamily: null);
        IPAddress? externalIpV4 = TryParse("--external-ip-v4", options.ExternalIpV4, AddressFamily.InterNetwork);
        IPAddress? externalIpV6 = TryParse("--external-ip-v6", options.ExternalIpV6, AddressFamily.InterNetworkV6);
        IPAddress primaryIp = externalIp ?? externalIpV4 ?? externalIpV6 ?? Normalize(fallbackIp, expectedFamily: null) ?? IPAddress.None;

        return new BootnodeExternalIps(
            primaryIp,
            externalIpV4 ?? GetIpV4(externalIp) ?? GetIpV4(fallbackIp),
            externalIpV6 ?? GetIpV6(externalIp) ?? GetIpV6(fallbackIp));
    }

    public BootnodeExternalIps WithFallback(IPAddress fallbackIp)
    {
        IPAddress? fallback = Normalize(fallbackIp, expectedFamily: null);
        return new BootnodeExternalIps(
            IsUnspecified(PrimaryIp) ? fallback ?? IPAddress.None : PrimaryIp,
            IpV4 ?? GetIpV4(fallback),
            IpV6 ?? GetIpV6(fallback));
    }

    private static IPAddress? TryParse(string optionName, string? value, AddressFamily? expectedFamily)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!IPAddress.TryParse(value, out IPAddress? ipAddress))
        {
            throw new ArgumentException($"{optionName} must be a valid IP address.", optionName);
        }

        if (Normalize(ipAddress, expectedFamily) is not { } normalizedIp)
        {
            throw new ArgumentException($"{optionName} must be a usable external IP address.", optionName);
        }

        return normalizedIp;
    }

    private static IPAddress? Normalize(IPAddress? ipAddress, AddressFamily? expectedFamily)
    {
        if (ipAddress is not { } address || IsUnspecified(address))
        {
            return null;
        }

        return expectedFamily switch
        {
            AddressFamily.InterNetwork => GetIpV4(address),
            AddressFamily.InterNetworkV6 => GetIpV6(address),
            _ => address
        };
    }

    private static IPAddress? GetIpV4(IPAddress? ipAddress)
        => ipAddress is null || IsUnspecified(ipAddress)
            ? null
            : ipAddress.AddressFamily switch
            {
                AddressFamily.InterNetwork => ipAddress,
                AddressFamily.InterNetworkV6 when ipAddress.IsIPv4MappedToIPv6 => ipAddress.MapToIPv4(),
                _ => null
            };

    private static IPAddress? GetIpV6(IPAddress? ipAddress)
        => ipAddress is not null
           && !IsUnspecified(ipAddress)
           && ipAddress.AddressFamily == AddressFamily.InterNetworkV6
           && !ipAddress.IsIPv4MappedToIPv6
            ? ipAddress
            : null;

    private static bool IsUnspecified(IPAddress ipAddress)
        => ipAddress.Equals(IPAddress.Any)
           || ipAddress.Equals(IPAddress.IPv6Any)
           || ipAddress.Equals(IPAddress.None)
           || ipAddress.Equals(IPAddress.IPv6None);
}
