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
                ExternalIpV4 = NormalizeExternalIp(externalIpV4, AddressFamily.InterNetwork)
                    ?? NormalizeExternalIp(externalIp, AddressFamily.InterNetwork);
                ExternalIpV6 = NormalizeExternalIp(externalIpV6, AddressFamily.InterNetworkV6)
                    ?? NormalizeExternalIp(externalIp, AddressFamily.InterNetworkV6);
            }

            public IPAddress LocalIp { get; init; }
            public IPAddress ExternalIp { get; init; }
            public IPAddress? ExternalIpV4 { get; init; }
            public IPAddress? ExternalIpV6 { get; init; }

            internal static IPAddress? NormalizeExternalIp(IPAddress? ipAddress, AddressFamily? expectedFamily)
            {
                if (ipAddress is null || IsUnspecified(ipAddress))
                {
                    return null;
                }

                return expectedFamily switch
                {
                    AddressFamily.InterNetwork => ipAddress.AddressFamily switch
                    {
                        AddressFamily.InterNetwork => ipAddress,
                        AddressFamily.InterNetworkV6 when ipAddress.IsIPv4MappedToIPv6 => ipAddress.MapToIPv4(),
                        _ => null
                    },
                    AddressFamily.InterNetworkV6 => ipAddress.AddressFamily == AddressFamily.InterNetworkV6 && !ipAddress.IsIPv4MappedToIPv6
                        ? ipAddress
                        : null,
                    null => ipAddress,
                    _ => null
                };
            }

            internal static bool IsUnspecified(IPAddress ipAddress)
                => ipAddress.Equals(IPAddress.Any)
                   || ipAddress.Equals(IPAddress.IPv6Any)
                   || ipAddress.Equals(IPAddress.None)
                   || ipAddress.Equals(IPAddress.IPv6None);
        }
    }
}
