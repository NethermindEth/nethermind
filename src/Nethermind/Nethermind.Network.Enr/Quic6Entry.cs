// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Enr;

/// <summary>An entry storing the IPv6 libp2p QUIC port.</summary>
public class Quic6Entry(int portNumber) : EnrContentEntry<int>(portNumber)
{
    public override string Key => EnrContentKey.Quic6;

    protected override int GetRlpLengthOfValue() => Rlp.LengthOf(Value);

    protected override void EncodeValue<TWriter>(ref TWriter writer) => writer.Encode(Value);
}
