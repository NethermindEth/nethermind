// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Specs;
using Nethermind.TxPool;

namespace Nethermind.JsonRpc.Modules.TxPool
{
    public class TxPoolRpcModule : ITxPoolRpcModule
    {
        private readonly ITxPoolInfoProvider _txPoolInfoProvider;
        private readonly ISpecProvider _specProvider;

        public TxPoolRpcModule(ITxPoolInfoProvider txPoolInfoProvider, ISpecProvider specProvider)
        {
            _txPoolInfoProvider = txPoolInfoProvider ?? throw new ArgumentNullException(nameof(txPoolInfoProvider));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
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
            var chainId = _specProvider.ChainId;
            return ResultWrapper<TxPoolContent>.Success(new TxPoolContent(poolInfo, chainId));
        }

        public ResultWrapper<TxPoolInspection> txpool_inspect()
        {
            var poolInfo = _txPoolInfoProvider.GetInfo();
            return ResultWrapper<TxPoolInspection>.Success(new TxPoolInspection(poolInfo));
        }
    }
}
