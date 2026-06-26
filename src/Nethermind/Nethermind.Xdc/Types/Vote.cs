// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;

namespace Nethermind.Xdc.Types;

public class Vote(BlockRoundInfo proposedBlockInfo, ulong gapNumber, Signature? signature = null, bool isMyVote = false) : RlpHashEqualityBase, IXdcPoolItem
{
    private static readonly VoteDecoder _decoder = new();

    public BlockRoundInfo ProposedBlockInfo { get; set; } = proposedBlockInfo;
    public ulong GapNumber { get; set; } = gapNumber;
    public Signature? Signature { get; set; } = signature;
    public Address? Signer { get; set; }
    public bool IsMyVote { get; } = isMyVote;

    public override string ToString() =>
        $"{ProposedBlockInfo.Round}:{GapNumber}:{ProposedBlockInfo.BlockNumber}";

    public (ulong Round, Hash256 hash) PoolKey()
    {
        KeccakRlpWriter writer = new();
        _decoder.Encode(ref writer, this, RlpBehaviors.ForSealing);
        return (ProposedBlockInfo.Round, writer.GetHash());
    }

    protected override void Encode(ref KeccakRlpWriter writer) =>
        _decoder.Encode(ref writer, this, RlpBehaviors.None);
}
