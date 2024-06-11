// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.JsonRpc.Data;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Synchronization.Reporting;
using System.Collections.Generic;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Core.Specs;
using Nethermind.Facade.Eth;

namespace Nethermind.JsonRpc.Modules.DebugModule;

public class DebugRpcModule : IDebugRpcModule
{
    private readonly IDebugBridge _debugBridge;
    private readonly ILogger _logger;
    private readonly TimeSpan _traceTimeout;
    private readonly IJsonRpcConfig _jsonRpcConfig;
    private readonly ISpecProvider _specProvider;
    private readonly BlockDecoder _blockDecoder;

    public DebugRpcModule(ILogManager logManager, IDebugBridge debugBridge, IJsonRpcConfig jsonRpcConfig, ISpecProvider specProvider)
    {
        _debugBridge = debugBridge ?? throw new ArgumentNullException(nameof(debugBridge));
        _jsonRpcConfig = jsonRpcConfig ?? throw new ArgumentNullException(nameof(jsonRpcConfig));
        _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        _logger = logManager.GetClassLogger();
        _traceTimeout = TimeSpan.FromMilliseconds(_jsonRpcConfig.Timeout);
        _blockDecoder = new BlockDecoder();
    }

    public ResultWrapper<ChainLevelForRpc> debug_getChainLevel(in long number)
    {
        ChainLevelInfo levelInfo = _debugBridge.GetLevelInfo(number);
        return levelInfo is null
            ? ResultWrapper<ChainLevelForRpc>.Fail($"Chain level {number} does not exist", ErrorCodes.ResourceNotFound)
            : ResultWrapper<ChainLevelForRpc>.Success(new ChainLevelForRpc(levelInfo));
    }

    public ResultWrapper<int> debug_deleteChainSlice(in long startNumber, bool force = false)
    {
        return ResultWrapper<int>.Success(_debugBridge.DeleteChainSlice(startNumber, force));
    }

    public ResultWrapper<GethLikeTxTrace> debug_traceTransaction(Hash256 transactionHash, GethTraceOptions? options = null)
    {
        using CancellationTokenSource cancellationTokenSource = new(_traceTimeout);
        CancellationToken cancellationToken = cancellationTokenSource.Token;
        GethLikeTxTrace? transactionTrace = _debugBridge.GetTransactionTrace(transactionHash, cancellationToken, options);
        if (transactionTrace is null)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail($"Cannot find transactionTrace for hash: {transactionHash}", ErrorCodes.ResourceNotFound);
        }

