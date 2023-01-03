// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Enr;

/// <summary>
/// An entry storing TCP IPv4 port number.
/// </summary>
public class TcpEntry : EnrContentEntry<int>
{
    public TcpEntry(int portNumber) : base(portNumber) { }

    public override string Key => EnrContentKey.Tcp;

    protected override int GetRlpLengthOfValue()
    {
        return Rlp.LengthOf(Value);
    }

    protected override void EncodeValue(RlpStream rlpStream)
    {
        rlpStream.Encode(Value);
    }
}
