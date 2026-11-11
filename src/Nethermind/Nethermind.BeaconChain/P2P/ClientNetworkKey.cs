// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Nethermind.BeaconChain.P2P;

internal static class ClientNetworkKey
{
    /// <summary>One partition per IPv4 address and per IPv6 /64, so a client cannot mint fresh budgets by rotating the addresses of one IPv6 subnet.</summary>
    internal static UInt128 Create(IPAddress? address)
    {
        if (address is null) return UInt128.MaxValue;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out int written);
        // An IPv6 key's low half is above every IPv4 key, so the two families never share a partition.
        return address.AddressFamily == AddressFamily.InterNetworkV6
            ? new UInt128(BinaryPrimitives.ReadUInt64BigEndian(bytes), 1UL << 32)
            : BinaryPrimitives.ReadUInt32BigEndian(bytes[..written]);
    }
}
