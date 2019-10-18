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
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Tracing;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.DebugModule
{
    public class DebugModule : IDebugModule
    {
        private readonly IDebugBridge _debugBridge;
        private readonly ILogger _logger;

        public DebugModule(ILogManager logManager, IDebugBridge debugBridge)
        {
            _debugBridge = debugBridge ?? throw new ArgumentNullException(nameof(debugBridge));
            _logger = logManager.GetClassLogger();
        }

        public ResultWrapper<GethLikeTxTrace> debug_traceTransaction(Keccak transactionHash, GethTraceOptions options = null)
        {
            GethLikeTxTrace transactionTrace = _debugBridge.GetTransactionTrace(transactionHash, options);
            if (transactionTrace == null)
            {
                return ResultWrapper<GethLikeTxTrace>.Fail($"Cannot find transactionTrace for hash: {transactionHash}", ErrorType.NotFound);
            }

            if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceTransaction)} request {transactionHash}, result: trace");
            return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
        }

        public ResultWrapper<GethLikeTxTrace> debug_traceTransactionByBlockhashAndIndex(Keccak blockhash, int index, GethTraceOptions options = null)
        {
            var transactionTrace = _debugBridge.GetTransactionTrace(blockhash, index, options);
            if (transactionTrace == null)
            {
                return ResultWrapper<GethLikeTxTrace>.Fail($"Cannot find transactionTrace {blockhash}", ErrorType.NotFound);
            }

            if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceTransactionByBlockhashAndIndex)} request {blockhash}, result: trace");
            return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
        }

        public ResultWrapper<GethLikeTxTrace> debug_traceTransactionByBlockAndIndex(BlockParameter blockParameter, int index, GethTraceOptions options = null)
        {
            long? blockNo = blockParameter.BlockNumber;
            if (!blockNo.HasValue)
            {
                throw new InvalidDataException("Block number value incorrect");
            }

            var transactionTrace = _debugBridge.GetTransactionTrace(blockNo.Value, index, options);
            if (transactionTrace == null)
            {
                return ResultWrapper<GethLikeTxTrace>.Fail($"Cannot find transactionTrace {blockNo}", ErrorType.NotFound);
            }

            if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceTransactionByBlockAndIndex)} request {blockNo}, result: trace");
            return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
        }

        public ResultWrapper<GethLikeTxTrace> debug_traceTransactionInBlockByHash(byte[] blockRlp, Keccak transactionHash, GethTraceOptions options = null)
        {
            var transactionTrace = _debugBridge.GetTransactionTrace(new Rlp(blockRlp), transactionHash, options);
            if (transactionTrace == null)
            {
                return ResultWrapper<GethLikeTxTrace>.Fail($"Trace is null for RLP {blockRlp.ToHexString()} and transactionTrace hash {transactionHash}", ErrorType.NotFound);
            }

            return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);            
        }
        
        public ResultWrapper<GethLikeTxTrace> debug_traceTransactionInBlockByIndex(byte[] blockRlp, int txIndex, GethTraceOptions options = null)
        {
            var blockTrace = _debugBridge.GetBlockTrace(new Rlp(blockRlp), options);
            var transactionTrace = blockTrace?.ElementAtOrDefault(txIndex);
            if (transactionTrace == null)
            {
                return ResultWrapper<GethLikeTxTrace>.Fail($"Trace is null for RLP {blockRlp.ToHexString()} and transaction index {txIndex}", ErrorType.NotFound);
            }

            return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);            
        }

        public ResultWrapper<GethLikeTxTrace[]> debug_traceBlock(byte[] blockRlp, GethTraceOptions options = null)
        {
            var blockTrace = _debugBridge.GetBlockTrace(new Rlp(blockRlp), options);
            if (blockTrace == null)
            {
                return ResultWrapper<GethLikeTxTrace[]>.Fail($"Trace is null for RLP {blockRlp.ToHexString()}", ErrorType.NotFound);
            }

            return ResultWrapper<GethLikeTxTrace[]>.Success(blockTrace);
        }

        public ResultWrapper<GethLikeTxTrace[]> debug_traceBlockByNumber(BigInteger blockNumber, GethTraceOptions options = null)
        {
            var blockTrace = _debugBridge.GetBlockTrace((long)blockNumber, options);
            if (blockTrace == null)
            {
                return ResultWrapper<GethLikeTxTrace[]>.Fail($"Trace is null for block {blockNumber}", ErrorType.NotFound);
            }

            if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceBlockByNumber)} request {blockNumber}, result: blockTrace");
            return ResultWrapper<GethLikeTxTrace[]>.Success(blockTrace);
        }

        public ResultWrapper<GethLikeTxTrace[]> debug_traceBlockByHash(Keccak blockHash, GethTraceOptions options = null)
        {
            GethLikeTxTrace[] gethLikeBlockTrace = _debugBridge.GetBlockTrace(blockHash, options);
            if (gethLikeBlockTrace == null)
            {
                return ResultWrapper<GethLikeTxTrace[]>.Fail($"Trace is null for block {blockHash}", ErrorType.NotFound);
            }

            if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceBlockByHash)} request {blockHash}, result: blockTrace");
            return ResultWrapper<GethLikeTxTrace[]>.Success(gethLikeBlockTrace);
        }

        public ResultWrapper<GethLikeTxTrace[]> debug_traceBlockFromFile(string fileName, GethTraceOptions options = null)
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

        public ResultWrapper<byte[]> debug_getBlockRlp(long blockNumber)
        {
            byte[] rlp = _debugBridge.GetBlockRlp(blockNumber);
            if (rlp == null)
            {
                return ResultWrapper<byte[]>.Fail($"Block {blockNumber} was not found", ErrorType.NotFound);    
            }
            
            return ResultWrapper<byte[]>.Success(rlp);
        }
        
        public ResultWrapper<byte[]> debug_getBlockRlpByHash(Keccak hash)
        {
            byte[] rlp = _debugBridge.GetBlockRlp(hash);
            if (rlp == null)
            {
                return ResultWrapper<byte[]>.Fail($"Block {hash} was not found", ErrorType.NotFound);    
            }
            
            return ResultWrapper<byte[]>.Success(rlp);
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
            return ResultWrapper<byte[]>.Success(dbValue);
        }

        public ResultWrapper<string> debug_getConfigValue(string category, string name)
        {
            var configValue = _debugBridge.GetConfigValue(category, name);
            return ResultWrapper<string>.Success(configValue);
        }
    }
}