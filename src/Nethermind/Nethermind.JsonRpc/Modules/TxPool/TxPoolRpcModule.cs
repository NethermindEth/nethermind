// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.TxPool;

namespace Nethermind.JsonRpc.Modules.TxPool
{
    public class TxPoolRpcModule(ITxPoolInfoProvider txPoolInfoProvider, ISpecProvider specProvider)
        : ITxPoolRpcModule
    {
        public ResultWrapper<TxPoolStatus> txpool_status()
        {
            TxPoolInfo poolInfo = txPoolInfoProvider.GetInfo();
            TxPoolStatus poolStatus = new(poolInfo);

            return ResultWrapper<TxPoolStatus>.Success(poolStatus);
        }

        public ResultWrapper<TxPoolContent> txpool_content()
        {
            TxPoolInfo poolInfo = txPoolInfoProvider.GetInfo();
            ulong chainId = specProvider.ChainId;
            return ResultWrapper<TxPoolContent>.Success(new TxPoolContent(poolInfo, chainId));
        }

        public ResultWrapper<TxPoolInspection> txpool_inspect()
        {
            TxPoolInfo poolInfo = txPoolInfoProvider.GetInfo();
            return ResultWrapper<TxPoolInspection>.Success(new TxPoolInspection(poolInfo));
        }
    }
}
