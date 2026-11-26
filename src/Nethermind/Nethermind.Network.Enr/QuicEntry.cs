// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Enr;

/// <summary>An entry storing the IPv4 libp2p QUIC port.</summary>
public class QuicEntry(int portNumber) : EnrContentEntry<int>(portNumber)
{
    public override string Key => EnrContentKey.Quic;

    protected override int GetRlpLengthOfValue() => Rlp.LengthOf(Value);

    protected override void EncodeValue(ref RlpWriter writer) => writer.Encode(Value);
}
