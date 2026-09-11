// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
        /// resolution. Explicit local, primary, IPv4, and IPv6 overrides are honored when set;
        /// otherwise the primary address is auto-detected.
        /// </remarks>
        /// <param name="cancellationToken">
        /// Cancels only the caller's wait for the result, not the shared cached resolution (which always
        /// runs to completion so it can still serve other callers).
        /// </param>
        ValueTask<NethermindIp> Resolve(CancellationToken cancellationToken = default);

        /// <summary>
        /// The resolved local and external IP addresses of this node.
        /// </summary>
        /// <remarks>
        /// Equality compares the resolved addresses, regardless of whether a family-specific address
        /// came from an explicit override or was derived from <see cref="ExternalIp"/>.
        /// </remarks>
        /// <param name="LocalIp">The local address used for network listeners.</param>
        /// <param name="ExternalIp">The primary external address used by existing consumers.</param>
        public readonly record struct NethermindIp(IPAddress LocalIp, IPAddress ExternalIp)
        {
            private readonly IPAddress? _externalIpV4Override;
            private readonly IPAddress? _externalIpV6Override;

            /// <summary>
            /// Creates resolved node addresses with optional family-specific advertisement overrides.
            /// </summary>
            /// <param name="localIp">The local address used for network listeners.</param>
            /// <param name="externalIp">The primary external address used by existing consumers.</param>
            /// <param name="externalIpV4">The optional IPv4 advertisement override.</param>
            /// <param name="externalIpV6">The optional IPv6 advertisement override.</param>
            public NethermindIp(IPAddress localIp, IPAddress externalIp, IPAddress? externalIpV4, IPAddress? externalIpV6)
                : this(localIp, externalIp)
            {
                _externalIpV4Override = NormalizeExternalIp(externalIpV4, AddressFamily.InterNetwork);
                _externalIpV6Override = NormalizeExternalIp(externalIpV6, AddressFamily.InterNetworkV6);
            }

            /// <summary>
            /// Gets the external IPv4 address to advertise. An explicit IPv4 override takes precedence;
            /// otherwise the value is derived from <see cref="ExternalIp"/>.
            /// </summary>
            public IPAddress? ExternalIpV4 => _externalIpV4Override ?? NormalizeExternalIp(ExternalIp, AddressFamily.InterNetwork);

            /// <summary>
            /// Gets the external IPv6 address to advertise. An explicit IPv6 override takes precedence;
            /// otherwise the value is derived from <see cref="ExternalIp"/>.
            /// </summary>
            public IPAddress? ExternalIpV6 => _externalIpV6Override ?? NormalizeExternalIp(ExternalIp, AddressFamily.InterNetworkV6);

            public bool Equals(NethermindIp other) =>
                LocalIp.Equals(other.LocalIp) &&
                ExternalIp.Equals(other.ExternalIp) &&
                Equals(ExternalIpV4, other.ExternalIpV4) &&
                Equals(ExternalIpV6, other.ExternalIpV6);

            public override int GetHashCode() => HashCode.Combine(LocalIp, ExternalIp, ExternalIpV4, ExternalIpV6);

            internal static IPAddress? NormalizeExternalIp(IPAddress? ipAddress, AddressFamily? expectedFamily)
            {
                if (ipAddress is null)
                {
                    return null;
                }

                if (ipAddress.IsIPv4MappedToIPv6 && expectedFamily != AddressFamily.InterNetworkV6)
                {
                    ipAddress = ipAddress.MapToIPv4();
                }

                if (ipAddress.IsWildcardOrNone)
                {
                    return null;
                }

                return expectedFamily switch
                {
                    AddressFamily.InterNetwork => ipAddress.AddressFamily == AddressFamily.InterNetwork ? ipAddress : null,
                    AddressFamily.InterNetworkV6 => ipAddress.AddressFamily == AddressFamily.InterNetworkV6 && !ipAddress.IsIPv4MappedToIPv6
                        ? ipAddress
                        : null,
                    null => ipAddress,
                    _ => null
                };
            }
        }
    }
}
