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
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.JsonRpc.Data;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.JsonRpc.Modules.DebugModule
{
    public class DebugRpcModule : IDebugRpcModule
    {
        private readonly IDebugBridge _debugBridge;
        private readonly ILogger _logger;
        private readonly TimeSpan _traceTimeout;
        private readonly IJsonRpcConfig _jsonRpcConfig;
         
        public DebugRpcModule(ILogManager logManager, IDebugBridge debugBridge, IJsonRpcConfig jsonRpcConfig)
        {
            _debugBridge = debugBridge ?? throw new ArgumentNullException(nameof(debugBridge));
            _jsonRpcConfig = jsonRpcConfig ?? throw new ArgumentNullException(nameof(jsonRpcConfig));
            _logger = logManager.GetClassLogger();
            _traceTimeout = TimeSpan.FromMilliseconds(_jsonRpcConfig.Timeout); 
        }

        public ResultWrapper<ChainLevelForRpc> debug_getChainLevel(in long number)
        {
            ChainLevelInfo levelInfo = _debugBridge.GetLevelInfo(number);
            return levelInfo == null
                ? ResultWrapper<ChainLevelForRpc>.Fail($"Chain level {number} does not exist", ErrorCodes.ResourceNotFound)
                : ResultWrapper<ChainLevelForRpc>.Success(new ChainLevelForRpc(levelInfo));
        }
        
        public ResultWrapper<int> debug_deleteChainSlice(in long startNumber)
        {
            return ResultWrapper<int>.Success(_debugBridge.DeleteChainSlice(startNumber));
        }

        public ResultWrapper<GethLikeTxTrace> debug_traceTransaction(Keccak transactionHash, GethTraceOptions options = null)
        {
            using CancellationTokenSource cancellationTokenSource = new(_traceTimeout);
            CancellationToken cancellationToken = cancellationTokenSource.Token;
            GethLikeTxTrace transactionTrace = _debugBridge.GetTransactionTrace(transactionHash, cancellationToken, options);
            if (transactionTrace == null)
            {
                return ResultWrapper<GethLikeTxTrace>.Fail($"Cannot find transactionTrace for hash: {transactionHash}", ErrorCodes.ResourceNotFound);
            }

            if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceTransaction)} request {transactionHash}, result: trace");
            return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
        }

        public ResultWrapper<GethLikeTxTrace> debug_traceTransactionByBlockhashAndIndex(Keccak blockhash, int index, GethTraceOptions options = null)
        {
            using CancellationTokenSource cancellationTokenSource = new(_traceTimeout);
            CancellationToken cancellationToken = cancellationTokenSource.Token;
            var transactionTrace = _debugBridge.GetTransactionTrace(blockhash, index, cancellationToken, options);
            if (transactionTrace == null)
            {
                return ResultWrapper<GethLikeTxTrace>.Fail($"Cannot find transactionTrace {blockhash}", ErrorCodes.ResourceNotFound);
            }

            if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceTransactionByBlockhashAndIndex)} request {blockhash}, result: trace");
            return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
        }

        public ResultWrapper<GethLikeTxTrace> debug_traceTransactionByBlockAndIndex(BlockParameter blockParameter, int index, GethTraceOptions options = null)
        {
            using CancellationTokenSource cancellationTokenSource = new(_traceTimeout);
            CancellationToken cancellationToken = cancellationTokenSource.Token;
            long? blockNo = blockParameter.BlockNumber;
            if (!blockNo.HasValue)
            {
                throw new InvalidDataException("Block number value incorrect");
            }

            var transactionTrace = _debugBridge.GetTransactionTrace(blockNo.Value, index, cancellationToken, options);
            if (transactionTrace == null)
            {
                return ResultWrapper<GethLikeTxTrace>.Fail($"Cannot find transactionTrace {blockNo}", ErrorCodes.ResourceNotFound);
            }

            if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceTransactionByBlockAndIndex)} request {blockNo}, result: trace");
            return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
        }

        public ResultWrapper<GethLikeTxTrace> debug_traceTransactionInBlockByHash(byte[] blockRlp, Keccak transactionHash, GethTraceOptions options = null)
        {
            using CancellationTokenSource cancellationTokenSource = new(_traceTimeout);
            CancellationToken cancellationToken = cancellationTokenSource.Token;
            var transactionTrace = _debugBridge.GetTransactionTrace(new Rlp(blockRlp), transactionHash, cancellationToken, options);
            if (transactionTrace == null)
            {
                return ResultWrapper<GethLikeTxTrace>.Fail($"Trace is null for RLP {blockRlp.ToHexString()} and transactionTrace hash {transactionHash}", ErrorCodes.ResourceNotFound);
            }

            return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);            
        }
        
        public ResultWrapper<GethLikeTxTrace> debug_traceTransactionInBlockByIndex(byte[] blockRlp, int txIndex, GethTraceOptions options = null)
        {
            using CancellationTokenSource cancellationTokenSource = new(_traceTimeout);
            CancellationToken cancellationToken = cancellationTokenSource.Token;
            var blockTrace = _debugBridge.GetBlockTrace(new Rlp(blockRlp), cancellationToken, options);
            var transactionTrace = blockTrace?.ElementAtOrDefault(txIndex);
            if (transactionTrace == null)
            {
                return ResultWrapper<GethLikeTxTrace>.Fail($"Trace is null for RLP {blockRlp.ToHexString()} and transaction index {txIndex}", ErrorCodes.ResourceNotFound);
            }

            return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);            
        }

        public async Task<ResultWrapper<bool>> debug_migrateReceipts(long blockNumber) => 
            ResultWrapper<bool>.Success(await _debugBridge.MigrateReceipts(blockNumber));

        public Task<ResultWrapper<bool>> debug_insertReceipts(BlockParameter blockParameter, ReceiptForRpc[] receiptForRpc)
        {
            _debugBridge.InsertReceipts(blockParameter, receiptForRpc.Select(r => r.ToReceipt()).ToArray());
            return Task.FromResult(ResultWrapper<bool>.Success(true));
        }

        public ResultWrapper<GethLikeTxTrace[]> debug_traceBlock(byte[] blockRlp, GethTraceOptions options = null)
        {
            using CancellationTokenSource cancellationTokenSource = new(_traceTimeout);
            CancellationToken cancellationToken = cancellationTokenSource.Token;
            var blockTrace = _debugBridge.GetBlockTrace(new Rlp(blockRlp), cancellationToken, options);
            if (blockTrace == null)
            {
                return ResultWrapper<GethLikeTxTrace[]>.Fail($"Trace is null for RLP {blockRlp.ToHexString()}", ErrorCodes.ResourceNotFound);
            }

            return ResultWrapper<GethLikeTxTrace[]>.Success(blockTrace);
        }

        public ResultWrapper<GethLikeTxTrace[]> debug_traceBlockByNumber(UInt256 blockNumber, GethTraceOptions options = null)
        {
            using CancellationTokenSource cancellationTokenSource = new(_traceTimeout);
            CancellationToken cancellationToken = cancellationTokenSource.Token;
            var blockTrace = _debugBridge.GetBlockTrace((long)blockNumber, cancellationToken, options);
            if (blockTrace == null)
            {
                return ResultWrapper<GethLikeTxTrace[]>.Fail($"Trace is null for block {blockNumber}", ErrorCodes.ResourceNotFound);
            }

            if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceBlockByNumber)} request {blockNumber}, result: blockTrace");
            return ResultWrapper<GethLikeTxTrace[]>.Success(blockTrace);
        }

        public ResultWrapper<GethLikeTxTrace[]> debug_traceBlockByHash(Keccak blockHash, GethTraceOptions options = null)
        {
            using CancellationTokenSource cancellationTokenSource = new(_traceTimeout);
            CancellationToken cancellationToken = cancellationTokenSource.Token;
            GethLikeTxTrace[] gethLikeBlockTrace = _debugBridge.GetBlockTrace(blockHash, cancellationToken, options);
            if (gethLikeBlockTrace == null)
            {
                return ResultWrapper<GethLikeTxTrace[]>.Fail($"Trace is null for block {blockHash}", ErrorCodes.ResourceNotFound);
            }

            if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceBlockByHash)} request {blockHash}, result: blockTrace");
            return ResultWrapper<GethLikeTxTrace[]>.Success(gethLikeBlockTrace);
        }

        public ResultWrapper<GethLikeTxTrace[]> debug_traceBlockFromFile(string fileName, GethTraceOptions options = null)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<object> debug_dumpBlock(BlockParameter blockParameter)
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
                return ResultWrapper<byte[]>.Fail($"Block {blockNumber} was not found", ErrorCodes.ResourceNotFound);    
            }
            
            return ResultWrapper<byte[]>.Success(rlp);
        }
        
        public ResultWrapper<byte[]> debug_getBlockRlpByHash(Keccak hash)
        {
            byte[] rlp = _debugBridge.GetBlockRlp(hash);
            if (rlp == null)
            {
                return ResultWrapper<byte[]>.Fail($"Block {hash} was not found", ErrorCodes.ResourceNotFound);    
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

        public ResultWrapper<object> debug_getConfigValue(string category, string name)
        {
            var configValue = _debugBridge.GetConfigValue(category, name);
            return ResultWrapper<object>.Success(configValue);
        }
        
        public ResultWrapper<bool> debug_resetHead(Keccak blockHash)
        {
            _debugBridge.UpdateHeadBlock(blockHash);
            return ResultWrapper<bool>.Success(true);
        }
    }
}
