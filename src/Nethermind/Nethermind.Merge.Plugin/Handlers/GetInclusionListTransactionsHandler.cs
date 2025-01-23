// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.TxPool;
using Nethermind.JsonRpc;
using Nethermind.Serialization.Rlp;
using System.Linq;

namespace Nethermind.Merge.Plugin.Handlers;

public class GetInclusionListTransactionsHandler(
    ITxPool txPool)
    : IHandler<byte[][]>
{
    public ResultWrapper<byte[][]> Handle()
    {
        Transaction[] txs = txPool.GetPendingTransactions();
        byte[][] txBytes = [.. txs.Select(tx => Rlp.Encode(tx).Bytes)];
        return ResultWrapper<byte[][]>.Success(txBytes);
    }
}
