// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>Builds an inclusion list from pending mempool transactions, bounded by
/// <see cref="Eip7805Constants.MaxBytesPerInclusionList"/> (EIP-7805).</summary>
/// <remarks>Only chains that never schedule EIP-7805 are refused: the list is for the next block, whose
/// timestamp a missed slot moves, so a narrower gate risks refusing the activation slot itself.</remarks>
public class GetInclusionListTransactionsHandler(
    ITxPool? txPool,
    IBlockTree blockTree,
    ISpecProvider specProvider) : IHandler<InclusionListBytes>
{
    private readonly InclusionListBuilder? _inclusionListBuilder = txPool is null ? null : new(txPool, blockTree, specProvider);

    public ResultWrapper<InclusionListBytes> Handle()
        => !specProvider.GetFinalSpec().IsEip7805Enabled
            ? ResultWrapper<InclusionListBytes>.Fail(MergeErrorMessages.UnsupportedFork, MergeErrorCodes.UnsupportedFork)
            : ResultWrapper<InclusionListBytes>.Success(_inclusionListBuilder?.GetInclusionList() ?? new InclusionListBytes(0));
}
