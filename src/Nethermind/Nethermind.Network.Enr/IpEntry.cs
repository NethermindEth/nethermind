// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Data;
using System.Net;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Enr;

/// <summary>
/// An entry storing the IP address of the node.
/// </summary>
public class IpEntry : EnrContentEntry<IPAddress>
{
    public IpEntry(IPAddress ipAddress) : base(ipAddress) { }

    public override string Key => EnrContentKey.Ip;

    protected override int GetRlpLengthOfValue()
    {
        return 5;
    }

    protected override void EncodeValue(RlpStream rlpStream)
    {
        Span<byte> bytes = stackalloc byte[4];
        Value.MapToIPv4().TryWriteBytes(bytes, out int _);
        rlpStream.Encode(bytes);
    }
}
