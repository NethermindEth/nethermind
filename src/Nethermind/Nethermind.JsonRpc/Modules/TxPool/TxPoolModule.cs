/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Logging;
using Nethermind.Facade;

namespace Nethermind.JsonRpc.Modules.TxPool
{
    public class TxPoolModule : ModuleBase, ITxPoolModule
    {
        private readonly IBlockchainBridge _blockchainBridge;        

        public TxPoolModule(IConfigProvider configProvider, ILogManager logManager, IJsonSerializer jsonSerializer,
            IBlockchainBridge blockchainBridge) : base(configProvider, logManager, jsonSerializer)
        {
            _blockchainBridge = blockchainBridge;
        }

        public ResultWrapper<TransactionPoolStatus> txpool_status()
        {
            var poolInfo = _blockchainBridge.GetTransactionPoolInfo();
            var poolStatus = new TransactionPoolStatus(poolInfo);
         
            return ResultWrapper<TransactionPoolStatus>.Success(poolStatus);
        }

        public ResultWrapper<TransactionPoolContent> txpool_content()
        {
            var poolInfo = _blockchainBridge.GetTransactionPoolInfo();
            return ResultWrapper<TransactionPoolContent>.Success(new TransactionPoolContent(poolInfo));
        }

        public ResultWrapper<TransactionPoolInspection> txpool_inspect()
        {
            var poolInfo = _blockchainBridge.GetTransactionPoolInfo();
            return ResultWrapper<TransactionPoolInspection>.Success(new TransactionPoolInspection(poolInfo));
        }
        
        public override ModuleType ModuleType => ModuleType.TxPool;
    }
}