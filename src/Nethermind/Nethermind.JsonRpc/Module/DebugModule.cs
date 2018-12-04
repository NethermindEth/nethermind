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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm.Tracing;
using Nethermind.JsonRpc.DataModel;
using Newtonsoft.Json;
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

        public override IReadOnlyCollection<JsonConverter> GetConverters()
        {
            return new[] {new GethLikeTxTraceConverter()};
        }

        public ResultWrapper<GethLikeTxTrace> debug_traceTransaction(Keccak transactionHash)
        {
            var transactionTrace = _debugBridge.GetTransactionTrace(transactionHash);
            if (transactionTrace == null)
            {
                return ResultWrapper<GethLikeTxTrace>.Fail($"Cannot find transactionTrace for hash: {transactionHash}", ErrorType.NotFound);
            }

            if (Logger.IsTrace) Logger.Trace($"{nameof(debug_traceTransaction)} request {transactionHash}, result: trace");
            return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
        }

        public ResultWrapper<GethLikeTxTrace> debug_traceTransactionByBlockhashAndIndex(Keccak blockhash, int index)
        {
            var transactionTrace = _debugBridge.GetTransactionTrace(blockhash, index);
            if (transactionTrace == null)
            {
                return ResultWrapper<GethLikeTxTrace>.Fail($"Cannot find transactionTrace {blockhash}", ErrorType.NotFound);
            }

            if (Logger.IsTrace) Logger.Trace($"{nameof(debug_traceTransactionByBlockhashAndIndex)} request {blockhash}, result: trace");
            return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
        }

        public ResultWrapper<GethLikeTxTrace> debug_traceTransactionByBlockAndIndex(BlockParameter blockParameter, int index)
        {
            UInt256? blockNo = blockParameter.BlockId.AsNumber();
            if (!blockNo.HasValue)
            {
                throw new InvalidDataException("Block number value incorrect");
            }

            var transactionTrace = _debugBridge.GetTransactionTrace(blockNo.Value, index);
            if (transactionTrace == null)
            {
                return ResultWrapper<GethLikeTxTrace>.Fail($"Cannot find transactionTrace {blockNo}", ErrorType.NotFound);
            }

            if (Logger.IsTrace) Logger.Trace($"{nameof(debug_traceTransactionByBlockAndIndex)} request {blockNo}, result: trace");
            return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
        }

        public ResultWrapper<BlockTraceItem[]> debug_traceBlock(byte[] blockRlp)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<BlockTraceItem[]> debug_traceBlockByNumber(BigInteger blockNumber)
        {
            var blockTrace = _debugBridge.GetBlockTrace((UInt256) blockNumber);
            if (blockTrace == null)
            {
                return ResultWrapper<BlockTraceItem[]>.Fail($"Trace is null for block {blockNumber}", ErrorType.NotFound);
            }

            var blockTraceModel = _modelMapper.MapBlockTrace(blockTrace);

            if (Logger.IsTrace) Logger.Trace($"{nameof(debug_traceBlockByNumber)} request {blockNumber}, result: {GetJsonLog(blockTraceModel.Select(btm => btm.ToJson()))}");
            return ResultWrapper<BlockTraceItem[]>.Success(blockTraceModel);
        }

        public ResultWrapper<BlockTraceItem[]> debug_traceBlockByHash(Keccak blockHash)
        {
            GethLikeTxTrace[] gethLikeBlockTrace = _debugBridge.GetBlockTrace(blockHash);
            if (gethLikeBlockTrace == null)
            {
                return ResultWrapper<BlockTraceItem[]>.Fail($"Trace is null for block {blockHash}", ErrorType.NotFound);
            }

            var blockTraceModel = _modelMapper.MapBlockTrace(gethLikeBlockTrace);

            if (Logger.IsTrace) Logger.Trace($"{nameof(debug_traceBlockByHash)} request {blockHash}, result: {GetJsonLog(blockTraceModel.Select(btm => btm.ToJson()))}");
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

        public ResultWrapper<byte[]> debug_getBlockRlp(BlockParameter blockParameter)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<MemStats> debug_memStats(BlockParameter blockParameter)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<byte[]> debug_seedHash(BlockParameter blockParameter)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<bool> debug_setHead(BlockParameter blockParameter)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<byte[]> debug_getFromDb(string dbName, byte[] key)
        {
            var dbValue = _debugBridge.GetDbValue(dbName, key);
            if (Logger.IsTrace) Logger.Trace($"{nameof(debug_getFromDb)} request [{dbName}, {key.ToHexString()}], result: {dbValue.ToHexString()}");
            return ResultWrapper<byte[]>.Success(dbValue);
        }

        public ResultWrapper<bool> debug_dumpPeerConnectionDetails()
        {
            var result = _debugBridge.LogPeerConnectionDetails();
            return ResultWrapper<bool>.Success(result);
        }

        public override ModuleType ModuleType => ModuleType.Debug;
    }
}