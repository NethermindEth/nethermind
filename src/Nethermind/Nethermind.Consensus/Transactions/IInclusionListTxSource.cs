// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Transactions;

/// <summary>Feeds the inclusion list supplied by <c>engine_forkchoiceUpdatedV5</c> into block production (EIP-7805).</summary>
public interface IInclusionListTxSource : ITxSource
{
    /// <summary>Retains the list for the build identified by the <paramref name="inclusionListTransactions"/>
    /// array instance, which is the key <c>GetTransactions</c> looks it up by.</summary>
    void Set(byte[][] inclusionListTransactions, IReleaseSpec spec);

    /// <summary>Supplies optional proof metadata to proof-aware sources.</summary>
    void Set(byte[][] inclusionListTransactions, IReleaseSpec spec, RecursiveStark? proof, byte[]? provenDependencies = null)
        => Set(inclusionListTransactions, spec);

    /// <summary>Attaches inclusion-list obligations to the produced block.</summary>
    void ApplyInclusionList(BlockToProduce block, PayloadAttributes? attributes)
    {
        block.InclusionListTransactions = attributes?.InclusionListTransactions is { } il
            ? TxsDecoder.DecodeTxs(il, skipErrors: true).Transactions : null;
        block.InclusionListRecursiveStark = attributes?.InclusionListRecursiveStark;
        block.InclusionListProvenDependencies = attributes?.InclusionListProvenDependencies;
        block.InclusionListProofInput = null;
    }
}
