// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Xdc.Types;

public class Vote(BlockRoundInfo proposedBlockInfo, ulong gapNumber, Signature signature = null, bool isMyVote = false) : RlpHashEqualityBase, IXdcPoolItem
{
    private static readonly VoteDecoder _decoder = new();

    public BlockRoundInfo ProposedBlockInfo { get; set; } = proposedBlockInfo;
    public ulong GapNumber { get; set; } = gapNumber;
    public Signature? Signature { get; set; } = signature;
    public Address? Signer { get; set; }
    public bool IsMyVote { get; } = isMyVote;

    public override string ToString() =>
        $"{ProposedBlockInfo.Round}:{GapNumber}:{ProposedBlockInfo.BlockNumber}";

    public (ulong Round, Hash256 hash) PoolKey() => (ProposedBlockInfo.Round, Keccak.Compute(_decoder.Encode(this, RlpBehaviors.ForSealing).Bytes));

    protected override void Encode(KeccakRlpStream stream) =>
        _decoder.Encode(stream, this, RlpBehaviors.None);
}
