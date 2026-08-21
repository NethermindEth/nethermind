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
            private readonly IPAddress? _externalIpV4Override;
            private readonly IPAddress? _externalIpV6Override;

            public NethermindIp(IPAddress localIp, IPAddress externalIp)
                : this(localIp, externalIp, null, null)
            {
            }

            public NethermindIp(IPAddress localIp, IPAddress externalIp, IPAddress? externalIpV4, IPAddress? externalIpV6)
            {
                LocalIp = localIp;
                ExternalIp = externalIp;
                _externalIpV4Override = externalIpV4;
                _externalIpV6Override = externalIpV6;
            }

            public IPAddress LocalIp { get; init; }
            public IPAddress ExternalIp { get; init; }

            /// <summary>
            /// The external IPv4 address to advertise, derived from the explicit IPv4 override or
            /// <see cref="ExternalIp"/>, so a copied record (<c>with { ExternalIp = ... }</c>) keeps the
            /// family addresses consistent with the primary address.
            /// </summary>
            public IPAddress? ExternalIpV4 => GetExternalIpV4(_externalIpV4Override) ?? GetExternalIpV4(ExternalIp);

            /// <summary>
            /// The external IPv6 address to advertise, derived from the explicit IPv6 override or
            /// <see cref="ExternalIp"/>.
            /// </summary>
            public IPAddress? ExternalIpV6 => GetExternalIpV6(_externalIpV6Override) ?? GetExternalIpV6(ExternalIp);

            /// <summary>
            /// Preserves the deconstruction contract of the previous positional record so plugins that
            /// deconstruct the resolver result keep compiling and running.
            /// </summary>
            public void Deconstruct(out IPAddress localIp, out IPAddress externalIp)
            {
                localIp = LocalIp;
                externalIp = ExternalIp;
            }

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
