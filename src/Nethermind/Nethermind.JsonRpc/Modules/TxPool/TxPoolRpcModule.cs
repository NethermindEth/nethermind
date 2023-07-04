// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Find;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.JsonRpc.Modules.TxPool
{
    public class TxPoolRpcModule : ITxPoolRpcModule
    {
        private readonly ITxPoolInfoProvider _txPoolInfoProvider;

        public TxPoolRpcModule(ITxPoolInfoProvider txPoolInfoProvider, ILogManager logManager)
        {
            _txPoolInfoProvider = txPoolInfoProvider ?? throw new ArgumentNullException(nameof(txPoolInfoProvider));
        }

        public ResultWrapper<TxPoolStatus> txpool_status()
        {
            var poolInfo = _txPoolInfoProvider.GetInfo();
            var poolStatus = new TxPoolStatus(poolInfo);

            return ResultWrapper<TxPoolStatus>.Success(poolStatus);
        }

        public ResultWrapper<TxPoolContent> txpool_content()
        {
            var poolInfo = _txPoolInfoProvider.GetInfo();
            return ResultWrapper<TxPoolContent>.Success(new TxPoolContent(poolInfo));
        }

        public ResultWrapper<TxPoolInspection> txpool_inspect()
        {
            var poolInfo = _txPoolInfoProvider.GetInfo();
            return ResultWrapper<TxPoolInspection>.Success(new TxPoolInspection(poolInfo));
        }
    }
}
