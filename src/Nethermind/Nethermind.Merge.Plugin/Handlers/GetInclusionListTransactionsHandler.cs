// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>
/// EIP-7805 (FOCIL): builds an inclusion list of pending mempool txs, bounded by
/// <see cref="Eip7805Constants.MaxBytesPerInclusionList"/>.
/// </summary>
public class GetInclusionListTransactionsHandler(ITxPool? txPool, IBlockFinder? blockFinder)
    : IHandler<Hash256, InclusionListBytes>
{
    private readonly InclusionListBuilder? _inclusionListBuilder = txPool is null ? null : new(txPool);

    public ResultWrapper<InclusionListBytes> Handle(Hash256 blockHash)
    {
        if (_inclusionListBuilder is null || blockFinder is null)
        {
            return ResultWrapper<InclusionListBytes>.Success(new InclusionListBytes(0));
        }

        if (blockFinder.FindHeader(blockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded) is null)
        {
            return ResultWrapper<InclusionListBytes>.Fail($"unknown parent block {blockHash}", MergeErrorCodes.UnknownParent);
        }

        return ResultWrapper<InclusionListBytes>.Success(_inclusionListBuilder.GetInclusionList());
    }
}
