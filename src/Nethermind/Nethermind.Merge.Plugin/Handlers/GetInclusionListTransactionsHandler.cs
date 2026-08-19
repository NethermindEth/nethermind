// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>
/// EIP-7805 (FOCIL): builds an inclusion list of pending mempool txs, bounded by
/// <see cref="Eip7805Constants.MaxBytesPerInclusionList"/>. Parameterless per execution-apis#609 —
/// the list is drawn from the node's local mempool, not keyed by a block hash.
/// </summary>
public class GetInclusionListTransactionsHandler(
    ITxPool? txPool,
    ISpecProvider specProvider,
    IBlockFinder blockFinder,
    IBlocksConfig blocksConfig) : IHandler<InclusionListBytes>
{
    private readonly InclusionListBuilder? _inclusionListBuilder = txPool is null ? null : new(txPool);

    public ResultWrapper<InclusionListBytes> Handle()
        // Reject out-of-fork calls with -38005 like the other engine endpoints (and the SSZ-REST route,
        // which is fork-gated at routing) rather than returning a mempool list before Bogota.
        => !NextBlockSpec().IsEip7805Enabled
            ? ResultWrapper<InclusionListBytes>.Fail(MergeErrorMessages.UnsupportedFork, MergeErrorCodes.UnsupportedFork)
            : ResultWrapper<InclusionListBytes>.Success(_inclusionListBuilder?.GetInclusionList() ?? new InclusionListBytes(0));

    /// <summary>Spec of the block the list is for, i.e. the one after the current head.</summary>
    /// <remarks>
    /// Gating on the head instead would reject the whole first Bogota slot: the committee builds that
    /// block's list while the head is still the last pre-Bogota block.
    /// </remarks>
    private IReleaseSpec NextBlockSpec()
    {
        BlockHeader? head = blockFinder.FindBestSuggestedHeader();
        return head is null
            ? specProvider.GenesisSpec
            : specProvider.GetSpec(head.Number + 1, head.Timestamp + blocksConfig.SecondsPerSlot);
    }
}
