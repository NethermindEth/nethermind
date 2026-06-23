// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;

namespace Nethermind.Network;

/// <summary>
/// IP address classification helpers used by peer and discovery filtering.
/// </summary>
public static class IPAddressExtensions
{
    extension(IPAddress ipAddress)
    {
        /// <summary>
        /// Returns <c>true</c> for loopback, private, link-local, CGNAT, and IPv6 ULA addresses.
        /// </summary>
        public bool IsLoopbackOrPrivateOrLinkLocal
            => ParsedIPAddress.Parse(ipAddress).IsLoopbackOrPrivateOrLinkLocal;

        /// <summary>
        /// Returns <c>true</c> for IPv4 or IPv6 multicast addresses.
        /// </summary>
        public bool IsMulticast
            => ParsedIPAddress.Parse(ipAddress).IsMulticast;

        /// <summary>
        /// Returns <c>true</c> for IPv4 multicast addresses.
        /// </summary>
        public bool IsIPv4Multicast
            => ParsedIPAddress.Parse(ipAddress).IsIPv4Multicast;

        /// <summary>
        /// Returns <c>true</c> for special-use addresses that should not be accepted as routable peers.
        /// </summary>
        /// <remarks>
        /// This intentionally does not include loopback, private, link-local, CGNAT, or IPv6 ULA ranges;
        /// callers that support private deployments can decide whether to accept those separately.
        /// </remarks>
        public bool IsSpecialUseAddress
            => ParsedIPAddress.Parse(ipAddress).IsSpecialUseAddress;
    }
}
