// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Enr;

/// <summary>
/// An entry storing TCP IPv6 port number.
/// </summary>
public class Tcp6Entry(int portNumber) : EnrContentEntry<int>(portNumber)
{
    public override string Key => EnrContentKey.Tcp6;

    protected override int GetRlpLengthOfValue() => Rlp.LengthOf(Value);

    protected override void EncodeValue(RlpStream rlpStream) => rlpStream.Encode(Value);
}
