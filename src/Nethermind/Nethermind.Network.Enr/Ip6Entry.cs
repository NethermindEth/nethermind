// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Enr;

/// <summary>
/// An entry storing the IPv6 address of the node.
/// </summary>
public class Ip6Entry(IPAddress ipAddress) : EnrContentEntry<IPAddress>(ipAddress)
{
    public override string Key => EnrContentKey.Ip6;

    protected override int GetRlpLengthOfValue() => 17;

    protected override void EncodeValue(RlpStream rlpStream)
    {
        Span<byte> bytes = stackalloc byte[16];
        Value.MapToIPv6().TryWriteBytes(bytes, out int _);
        rlpStream.Encode(bytes);
    }
}
