// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
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
        /// honored when set, otherwise the address is auto-detected. The IPv4/IPv6 addresses to
        /// advertise are derived from <c>ExternalIp</c> unless <c>ExternalIpV4</c>/<c>ExternalIpV6</c>
        /// overrides are set.
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
        /// Equality compares the resolved addresses, regardless of whether a family address came from
        /// an explicit override or was derived from <see cref="ExternalIp"/>.
        /// </remarks>
        public readonly record struct NethermindIp
        {
            /// <summary>
            /// Creates resolved node addresses, deriving the family-specific address from
            /// <paramref name="ExternalIp"/>.
            /// </summary>
            /// <param name="LocalIp">The local address used for network listeners.</param>
            /// <param name="ExternalIp">The primary external address used by existing consumers.</param>
            public NethermindIp(IPAddress LocalIp, IPAddress ExternalIp)
                : this(LocalIp, ExternalIp, null, null)
            {
            }

            /// <summary>
            /// Creates resolved node addresses with optional family-specific advertisement overrides.
            /// </summary>
            /// <param name="localIp">The local address used for network listeners.</param>
            /// <param name="externalIp">The primary external address used by existing consumers.</param>
            /// <param name="externalIpV4">The optional IPv4 advertisement override.</param>
            /// <param name="externalIpV6">The optional IPv6 advertisement override.</param>
            public NethermindIp(IPAddress localIp, IPAddress externalIp, IPAddress? externalIpV4, IPAddress? externalIpV6)
            {
                LocalIp = localIp;
                ExternalIp = externalIp;
                ExternalIpV4 = GetExternalIpV4(externalIpV4) ?? GetExternalIpV4(externalIp);
                ExternalIpV6 = GetExternalIpV6(externalIpV6) ?? GetExternalIpV6(externalIp);
            }

            /// <summary>
            /// Gets the local address used for network listeners.
            /// </summary>
            public IPAddress LocalIp { get; }

            /// <summary>
            /// Gets the primary external address used by existing consumers.
            /// </summary>
            public IPAddress ExternalIp { get; }

            /// <summary>
            /// Gets the external IPv4 address to advertise. An explicit IPv4 override takes precedence;
            /// otherwise the value is derived from <see cref="ExternalIp"/>.
            /// </summary>
            public IPAddress? ExternalIpV4 { get; }

            /// <summary>
            /// Gets the external IPv6 address to advertise. An explicit IPv6 override takes precedence;
            /// otherwise the value is derived from <see cref="ExternalIp"/>.
            /// </summary>
            public IPAddress? ExternalIpV6 { get; }

            /// <summary>
            /// Preserves the deconstruction contract of the previous positional record so plugins that
            /// deconstruct the resolver result keep compiling and running.
            /// </summary>
            /// <param name="LocalIp">The resolved local address.</param>
            /// <param name="ExternalIp">The resolved primary external address.</param>
            public void Deconstruct(out IPAddress LocalIp, out IPAddress ExternalIp)
            {
                LocalIp = this.LocalIp;
                ExternalIp = this.ExternalIp;
            }

            internal static IPAddress? GetExternalIpV4(IPAddress? ipAddress)
            {
                if (ipAddress is null)
                {
                    return null;
                }

                // Map first so a mapped unspecified value (::ffff:0.0.0.0) is rejected like its
                // native IPv4 equivalent.
                if (ipAddress.IsIPv4MappedToIPv6)
                {
                    ipAddress = ipAddress.MapToIPv4();
                }

                return !IsUnspecified(ipAddress) && ipAddress.AddressFamily == AddressFamily.InterNetwork
                    ? ipAddress
                    : null;
            }

            internal static IPAddress? GetExternalIpV6(IPAddress? ipAddress)
                => ipAddress is not null
                   && !IsUnspecified(ipAddress)
                   && ipAddress.AddressFamily == AddressFamily.InterNetworkV6
                   && !ipAddress.IsIPv4MappedToIPv6
                    ? ipAddress
                    : null;

            internal static bool IsUnspecified(IPAddress ipAddress)
                => ipAddress.Equals(IPAddress.Any)
                   || ipAddress.Equals(IPAddress.None)
                   || ipAddress.Equals(IPAddress.IPv6Any);
        }
    }
}
