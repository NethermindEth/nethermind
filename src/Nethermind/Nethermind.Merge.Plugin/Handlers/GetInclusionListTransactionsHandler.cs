// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>
/// EIP-7805 (FOCIL): builds an inclusion list of pending mempool transactions for the
/// given <c>blockHash</c>. The CL aggregates ILs from committee members. The IL is
/// bounded by <see cref="Eip7805Constants.MaxBytesPerInclusionList"/> RLP bytes and
/// <see cref="Eip7805Constants.MaxTransactionsPerInclusionList"/> transactions.
/// </summary>
/// <remarks>
/// Selection strategy is implementation-defined per spec. The current
/// <see cref="InclusionListBuilder"/> drains the local pool by descending fee priority.
/// The <c>blockHash</c> parameter is required by
/// <see href="https://github.com/ethereum/execution-apis/pull/609">execution-apis#609</see>
/// but is not consulted today — the IL is built from the current mempool snapshot rather
/// than a hypothetical post-<c>blockHash</c> state. If a future committee policy needs to
/// gate IL contents on a specific head, the hash flows through ready to use.
/// </remarks>
public class GetInclusionListTransactionsHandler(ITxPool? txPool)
    : IHandler<Hash256, ArrayPoolList<byte[]>>
{
    private readonly InclusionListBuilder? _inclusionListBuilder = txPool is null ? null : new(txPool);

    public ResultWrapper<ArrayPoolList<byte[]>> Handle(Hash256 blockHash)
    {
        if (_inclusionListBuilder is null)
        {
            return ResultWrapper<ArrayPoolList<byte[]>>.Success(ArrayPoolList<byte[]>.Empty());
        }

        ArrayPoolList<byte[]> txBytes = new(Eip7805Constants.MaxTransactionsPerInclusionList, _inclusionListBuilder.GetInclusionList());
        return ResultWrapper<ArrayPoolList<byte[]>>.Success(txBytes);
    }
}
