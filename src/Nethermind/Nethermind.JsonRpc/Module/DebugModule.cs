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

using System;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Dirichlet.Numerics;
using Nethermind.JsonRpc.DataModel;

namespace Nethermind.JsonRpc.Module
{
    public class DebugModule : ModuleBase, IDebugModule
    {
        private readonly IBlockchainBridge _blockchainBridge;
        private readonly IJsonRpcModelMapper _modelMapper;

        public DebugModule(IConfigProvider configurationProvider, ILogManager logManager, IBlockchainBridge blockchainBridge, IJsonRpcModelMapper modelMapper, IJsonSerializer jsonSerializer) : base(configurationProvider, logManager, jsonSerializer)
        {
            _blockchainBridge = blockchainBridge;
            _modelMapper = modelMapper;
        }

        public ResultWrapper<TransactionTrace> debug_traceTransaction(Data transationHash)
        {
            var transactionTrace = _blockchainBridge.GetTransactionTrace(new Keccak(transationHash.Value));
            if (transactionTrace == null)
            {
                return ResultWrapper<TransactionTrace>.Fail($"Cannot find transactionTrace for hash: {transationHash.Value}", ErrorType.NotFound);
            }
            var transactionModel = _modelMapper.MapTransactionTrace(transactionTrace);

            if (Logger.IsTrace) Logger.Trace($"debug_traceTransaction request {transationHash.ToJson()}, result: {GetJsonLog(transactionModel.ToJson())}");
            return ResultWrapper<TransactionTrace>.Success(transactionModel);
        }

        public ResultWrapper<bool> debug_addTxData(BlockParameter blockParameter)
        {
            if (blockParameter.Type != BlockParameterType.BlockId)
            {
                throw new InvalidOperationException("Can only addTxData for historical blocks");
            }
            
            _blockchainBridge.AddTxData((UInt256)blockParameter.BlockId.GetValue().Value); // tks ...
            return ResultWrapper<bool>.Success(true);
        }
    }
}