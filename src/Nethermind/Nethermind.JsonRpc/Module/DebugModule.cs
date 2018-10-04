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
using System.Linq;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Dirichlet.Numerics;
using Nethermind.JsonRpc.DataModel;

namespace Nethermind.JsonRpc.Module
{
    public class DebugModule : ModuleBase, IDebugModule
    {
        private readonly IBlockchainBridge _blockchainBridge;
        private readonly IJsonRpcModelMapper _modelMapper;

        public DebugModule(IConfigProvider configurationProvider, ILogManager logManager, IBlockchainBridge blockchainBridge, IJsonRpcModelMapper modelMapper, IJsonSerializer jsonSerializer)
            : base(configurationProvider, logManager, jsonSerializer)
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

            if (Logger.IsTrace) Logger.Trace($"{nameof(debug_traceTransaction)} request {transationHash.ToJson()}, result: {GetJsonLog(transactionModel.ToJson())}");
            return ResultWrapper<TransactionTrace>.Success(transactionModel);
        }
        
        public ResultWrapper<TransactionTrace> debug_traceTransactionByBlockhashAndIndex(Data blockhash, int index)
        {
            var transactionTrace = _blockchainBridge.GetTransactionTrace(new Keccak(blockhash.Value), index);
            if (transactionTrace == null)
            {
                return ResultWrapper<TransactionTrace>.Fail($"Cannot find transactionTrace {blockhash}", ErrorType.NotFound);
            }
            var transactionModel = _modelMapper.MapTransactionTrace(transactionTrace);

            if (Logger.IsTrace) Logger.Trace($"{nameof(debug_traceTransactionByBlockhashAndIndex)} request {blockhash}, result: {GetJsonLog(transactionModel.ToJson())}");
            return ResultWrapper<TransactionTrace>.Success(transactionModel);
        }
        
        public ResultWrapper<TransactionTrace> debug_traceTransactionByBlockAndIndex(BlockParameter blockParameter, int index)
        {
            UInt256 blockNo = (UInt256) blockParameter.BlockId.GetValue().Value;
            var transactionTrace = _blockchainBridge.GetTransactionTrace(blockNo, index);
            if (transactionTrace == null)
            {
                return ResultWrapper<TransactionTrace>.Fail($"Cannot find transactionTrace {blockNo}", ErrorType.NotFound);
            }
            var transactionModel = _modelMapper.MapTransactionTrace(transactionTrace);

            if (Logger.IsTrace) Logger.Trace($"{nameof(debug_traceTransactionByBlockAndIndex)} request {blockNo}, result: {GetJsonLog(transactionModel.ToJson())}");
            return ResultWrapper<TransactionTrace>.Success(transactionModel);
        }

        public ResultWrapper<bool> debug_addTxData(BlockParameter blockParameter)
        {
            if (blockParameter.Type != BlockParameterType.BlockId)
            {
                throw new InvalidOperationException("Can only addTxData for historical blocks");
            }
            
            UInt256 blockNo = (UInt256) blockParameter.BlockId.GetValue().Value;
            _blockchainBridge.AddTxData(blockNo);
            return ResultWrapper<bool>.Success(true);
        }

        public ResultWrapper<BlockTraceItem[]> debug_traceBlock(Data blockRlp)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<BlockTraceItem[]> debug_traceBlockByNumber(BlockParameter blockParameter)
        {
            if (blockParameter.Type != BlockParameterType.BlockId)
            {
                throw new InvalidOperationException("Can only addTxData for historical blocks");
            }

            UInt256 blockNo = (UInt256) blockParameter.BlockId.GetValue().Value;
            var blockTrace = _blockchainBridge.GetBlockTrace(blockNo); // tks ...
            if (blockTrace == null)
            {
                return ResultWrapper<BlockTraceItem[]>.Fail($"Trace is null for block {blockNo}", ErrorType.NotFound);
            }
            
            var blockTraceModel = _modelMapper.MapBlockTrace(blockTrace);

            if (Logger.IsTrace) Logger.Trace($"{nameof(debug_traceBlockByNumber)} request {blockParameter}, result: {GetJsonLog(blockTraceModel.Select(btm => btm.ToJson()))}");
            return ResultWrapper<BlockTraceItem[]>.Success(blockTraceModel);
        }

        public ResultWrapper<BlockTraceItem[]> debug_traceBlockByHash(Data blockHash)
        {
            var blockTrace = _blockchainBridge.GetBlockTrace(new Keccak(blockHash.Value));
            if (blockTrace == null)
            {
                return ResultWrapper<BlockTraceItem[]>.Fail($"Trace is null for block {blockHash}", ErrorType.NotFound);
            }
            
            var blockTraceModel = _modelMapper.MapBlockTrace(blockTrace);

            if (Logger.IsTrace) Logger.Trace($"{nameof(debug_traceBlockByHash)} request {blockHash.ToJson()}, result: {GetJsonLog(blockTraceModel.Select(btm => btm.ToJson()))}");
            return ResultWrapper<BlockTraceItem[]>.Success(blockTraceModel);
        }

        public ResultWrapper<BlockTraceItem[]> debug_traceBlockFromFile(string fileName)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<State> debug_dumpBlock(BlockParameter blockParameter)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<GcStats> debug_gcStats()
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<Data> debug_getBlockRlp(BlockParameter blockParameter)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<MemStats> debug_memStats(BlockParameter blockParameter)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<Data> debug_seedHash(BlockParameter blockParameter)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<bool> debug_setHead(BlockParameter blockParameter)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<byte[]> debug_getFromDb(string dbName, Data key)
        {
            var dbValue = _blockchainBridge.GetDbValue(dbName, key.Value);
            if (Logger.IsTrace) Logger.Trace($"{nameof(debug_getFromDb)} request [{dbName}, {key.Value.ToHexString()}], result: {dbValue.ToHexString()}");
            return ResultWrapper<byte[]>.Success(dbValue);
        }
    }
}