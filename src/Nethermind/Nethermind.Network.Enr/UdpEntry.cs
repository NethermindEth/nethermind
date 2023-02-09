// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Enr;

/// <summary>
/// An entry storing UDP IPv4 port number.
/// </summary>
public class UdpEntry : EnrContentEntry<int>
{
    public UdpEntry(int portNumber) : base(portNumber) { }

    public override string Key => EnrContentKey.Udp;

    protected override int GetRlpLengthOfValue()
    {
        return Rlp.LengthOf(Value);
    }

    protected override void EncodeValue(RlpStream rlpStream)
    {
        rlpStream.Encode(Value);
    }
}
