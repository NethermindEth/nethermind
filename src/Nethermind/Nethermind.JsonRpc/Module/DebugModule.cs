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
using System.IO;
using System.Linq;
using System.Numerics;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.JsonRpc.DataModel;
using TransactionTrace = Nethermind.JsonRpc.DataModel.TransactionTrace;

namespace Nethermind.JsonRpc.Module
{
    public class DebugModule : ModuleBase, IDebugModule
    {
        private readonly IDebugBridge _debugBridge;
        private readonly IJsonRpcModelMapper _modelMapper;

        public DebugModule(IConfigProvider configurationProvider, ILogManager logManager, IDebugBridge debugBridge, IJsonRpcModelMapper modelMapper, IJsonSerializer jsonSerializer)
            : base(configurationProvider, logManager, jsonSerializer)
        {
            _debugBridge = debugBridge;
            _modelMapper = modelMapper;
        }

        public ResultWrapper<TransactionTrace> debug_traceTransaction(Data transationHash)
        {
            var transactionTrace = _debugBridge.GetTransactionTrace(new Keccak(transationHash.Value));
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
            var transactionTrace = _debugBridge.GetTransactionTrace(new Keccak(blockhash.Value), index);
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
            UInt256? blockNo = blockParameter.BlockId.AsNumber();
            if (!blockNo.HasValue)
            {
                throw new InvalidDataException("Block number value incorrect");
            }

            var transactionTrace = _debugBridge.GetTransactionTrace(blockNo.Value, index);
            if (transactionTrace == null)
            {
                return ResultWrapper<TransactionTrace>.Fail($"Cannot find transactionTrace {blockNo}", ErrorType.NotFound);
            }

            var transactionModel = _modelMapper.MapTransactionTrace(transactionTrace);

            if (Logger.IsTrace) Logger.Trace($"{nameof(debug_traceTransactionByBlockAndIndex)} request {blockNo}, result: {GetJsonLog(transactionModel.ToJson())}");
            return ResultWrapper<TransactionTrace>.Success(transactionModel);
        }
        
        public ResultWrapper<bool> debug_addTxDataByNumber(Quantity blockNumberData)
        {
            UInt256? blockNumber = blockNumberData.AsNumber();
            if (!blockNumber.HasValue)
            {
                throw new InvalidDataException("Expected block number value");
            }
            
            _debugBridge.AddTxData(blockNumber.Value);
            return ResultWrapper<bool>.Success(true);
        }
        
        public ResultWrapper<bool> debug_addTxDataByHash(Data blockHashData)
        {
            Keccak blockHash = new Keccak(blockHashData.Value);
            
            _debugBridge.AddTxData(blockHash);
            return ResultWrapper<bool>.Success(true);
        }

        public ResultWrapper<BlockTraceItem[]> debug_traceBlock(Data blockRlp)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<BlockTraceItem[]> debug_traceBlockByNumber(Quantity blockNumber)
        {
            UInt256? blockNo = blockNumber.AsNumber();
            if (!blockNo.HasValue)
            {
                throw new InvalidDataException("Expected block number value");
            }

            var blockTrace = _debugBridge.GetBlockTrace(blockNo.Value);
            if (blockTrace == null)
            {
                return ResultWrapper<BlockTraceItem[]>.Fail($"Trace is null for block {blockNo}", ErrorType.NotFound);
            }

            var blockTraceModel = _modelMapper.MapBlockTrace(blockTrace);

            if (Logger.IsTrace) Logger.Trace($"{nameof(debug_traceBlockByNumber)} request {blockNumber}, result: {GetJsonLog(blockTraceModel.Select(btm => btm.ToJson()))}");
            return ResultWrapper<BlockTraceItem[]>.Success(blockTraceModel);
        }

        public ResultWrapper<BlockTraceItem[]> debug_traceBlockByHash(Data blockHash)
        {
            BlockTrace blockTrace = _debugBridge.GetBlockTrace(new Keccak(blockHash.Value));
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
            var dbValue = _debugBridge.GetDbValue(dbName, key.Value);
            if (Logger.IsTrace) Logger.Trace($"{nameof(debug_getFromDb)} request [{dbName}, {key.Value.ToHexString()}], result: {dbValue.ToHexString()}");
            return ResultWrapper<byte[]>.Success(dbValue);
        }
    }
}