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
/// <remarks>
/// Only chains that never schedule FOCIL are refused. The list is for the next block, whose timestamp the
/// node cannot derive from the head — a missed slot moves it — so any narrower gate risks refusing the
/// activation slot itself. This matches how the method and its SSZ-REST route are advertised.
/// </remarks>
public class GetInclusionListTransactionsHandler(
    ITxPool? txPool,
    ISpecProvider specProvider) : IHandler<InclusionListBytes>
{
    private readonly InclusionListBuilder? _inclusionListBuilder = txPool is null ? null : new(txPool);

    public ResultWrapper<InclusionListBytes> Handle()
        => !specProvider.GetFinalSpec().IsEip7805Enabled
            ? ResultWrapper<InclusionListBytes>.Fail(MergeErrorMessages.UnsupportedFork, MergeErrorCodes.UnsupportedFork)
            : ResultWrapper<InclusionListBytes>.Success(_inclusionListBuilder?.GetInclusionList() ?? new InclusionListBytes(0));
}