        if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceTransaction)} request {transactionHash}, result: trace");
        return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
    }

    public ResultWrapper<GethLikeTxTrace> debug_traceCall(TransactionForRpc call, BlockParameter? blockParameter = null, GethTraceOptions? options = null)
    {
        blockParameter ??= BlockParameter.Latest;
        call.EnsureDefaults(_jsonRpcConfig.GasCap);
        Transaction tx = call.ToTransaction();
        using CancellationTokenSource cancellationTokenSource = new(_traceTimeout);
        CancellationToken cancellationToken = cancellationTokenSource.Token;

        GethLikeTxTrace transactionTrace = _debugBridge.GetTransactionTrace(tx, blockParameter, cancellationToken, options);
        if (transactionTrace is null)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail($"Cannot find transactionTrace for hash: {tx.Hash}", ErrorCodes.ResourceNotFound);
        }

        if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceTransaction)} request {tx.Hash}, result: trace");
        return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
    }

    public ResultWrapper<GethLikeTxTrace> debug_traceTransactionByBlockhashAndIndex(Hash256 blockhash, int index, GethTraceOptions options = null)
    {
        using CancellationTokenSource cancellationTokenSource = new(_traceTimeout);
        CancellationToken cancellationToken = cancellationTokenSource.Token;
        var transactionTrace = _debugBridge.GetTransactionTrace(blockhash, index, cancellationToken, options);
        if (transactionTrace is null)
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
        if (transactionTrace is null)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail($"Cannot find transactionTrace {blockNo}", ErrorCodes.ResourceNotFound);
        }

        if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceTransactionByBlockAndIndex)} request {blockNo}, result: trace");
        return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
    }

    public ResultWrapper<GethLikeTxTrace> debug_traceTransactionInBlockByHash(byte[] blockRlp, Hash256 transactionHash, GethTraceOptions options = null)
    {
        using CancellationTokenSource cancellationTokenSource = new(_traceTimeout);
        CancellationToken cancellationToken = cancellationTokenSource.Token;
        var transactionTrace = _debugBridge.GetTransactionTrace(new Rlp(blockRlp), transactionHash, cancellationToken, options);
        if (transactionTrace is null)
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
        if (transactionTrace is null)
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

    public ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>> debug_traceBlock(byte[] blockRlp, GethTraceOptions options = null)
    {
        using var cancellationTokenSource = new CancellationTokenSource(_traceTimeout);
        var cancellationToken = cancellationTokenSource.Token;
        var blockTrace = _debugBridge.GetBlockTrace(new Rlp(blockRlp), cancellationToken, options);

        if (blockTrace is null)
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Trace is null for RLP {blockRlp.ToHexString()}", ErrorCodes.ResourceNotFound);

        if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceBlock)} request {blockRlp.ToHexString()}, result: {blockTrace}");

        return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Success(blockTrace);
    }

    public ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>> debug_traceBlockByNumber(BlockParameter blockNumber, GethTraceOptions options = null)
    {
        using var cancellationTokenSource = new CancellationTokenSource(_traceTimeout);
        var cancellationToken = cancellationTokenSource.Token;
        var blockTrace = _debugBridge.GetBlockTrace(blockNumber, cancellationToken, options);

        if (blockTrace is null)
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Trace is null for block {blockNumber}", ErrorCodes.ResourceNotFound);

        if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceBlockByNumber)} request {blockNumber}, result: {blockTrace}");

        return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Success(blockTrace);
    }

    public ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>> debug_traceBlockByHash(Hash256 blockHash, GethTraceOptions options = null)
    {
        using var cancellationTokenSource = new CancellationTokenSource(_traceTimeout);
        var cancellationToken = cancellationTokenSource.Token;
        var blockTrace = _debugBridge.GetBlockTrace(new BlockParameter(blockHash), cancellationToken, options);

        if (blockTrace is null)
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Trace is null for block {blockHash}", ErrorCodes.ResourceNotFound);

        if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceBlockByHash)} request {blockHash}, result: {blockTrace}");

        return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Success(blockTrace);
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
        byte[] rlp = _debugBridge.GetBlockRlp(new BlockParameter(blockNumber));
        if (rlp is null)
        {
            return ResultWrapper<byte[]>.Fail($"Block {blockNumber} was not found", ErrorCodes.ResourceNotFound);
        }

        return ResultWrapper<byte[]>.Success(rlp);
    }

    public ResultWrapper<byte[]> debug_getBlockRlpByHash(Hash256 hash)
    {
        byte[] rlp = _debugBridge.GetBlockRlp(new BlockParameter(hash));
        if (rlp is null)
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

    public ResultWrapper<bool> debug_resetHead(Hash256 blockHash)
    {
        _debugBridge.UpdateHeadBlock(blockHash);
        return ResultWrapper<bool>.Success(true);
    }

    public ResultWrapper<byte[]> debug_getRawTransaction(Hash256 transactionHash)
    {
        var transaction = _debugBridge.GetTransactionFromHash(transactionHash);
        if (transaction is null)
        {
            return ResultWrapper<byte[]>.Fail($"Transaction {transactionHash} was not found", ErrorCodes.ResourceNotFound);
        }
        var rlp = Rlp.Encode(transaction);
        return ResultWrapper<byte[]>.Success(rlp.Bytes);
    }

    public ResultWrapper<byte[][]> debug_getRawReceipts(BlockParameter blockParameter)
    {
        TxReceipt[] receipts = _debugBridge.GetReceiptsForBlock(blockParameter);
        if (receipts is null)
        {
            return ResultWrapper<byte[][]>.Fail($"Receipts are not found for block {blockParameter}", ErrorCodes.ResourceNotFound);
        }

        if (!receipts.Any())
        {
            return ResultWrapper<byte[][]>.Success(Array.Empty<byte[]>());
        }
        RlpBehaviors behavior =
            (_specProvider.GetReceiptSpec(receipts[0].BlockNumber).IsEip658Enabled ?
            RlpBehaviors.Eip658Receipts : RlpBehaviors.None) | RlpBehaviors.SkipTypedWrapping;
        var rlp = receipts.Select(tx => Rlp.Encode(tx, behavior).Bytes);
        return ResultWrapper<byte[][]>.Success(rlp.ToArray());
    }

    public ResultWrapper<byte[]> debug_getRawBlock(BlockParameter blockParameter)
    {
        var blockRLP = _debugBridge.GetBlockRlp(blockParameter);
        if (blockRLP is null)
        {
            return ResultWrapper<byte[]>.Fail($"Block {blockParameter} was not found", ErrorCodes.ResourceNotFound);
        }
        return ResultWrapper<byte[]>.Success(blockRLP);
    }

    public ResultWrapper<byte[]> debug_getRawHeader(BlockParameter blockParameter)
    {
        var block = _debugBridge.GetBlock(blockParameter);
        if (block is null)
        {
            return ResultWrapper<byte[]>.Fail($"Block {blockParameter} was not found", ErrorCodes.ResourceNotFound);
        }
        Rlp rlp = Rlp.Encode<BlockHeader>(block.Header);
        return ResultWrapper<byte[]>.Success(rlp.Bytes);
    }

    public Task<ResultWrapper<SyncReportSymmary>> debug_getSyncStage()
    {
        return ResultWrapper<SyncReportSymmary>.Success(_debugBridge.GetCurrentSyncStage());
    }

    public ResultWrapper<IEnumerable<string>> debug_standardTraceBlockToFile(Hash256 blockHash, GethTraceOptions options = null)
    {
        using var cancellationTokenSource = new CancellationTokenSource(_traceTimeout);
        var cancellationToken = cancellationTokenSource.Token;

        var files = _debugBridge.TraceBlockToFile(blockHash, cancellationToken, options);

        if (_logger.IsTrace) _logger.Trace($"{nameof(debug_standardTraceBlockToFile)} request {blockHash}, result: {files}");

        return ResultWrapper<IEnumerable<string>>.Success(files);
    }

    public ResultWrapper<IEnumerable<BadBlock>> debug_getBadBlocks()
    {
        IEnumerable<BadBlock> badBlocks = _debugBridge.GetBadBlocks().Select(block => new BadBlock(block, true, _specProvider, _blockDecoder));
        return ResultWrapper<IEnumerable<BadBlock>>.Success(badBlocks);
    }
}
