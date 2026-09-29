// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>
/// The postBatch that settles a span of blocks without cross-chain effects: one immediate anchor entry that moves our
/// rollup from the block the previous settlement ended at to the span's last block, and the span as its DA. The
/// states it names are block hashes, which is what L1 stores.
/// </summary>
public static class AnchorBatch
{
    /// <param name="settled">The block L1's commitment names now: the parent of the span's first block.</param>
    /// <param name="last">The span's last block, which the batch settles.</param>
    /// <param name="span">Every block after <paramref name="settled"/> up to <paramref name="last"/>, as DA carries them.</param>
    /// <param name="proofSystems">The proof systems whose proofs the batch will carry, ascending.</param>
    /// <returns>The batch without proofs: they sign it as it is.</returns>
    public static PostBatch Build(ulong rollupId, in ValueHash256 settled, in ValueHash256 last, IReadOnlyList<DaBlock> span, Address[] proofSystems)
    {
        StateUpdate update = new(rollupId, settled, last, Int256.Int256.Zero);
        ExecutionEntry anchor = new([update], default, [], [], RollingHash.SeedL1(update, default), rollupId, true, []);
        ulong[] indexes = new ulong[proofSystems.Length];
        for (int i = 0; i < indexes.Length; i++)
        {
            indexes[i] = (ulong)i;
        }

        return new PostBatch([], [anchor], [], 1, 0, proofSystems, [new RollupProofSystems(rollupId, indexes)], [],
            DaPayloadCodec.Encode(rollupId, span, []), [], 0, false);
    }

    /// <summary>The span as the batch's DA carries it: each block's beneficiary, extra data and every transaction.</summary>
    public static DaBlock[] Da(IReadOnlyList<Block> span)
    {
        DaBlock[] da = new DaBlock[span.Count];
        for (int i = 0; i < da.Length; i++)
        {
            Block block = span[i];
            byte[][] transactions = new byte[block.Transactions.Length][];
            for (int j = 0; j < transactions.Length; j++)
            {
                transactions[j] = TxDecoder.Instance.Encode(block.Transactions[j], RlpBehaviors.SkipTypedWrapping).Bytes;
            }

            da[i] = new DaBlock(block.Beneficiary!, block.Header.ExtraData, transactions);
        }

        return da;
    }
}
