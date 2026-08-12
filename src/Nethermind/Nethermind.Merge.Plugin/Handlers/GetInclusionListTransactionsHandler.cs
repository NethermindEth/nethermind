// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
public class GetInclusionListTransactionsHandler(ITxPool? txPool, IChainHeadSpecProvider chainHeadSpecProvider) : IHandler<InclusionListBytes>
{
    private readonly InclusionListBuilder? _inclusionListBuilder = txPool is null ? null : new(txPool);

    public ResultWrapper<InclusionListBytes> Handle()
        // Reject out-of-fork calls with -38005 like the other engine endpoints (and the SSZ-REST route,
        // which is fork-gated at routing) rather than returning a mempool list before Bogota.
        => !chainHeadSpecProvider.GetCurrentHeadSpec().IsEip7805Enabled
            ? ResultWrapper<InclusionListBytes>.Fail(MergeErrorMessages.UnsupportedFork, MergeErrorCodes.UnsupportedFork)
            : ResultWrapper<InclusionListBytes>.Success(_inclusionListBuilder?.GetInclusionList() ?? new InclusionListBytes(0));
}
