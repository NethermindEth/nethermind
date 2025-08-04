// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.Core.Collections;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin.Handlers;

public class GetInclusionListTransactionsHandler(ITxPool? txPool)
    : IHandler<ArrayPoolList<byte[]>>
{
    private readonly InclusionListBuilder? inclusionListBuilder = txPool is null ? null : new(txPool);

    public ResultWrapper<ArrayPoolList<byte[]>> Handle()
    {
        if (txPool is null)
        {
            return ResultWrapper<ArrayPoolList<byte[]>>.Success(ArrayPoolList<byte[]>.Empty());
        }

        ArrayPoolList<byte[]> txBytes = new(Eip7805Constants.MaxTransactionsPerInclusionList, inclusionListBuilder!.GetInclusionList());
        return ResultWrapper<ArrayPoolList<byte[]>>.Success(txBytes);
    }

}
