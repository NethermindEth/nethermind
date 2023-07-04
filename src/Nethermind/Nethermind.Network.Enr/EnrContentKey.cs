// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Enr
{
    /// <summary>
    /// A string identifying an ENR entry type.
    /// </summary>
    public static class EnrContentKey
    {
        /// <summary>
        /// ETH info
        /// </summary>
        public const string Eth = "eth";

        /// <summary>
        /// Name of identity scheme, e.g. "v4"
        /// </summary>
        public const string Id = "id";

        /// <summary>
        /// IPv4 address, 4 bytes
        /// </summary>
        public const string Ip = "ip";

        /// <summary>
        /// IPv6 address, 16 bytes
        /// </summary>
        public const string Ip6 = "ip6";

        /// <summary>
        /// Compressed secp256k1 public key, 33 bytes
        /// </summary>
        public const string Secp256K1 = "secp256k1";

        /// <summary>
        /// TCP port, big endian integer
        /// </summary>
        public const string Tcp = "tcp";

        /// <summary>
        /// IPv6-specific TCP port, big endian integer
        /// </summary>
        public const string Tcp6 = "tcp6";

        /// <summary>
        /// UDP port, big endian integer
        /// </summary>
        public const string Udp = "udp";

        /// <summary>
        /// IPv6-specific UDP port, big endian integer
        /// </summary>
        public const string Udp6 = "udp6";
    }
}
