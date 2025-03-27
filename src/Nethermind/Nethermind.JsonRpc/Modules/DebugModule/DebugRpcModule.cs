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
using Nethermind.Facade.Eth.RpcTransaction;
using DotNetty.Buffers;
using Nethermind.TxPool;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Facade;
using Nethermind.Facade.Simulate;
using Nethermind.State;

namespace Nethermind.JsonRpc.Modules.DebugModule;

public class DebugRpcModule : IDebugRpcModule
{
    private readonly IDebugBridge _debugBridge;
    private readonly ILogger _logger;
    private readonly IJsonRpcConfig _jsonRpcConfig;
    private readonly ISpecProvider _specProvider;
    private readonly BlockDecoder _blockDecoder;
    private readonly IBlockchainBridge _blockchainBridge;
    private readonly ulong _secondsPerSlot;
    private readonly IBlockFinder _blockFinder;
    private readonly IStateReader _stateReader;

    public DebugRpcModule(ILogManager logManager, IDebugBridge debugBridge, IJsonRpcConfig jsonRpcConfig, ISpecProvider specProvider, IBlockchainBridge? blockchainBridge, ulong? secondsPerSlot, IBlockFinder? blockFinder, IStateReader? stateReader)
    {
        _debugBridge = debugBridge ?? throw new ArgumentNullException(nameof(debugBridge));
        _jsonRpcConfig = jsonRpcConfig ?? throw new ArgumentNullException(nameof(jsonRpcConfig));
        _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        _logger = logManager.GetClassLogger();
        _blockDecoder = new BlockDecoder();
        _blockchainBridge = blockchainBridge ?? throw new ArgumentNullException(nameof(blockchainBridge));
        _secondsPerSlot = secondsPerSlot ?? throw new ArgumentNullException(nameof(secondsPerSlot));
        _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));
        _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
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
        // First, find the block by transaction hash
        var transactionAndBlock = _debugBridge.GetTransactionFromHash(transactionHash);
        if (transactionAndBlock == null)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail($"Cannot find transaction for hash: {transactionHash}", ErrorCodes.ResourceNotFound);
        }

        // Find the block hash
        var receiptBlockHash = _debugBridge.GetReceiptsForBlock(new BlockParameter(transactionHash))?.FirstOrDefault()?.BlockHash;
        if (receiptBlockHash == null)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail($"Cannot find block for transaction hash: {transactionHash}", ErrorCodes.ResourceNotFound);
        }

        // Get the block header
        SearchResult<BlockHeader> headerSearch = _blockFinder.SearchForHeader(new BlockParameter(receiptBlockHash));
        if (headerSearch.IsError)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail(headerSearch);
        }

        // Check if state is available for the block
        if (!_stateReader.HasStateForBlock(headerSearch.Object!))
        {
            return GetStateFailureResult<GethLikeTxTrace>(headerSearch.Object!);
        }

        using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;
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
        
        // Check if state is available for the block
        SearchResult<BlockHeader> headerSearch = _blockFinder.SearchForHeader(blockParameter);
        if (headerSearch.IsError)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail(headerSearch);
        }

        BlockHeader header = headerSearch.Object!;
        if (!_stateReader.HasStateForBlock(header))
        {
            return GetStateFailureResult<GethLikeTxTrace>(header);
        }

        call.EnsureDefaults(_jsonRpcConfig.GasCap);
        Transaction tx = call.ToTransaction();
        using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;

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
        // Check if state is available for the block
        SearchResult<BlockHeader> headerSearch = _blockFinder.SearchForHeader(new BlockParameter(blockhash));
        if (headerSearch.IsError)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail(headerSearch);
        }

        BlockHeader header = headerSearch.Object!;
        if (!_stateReader.HasStateForBlock(header))
        {
            return GetStateFailureResult<GethLikeTxTrace>(header);
        }

        using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;
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
        // Check if state is available for the block
        SearchResult<BlockHeader> headerSearch = _blockFinder.SearchForHeader(blockParameter);
        if (headerSearch.IsError)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail(headerSearch);
        }

        BlockHeader header = headerSearch.Object!;
        if (!_stateReader.HasStateForBlock(header))
        {
            return GetStateFailureResult<GethLikeTxTrace>(header);
        }

        using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;
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
        // For RLP block we can't check state as we don't know the block hash
        using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;
        var transactionTrace = _debugBridge.GetTransactionTrace(new Rlp(blockRlp), transactionHash, cancellationToken, options);
        if (transactionTrace is null)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail($"Trace is null for RLP {blockRlp.ToHexString()} and transactionTrace hash {transactionHash}", ErrorCodes.ResourceNotFound);
        }

        return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
    }

    public ResultWrapper<GethLikeTxTrace> debug_traceTransactionInBlockByIndex(byte[] blockRlp, int txIndex, GethTraceOptions options = null)
    {
        // For RLP block we can't check state as we don't know the block hash
        using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;
        var blockTrace = _debugBridge.GetBlockTrace(new Rlp(blockRlp), cancellationToken, options);
        var transactionTrace = blockTrace?.ElementAtOrDefault(txIndex);
        if (transactionTrace is null)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail($"Trace is null for RLP {blockRlp.ToHexString()} and transaction index {txIndex}", ErrorCodes.ResourceNotFound);
        }

        return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
    }

    public async Task<ResultWrapper<bool>> debug_migrateReceipts(long from, long to) =>
        ResultWrapper<bool>.Success(await _debugBridge.MigrateReceipts(from, to));

    public Task<ResultWrapper<bool>> debug_insertReceipts(BlockParameter blockParameter, ReceiptForRpc[] receiptForRpc)
    {
        _debugBridge.InsertReceipts(blockParameter, receiptForRpc.Select(static r => r.ToReceipt()).ToArray());
        return Task.FromResult(ResultWrapper<bool>.Success(true));
    }

    public ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>> debug_traceBlock(byte[] blockRlp, GethTraceOptions options = null)
    {
        // For RLP block we can't check state as we don't know the block hash
        using CancellationTokenSource? timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;
        try
        {
            var blockTrace = _debugBridge.GetBlockTrace(new Rlp(blockRlp), cancellationToken, options);

            if (blockTrace is null)
                return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Trace is null for RLP {blockRlp.ToHexString()}", ErrorCodes.ResourceNotFound);

            if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceBlock)} request {blockRlp.ToHexString()}, result: {blockTrace}");

            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Success(blockTrace);
        }
        catch (RlpException)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Error decoding block rlp: {blockRlp.ToHexString()}", ErrorCodes.InvalidInput);
        }
        catch (ArgumentNullException)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Couldn't find any block", ErrorCodes.InvalidInput);
        }
    }

    public ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>> debug_traceBlockByNumber(BlockParameter blockParameter, GethTraceOptions options = null)
    {
        // Check if state is available for the block
        SearchResult<BlockHeader> headerSearch = _blockFinder.SearchForHeader(blockParameter);
        if (headerSearch.IsError)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail(headerSearch);
        }

        BlockHeader header = headerSearch.Object!;
        if (!_stateReader.HasStateForBlock(header))
        {
            return GetStateFailureResult<IReadOnlyCollection<GethLikeTxTrace>>(header);
        }

        using CancellationTokenSource? timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;
        IReadOnlyCollection<GethLikeTxTrace>? blockTrace = _debugBridge.GetBlockTrace(blockParameter, cancellationToken, options);

        try
        {
            if (blockTrace is null)
                return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Trace is null for block {blockParameter}", ErrorCodes.ResourceNotFound);

            if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceBlockByNumber)} request {blockParameter}, result: {blockTrace}");

            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Success(blockTrace);
        }
        catch (ArgumentNullException)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Trace is null for block {blockParameter}", ErrorCodes.InvalidInput);
        }
    }

    public ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>> debug_traceBlockByHash(Hash256 blockHash, GethTraceOptions options = null)
    {
        // Check if state is available for the block
        SearchResult<BlockHeader> headerSearch = _blockFinder.SearchForHeader(new BlockParameter(blockHash));
        if (headerSearch.IsError)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail(headerSearch);
        }

        BlockHeader header = headerSearch.Object!;
        if (!_stateReader.HasStateForBlock(header))
        {
            return GetStateFailureResult<IReadOnlyCollection<GethLikeTxTrace>>(header);
        }

        using CancellationTokenSource? timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;
        IReadOnlyCollection<GethLikeTxTrace>? blockTrace = _debugBridge.GetBlockTrace(new BlockParameter(blockHash), cancellationToken, options);

        try
        {
            if (blockTrace is null)
                return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Trace is null for block {blockHash}", ErrorCodes.ResourceNotFound);

            if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceBlockByHash)} request {blockHash}, result: {blockTrace}");

            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Success(blockTrace);
        }
        catch (ArgumentNullException)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Trace is null for block {blockHash}", ErrorCodes.InvalidInput);
        }
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
        byte[] data = _debugBridge.GetBlockRlp(blockNumber);
        return ResultWrapper<byte[]>.Success(data);
    }

    public ResultWrapper<byte[]> debug_getBlockRlpByHash(Hash256 hash)
    {
        byte[] data = _debugBridge.GetBlockRlp(hash);
        return ResultWrapper<byte[]>.Success(data);
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
        byte[] data = _debugBridge.GetDbValue(dbName, key);
        return ResultWrapper<byte[]>.Success(data);
    }

    public ResultWrapper<object> debug_getConfigValue(string category, string name)
    {
        object value = _debugBridge.GetConfigValue(category, name);
        return ResultWrapper<object>.Success(value);
    }

    public ResultWrapper<bool> debug_resetHead(Hash256 blockHash)
    {
        _debugBridge.UpdateHeadBlock(blockHash);
        return ResultWrapper<bool>.Success(true);
    }

    public ResultWrapper<string?> debug_getRawTransaction(Hash256 transactionHash)
    {
        Transaction? transaction = _debugBridge.GetTransactionFromHash(transactionHash);
        if (transaction is null)
        {
            return ResultWrapper<string?>.Success(null);
        }

        try
        {
            return ResultWrapper<string?>.Success(Rlp.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes.ToHexString(true));
        }
        catch (Exception)
        {
            return ResultWrapper<string?>.Success(null);
        }
    }

    public ResultWrapper<byte[][]> debug_getRawReceipts(BlockParameter blockParameter)
    {
        TxReceipt[]? receipts = _debugBridge.GetReceiptsForBlock(blockParameter);
        if (receipts is null)
        {
            return ResultWrapper<byte[][]>.Success([]);
        }

        byte[][] result = new byte[receipts.Length][];
        for (int i = 0; i < receipts.Length; i++)
        {
            result[i] = Rlp.Encode(receipts[i]).Bytes;
        }

        return ResultWrapper<byte[][]>.Success(result);
    }

    public ResultWrapper<byte[]> debug_getRawBlock(BlockParameter blockParameter)
    {
        Block? block = _debugBridge.GetBlock(blockParameter);
        if (block is null)
        {
            return ResultWrapper<byte[]>.Success(Array.Empty<byte>());
        }

        return ResultWrapper<byte[]>.Success(Rlp.Encode(block).Bytes);
    }

    public ResultWrapper<byte[]> debug_getRawHeader(BlockParameter blockParameter)
    {
        Block? block = _debugBridge.GetBlock(blockParameter);
        if (block is null)
        {
            return ResultWrapper<byte[]>.Success(Array.Empty<byte>());
        }

        return ResultWrapper<byte[]>.Success(Rlp.Encode(block.Header).Bytes);
    }

    public Task<ResultWrapper<SyncReportSymmary>> debug_getSyncStage()
    {
        SyncReportSymmary summary = _debugBridge.GetCurrentSyncStage();
        return Task.FromResult(ResultWrapper<SyncReportSymmary>.Success(summary));
    }

    public ResultWrapper<IEnumerable<string>> debug_standardTraceBlockToFile(Hash256 blockHash, GethTraceOptions options = null)
    {
        // Check if state is available for the block
        SearchResult<BlockHeader> headerSearch = _blockFinder.SearchForHeader(new BlockParameter(blockHash));
        if (headerSearch.IsError)
        {
            return ResultWrapper<IEnumerable<string>>.Fail(headerSearch);
        }

        BlockHeader header = headerSearch.Object!;
        if (!_stateReader.HasStateForBlock(header))
        {
            return GetStateFailureResult<IEnumerable<string>>(header);
        }

        using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;
        var txTraces = _debugBridge.TraceBlockToFile(blockHash, cancellationToken, options);
        return ResultWrapper<IEnumerable<string>>.Success(txTraces);
    }

    public ResultWrapper<IEnumerable<string>> debug_standardTraceBadBlockToFile(Hash256 blockHash, GethTraceOptions options = null)
    {
        // For "bad" block we don't check state availability since the block might be invalid
        using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;
        var txTraces = _debugBridge.TraceBadBlockToFile(blockHash, cancellationToken, options);
        return ResultWrapper<IEnumerable<string>>.Success(txTraces);
    }

    public ResultWrapper<IEnumerable<BadBlock>> debug_getBadBlocks()
    {
        IEnumerable<Block> blocks = _debugBridge.GetBadBlocks();
        List<BadBlock> badBlocks = blocks.Select(block => new BadBlock(block, true, _specProvider, _blockDecoder)).ToList();
        return ResultWrapper<IEnumerable<BadBlock>>.Success(badBlocks);
    }

    private CancellationTokenSource BuildTimeoutCancellationTokenSource() =>
        _jsonRpcConfig.BuildTimeoutCancellationToken();

    private static ResultWrapper<TResult> GetStateFailureResult<TResult>(BlockHeader header) =>
        ResultWrapper<TResult>.Fail($"No state available for block {header.ToString(BlockHeader.Format.FullHashAndNumber)}", ErrorCodes.ResourceUnavailable);

    public ResultWrapper<IReadOnlyList<SimulateBlockResult<GethLikeTxTrace>>> debug_simulateV1(
        SimulatePayload<TransactionForRpc> payload, BlockParameter? blockParameter = null, GethTraceOptions? options = null)
    {
        return new SimulateTxExecutor<GethLikeTxTrace>(_blockchainBridge, _blockFinder, _jsonRpcConfig, new GethStyleSimulateBlockTracerFactory(options ?? GethTraceOptions.Default), _secondsPerSlot)
            .Execute(payload, blockParameter);
    }
}
