// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Nethermind.Network.Discovery;

/// <summary>
/// Describes which remote address families the local RLPx and discovery sockets can serve from their shared bind address.
/// </summary>
public static class DiscoveryAddressSupport
{
    /// <summary>
    /// Returns whether a socket bound to <paramref name="localIp"/> can send to and receive from <paramref name="remoteIp"/>.
    /// </summary>
    /// <remarks>
    /// A native IPv4 socket cannot use an endpoint that remains in IPv4-mapped IPv6 form; callers must unmap it first.
    /// </remarks>
    /// <param name="localIp">The address used to bind the socket.</param>
    /// <param name="remoteIp">The remote address to test.</param>
    /// <returns><see langword="true"/> when the bound socket can serve the remote address.</returns>
    internal static bool Supports(IPAddress localIp, IPAddress remoteIp)
        => !(localIp.AddressFamily == AddressFamily.InterNetwork && remoteIp.IsIPv4MappedToIPv6) &&
           SupportsFamily(localIp, GetFamily(remoteIp));

    /// <summary>
    /// Returns the effective address family, treating IPv4-mapped IPv6 addresses as IPv4.
    /// </summary>
    /// <param name="address">The address whose effective family is required.</param>
    /// <returns>The effective IPv4 or IPv6 address family.</returns>
    public static AddressFamily GetFamily(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? AddressFamily.InterNetwork : address.AddressFamily;

    private static bool SupportsFamily(IPAddress localIp, AddressFamily addressFamily)
        => addressFamily switch
        {
            AddressFamily.InterNetwork =>
                localIp.AddressFamily == AddressFamily.InterNetwork ||
                localIp.IsIPv4MappedToIPv6 ||
                localIp.Equals(IPAddress.IPv6Any),
            AddressFamily.InterNetworkV6 =>
                localIp.AddressFamily == AddressFamily.InterNetworkV6 &&
                !localIp.IsIPv4MappedToIPv6,
            _ => false
        };

    /// <summary>
    /// Selects the external addresses supported by the local RLPx and discovery listeners.
    /// </summary>
    /// <remarks>
    /// RLPx and discovery each bind a single socket to <paramref name="localIp"/>, so an address family is
    /// advertised only when that socket can receive it; otherwise peers would dial an endpoint nothing is
    /// listening on.
    /// </remarks>
    /// <param name="localIp">The address used to bind the local RLPx and discovery sockets.</param>
    /// <param name="externalIpV4">The resolved external IPv4 address.</param>
    /// <param name="externalIpV6">The resolved external IPv6 address.</param>
    /// <returns>The supported IPv4 and IPv6 addresses; an unsupported address is returned as <see langword="null"/>.</returns>
    public static (IPAddress? IPv4, IPAddress? IPv6) SelectAdvertised(
        IPAddress localIp,
        IPAddress? externalIpV4,
        IPAddress? externalIpV6)
        => (
            SupportsFamily(localIp, AddressFamily.InterNetwork) ? externalIpV4 : null,
            SupportsFamily(localIp, AddressFamily.InterNetworkV6) ? externalIpV6 : null);

    /// <summary>
    /// Writes listener-supported address families in preferred, IPv4, then IPv6 order without duplicates.
    /// </summary>
    /// <param name="localIp">The address used to bind the local socket.</param>
    /// <param name="preferredEndpoint">The endpoint whose family should be written first, when supported.</param>
    /// <param name="addressFamilies">The destination span, which must have room for at least two entries.</param>
    /// <returns>The number of families written, at most two.</returns>
    internal static int GetSupportedFamilies(
        IPAddress localIp,
        IPEndPoint? preferredEndpoint,
        Span<AddressFamily> addressFamilies)
    {
        Debug.Assert(addressFamilies.Length >= 2);

        int count = 0;
        AddressFamily? preferredFamily = preferredEndpoint is null
            ? null
            : GetFamily(preferredEndpoint.Address);
        if (preferredFamily is { } family && SupportsFamily(localIp, family))
        {
            addressFamilies[count++] = family;
        }

        if (preferredFamily != AddressFamily.InterNetwork &&
            SupportsFamily(localIp, AddressFamily.InterNetwork))
        {
            addressFamilies[count++] = AddressFamily.InterNetwork;
        }

        if (preferredFamily != AddressFamily.InterNetworkV6 &&
            SupportsFamily(localIp, AddressFamily.InterNetworkV6))
        {
            addressFamilies[count++] = AddressFamily.InterNetworkV6;
        }

        return count;
    }
}
