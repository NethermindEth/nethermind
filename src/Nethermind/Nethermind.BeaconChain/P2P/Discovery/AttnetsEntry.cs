// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Enr;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.BeaconChain.P2P.Discovery;

/// <summary>The phase0 <c>attnets</c> ENR entry: the SSZ <c>Bitvector[ATTESTATION_SUBNET_COUNT]</c> of <c>MetaData.attnets</c> as an RLP byte string.</summary>
/// <remarks>Shaped like <see cref="Eth2Entry"/>; published by <see cref="BeaconNodeRecordProvider"/> while any subnet bit is set.</remarks>
internal sealed class AttnetsEntry(byte[] attnets) : EnrContentEntry<byte[]>(attnets)
{
    public override string Key => "attnets";
    protected override int GetRlpLengthOfValue() => Rlp.LengthOf(Value);
    protected override void EncodeValue(ref RlpWriter writer) => writer.Encode(Value);
}
