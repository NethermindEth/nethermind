// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using RlpBehaviors = Nethermind.Serialization.Rlp.RlpBehaviors;

namespace Nethermind.Xdc.Types;

public class Vote(BlockRoundInfo proposedBlockInfo, ulong gapNumber, Signature signature = null) : IXdcPoolItem
{
    private readonly VoteDecoder _decoder = new();
    public BlockRoundInfo ProposedBlockInfo { get; set; } = proposedBlockInfo;
    public ulong GapNumber { get; set; } = gapNumber;
    public Signature? Signature { get; set; } = signature;

    public override string ToString() =>
        $"{ProposedBlockInfo.Round}:{GapNumber}:{ProposedBlockInfo.BlockNumber}";

    public (ulong Round, Hash256 hash) PoolKey() => (ProposedBlockInfo.Round, Keccak.Compute(_decoder.Encode(this, RlpBehaviors.ForSealing).Bytes));
}
