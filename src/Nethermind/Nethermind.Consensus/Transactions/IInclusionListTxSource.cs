// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus.Transactions;

/// <summary>Feeds the inclusion lists supplied by <c>engine_forkchoiceUpdatedV5</c> or <c>V6</c> into block production (EIP-7805).</summary>
public interface IInclusionListTxSource : ITxSource
{
    /// <summary>Retains the list for the build identified by the <paramref name="inclusionListTransactions"/>
    /// array instance, which is the key <c>GetTransactions</c> looks it up by.</summary>
    void Set(byte[][] inclusionListTransactions, IReleaseSpec spec);

    /// <summary>Supplies the counted lists and their EIP-8288 commitments to proof-aware sources.</summary>
    /// <param name="inclusionListTransactions">The lists flattened, identifying the build as in <see cref="Set(byte[][], IReleaseSpec)"/>.</param>
    /// <param name="lists">The counted lists, or <c>null</c> for a flattened list alone.</param>
    void Set(byte[][] inclusionListTransactions, IReleaseSpec spec, IReadOnlyList<InclusionListV1>? lists)
        => Set(inclusionListTransactions, spec);

    /// <summary>Attaches inclusion-list obligations to the produced block.</summary>
    void ApplyInclusionList(BlockToProduce block, PayloadAttributes? attributes)
        => InclusionListV1.ApplyToBlock(block, attributes);
}
