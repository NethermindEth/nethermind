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
using Nethermind.JsonRpc.DataModel;

namespace Nethermind.JsonRpc.Module
{
    public class TxPoolModule : ModuleBase, ITxPoolModule
    {
        private readonly IBlockchainBridge _blockchainBridge;
        private readonly IJsonRpcModelMapper _modelMapper;

        public TxPoolModule(IConfigProvider configurationProvider, ILogManager logManager, IJsonSerializer jsonSerializer,
            IBlockchainBridge blockchainBridge, IJsonRpcModelMapper modelMapper) : base(configurationProvider, logManager, jsonSerializer)
        {
            _blockchainBridge = blockchainBridge;
            _modelMapper = modelMapper;
        }
        
        public ResultWrapper<TransactionPoolStatus> txpool_status()
            => ResultWrapper<TransactionPoolStatus>.Success(
                _modelMapper.MapTransactionPoolStatus(_blockchainBridge.GetTransactionPoolInfo()));

        public ResultWrapper<TransactionPoolContent> txpool_content()
            => ResultWrapper<TransactionPoolContent>.Success(
                _modelMapper.MapTransactionPoolContent(_blockchainBridge.GetTransactionPoolInfo()));

        public ResultWrapper<TransactionPoolInspection> txpool_inspect()
            => ResultWrapper<TransactionPoolInspection>.Success(
                _modelMapper.MapTransactionPoolInspection(_blockchainBridge.GetTransactionPoolInfo()));
        
        public override ModuleType ModuleType => ModuleType.TxPool;
    }
}