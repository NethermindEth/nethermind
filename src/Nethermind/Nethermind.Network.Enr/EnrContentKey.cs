// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
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
        public static ReadOnlySpan<byte> EthU8 => "eth"u8;

        /// <summary>
        /// Name of identity scheme, e.g. "v4"
        /// </summary>
        public const string Id = "id";
        public static ReadOnlySpan<byte> IdU8 => "id"u8;

        /// <summary>
        /// IPv4 address, 4 bytes
        /// </summary>
        public const string Ip = "ip";
        public static ReadOnlySpan<byte> IpU8 => "ip"u8;

        /// <summary>
        /// IPv6 address, 16 bytes
        /// </summary>
        public const string Ip6 = "ip6";
        public static ReadOnlySpan<byte> Ip6U8 => "ip6"u8;

        /// <summary>
        /// Compressed secp256k1 public key, 33 bytes
        /// </summary>
        public const string Secp256K1 = "secp256k1";
        public static ReadOnlySpan<byte> Secp256K1U8 => "secp256k1"u8;

        /// <summary>
        /// TCP port, big endian integer
        /// </summary>
        public const string Tcp = "tcp";
        public static ReadOnlySpan<byte> TcpU8 => "tcp"u8;

        /// <summary>
        /// IPv6-specific TCP port, big endian integer
        /// </summary>
        public const string Tcp6 = "tcp6";
        public static ReadOnlySpan<byte> Tcp6U8 => "tcp6"u8;

        /// <summary>
        /// UDP port, big endian integer
        /// </summary>
        public const string Udp = "udp";
        public static ReadOnlySpan<byte> UdpU8 => "udp"u8;

        /// <summary>
        /// IPv6-specific UDP port, big endian integer
        /// </summary>
        public const string Udp6 = "udp6";
        public static ReadOnlySpan<byte> Udp6U8 => "udp6"u8;
    }
}
