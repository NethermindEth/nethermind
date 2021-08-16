//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
