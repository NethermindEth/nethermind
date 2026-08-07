// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Network
{
    public interface IIPResolver
    {
        /// <summary>
        /// Resolves the node's local and external IP addresses.
        /// </summary>
        /// <remarks>
        /// The result is resolved once and cached; concurrent callers await the same in-flight
        /// resolution. An explicit <c>INetworkConfig.LocalIp</c>/<c>ExternalIp</c> override is
        /// honored when set, otherwise the address is auto-detected.
        /// </remarks>
        /// <param name="cancellationToken">
        /// Cancels only the caller's wait for the result, not the shared cached resolution (which always
        /// runs to completion so it can still serve other callers).
        /// </param>
        ValueTask<NethermindIp> Resolve(CancellationToken cancellationToken = default);

        /// <summary>
        /// The resolved local and external IP addresses of this node.
        /// </summary>
        public readonly record struct NethermindIp
        {
            public NethermindIp(IPAddress localIp, IPAddress externalIp)
                : this(localIp, externalIp, null, null)
            {
            }

            public NethermindIp(IPAddress localIp, IPAddress externalIp, IPAddress? externalIpV4, IPAddress? externalIpV6)
            {
                LocalIp = localIp;
                ExternalIp = externalIp;
                ExternalIpV4 = GetExternalIpV4(externalIpV4) ?? GetExternalIpV4(externalIp);
                ExternalIpV6 = GetExternalIpV6(externalIpV6) ?? GetExternalIpV6(externalIp);
            }

            public IPAddress LocalIp { get; init; }
            public IPAddress ExternalIp { get; init; }
            public IPAddress? ExternalIpV4 { get; init; }
            public IPAddress? ExternalIpV6 { get; init; }

            private static IPAddress? GetExternalIpV4(IPAddress? ipAddress)
                => ipAddress is null || IsUnspecified(ipAddress)
                    ? null
                    : ipAddress.AddressFamily switch
                    {
                        AddressFamily.InterNetwork => ipAddress,
                        AddressFamily.InterNetworkV6 when ipAddress.IsIPv4MappedToIPv6 => ipAddress.MapToIPv4(),
                        _ => null
                    };

            private static IPAddress? GetExternalIpV6(IPAddress? ipAddress)
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
    }
}
