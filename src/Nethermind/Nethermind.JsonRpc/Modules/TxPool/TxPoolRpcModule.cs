// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Find;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Facade.Eth;
using Nethermind.TxPool;

namespace Nethermind.JsonRpc.Modules.TxPool;

public class TxPoolRpcModule(ITxPoolInfoProvider txPoolInfoProvider, ISpecProvider specProvider, IBlockFinder blockFinder, IBlocksConfig blocksConfig)
    : ITxPoolRpcModule
{
    public ResultWrapper<TxPoolStatus> txpool_status()
    {
        TxPoolCounts counts = txPoolInfoProvider.GetCounts();
        TxPoolStatus poolStatus = new(counts);

        return ResultWrapper<TxPoolStatus>.Success(poolStatus);
    }

    public ResultWrapper<TxPoolContent> txpool_content()
    {
        TxPoolInfo poolInfo = txPoolInfoProvider.GetInfo();
        return ResultWrapper<TxPoolContent>.Success(new TxPoolContent(poolInfo, PendingContext()));
    }

    public ResultWrapper<TxPoolContentFrom> txpool_contentFrom(Address address)
    {
        ArgumentNullException.ThrowIfNull(address);
        TxPoolSenderInfo senderInfo = txPoolInfoProvider.GetSenderInfo(address);
        return ResultWrapper<TxPoolContentFrom>.Success(new TxPoolContentFrom(senderInfo, PendingContext()));
    }

    public ResultWrapper<TxPoolInspection> txpool_inspect()
    {
        TxPoolInfo poolInfo = txPoolInfoProvider.GetInfo();
        return ResultWrapper<TxPoolInspection>.Success(new TxPoolInspection(poolInfo, PendingContext().Spec));
    }

    private TransactionForRpcContext PendingContext() =>
        TransactionForRpcContext.ForPending(specProvider, blockFinder.Head?.Header, blocksConfig.SecondsPerSlot);
}
