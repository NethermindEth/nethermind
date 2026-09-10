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
        /// <summary>Raised after a cached resolution refresh changes an address.</summary>
        event EventHandler? Changed;

        /// <summary>
        /// Resolves the node's local and external IP addresses.
        /// </summary>
        /// <remarks>
        /// Results containing automatically detected addresses are cached for five minutes; fully configured
        /// results do not expire. Concurrent callers await the same in-flight resolution. Explicit local,
        /// primary, IPv4, and IPv6 overrides are honored when set; otherwise missing external IPv4 and IPv6
        /// addresses are auto-detected independently. Periodic refresh lets ENR publication replace an
        /// automatically detected address when the host's public address changes.
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
            private readonly IPAddress? _externalIpV4;
            private readonly IPAddress? _externalIpV6;

            /// <summary>
            /// Creates resolved node addresses with optional family-specific external addresses.
            /// </summary>
            /// <param name="localIp">The local address used for network listeners.</param>
            /// <param name="externalIp">The primary external address used by existing consumers.</param>
            /// <param name="externalIpV4">The optional resolved IPv4 address.</param>
            /// <param name="externalIpV6">The optional resolved IPv6 address.</param>
            public NethermindIp(IPAddress localIp, IPAddress externalIp, IPAddress? externalIpV4, IPAddress? externalIpV6)
                : this(localIp, externalIp)
            {
                _externalIpV4 = NormalizeExternalIp(externalIpV4, AddressFamily.InterNetwork);
                _externalIpV6 = NormalizeExternalIp(externalIpV6, AddressFamily.InterNetworkV6);
            }

            /// <summary>
            /// Gets the resolved external IPv4 address. A family-specific value takes precedence;
            /// otherwise the value is derived from <see cref="ExternalIp"/>.
            /// </summary>
            public IPAddress? ExternalIpV4 => _externalIpV4 ?? NormalizeExternalIp(ExternalIp, AddressFamily.InterNetwork);

            /// <summary>
            /// Gets the resolved external IPv6 address. A family-specific value takes precedence;
            /// otherwise the value is derived from <see cref="ExternalIp"/>.
            /// </summary>
            public IPAddress? ExternalIpV6 => _externalIpV6 ?? NormalizeExternalIp(ExternalIp, AddressFamily.InterNetworkV6);

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
