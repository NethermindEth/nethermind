// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Enr;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.BeaconChain.P2P.Discovery;

/// <summary>The altair <c>syncnets</c> ENR entry: the SSZ <c>Bitvector[SYNC_COMMITTEE_SUBNET_COUNT]</c> of <c>MetaData.syncnets</c> as an RLP byte string.</summary>
/// <remarks>Shaped like <see cref="AttnetsEntry"/>; published by <see cref="BeaconNodeRecordProvider"/> while any subnet bit is set.</remarks>
internal sealed class SyncnetsEntry(byte[] syncnets) : EnrContentEntry<byte[]>(syncnets)
{
    public override string Key => "syncnets";
    protected override int GetRlpLengthOfValue() => Rlp.LengthOf(Value);
    protected override void EncodeValue(ref RlpWriter writer) => writer.Encode(Value);
}
