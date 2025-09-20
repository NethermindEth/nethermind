// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
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
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.JsonRpc.Data;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Synchronization.Reporting;
using System.Collections.Generic;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Core.Specs;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Config;
using Nethermind.Consensus.Stateless;
using Nethermind.TxPool;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Facade;
using Nethermind.Facade.Simulate;

namespace Nethermind.JsonRpc.Modules.DebugModule;

public class DebugRpcModule(
    ILogManager logManager,
    IDebugBridge debugBridge,
    IJsonRpcConfig jsonRpcConfig,
    ISpecProvider specProvider,
    IBlockchainBridge blockchainBridge,
    IBlocksConfig blocksConfig,
    IBlockFinder blockFinder)
    : IDebugRpcModule
{
    private readonly ILogger _logger = logManager.GetClassLogger();
    private readonly BlockDecoder _blockDecoder = new();
    private readonly ulong _secondsPerSlot = blocksConfig.SecondsPerSlot;

    private static bool HasStateForBlock(IBlockchainBridge blockchainBridge, BlockHeader header)
    {
        return blockchainBridge.HasStateForBlock(header);
    }

    public ResultWrapper<ChainLevelForRpc> debug_getChainLevel(in long number)
    {
        ChainLevelInfo levelInfo = debugBridge.GetLevelInfo(number);
        return levelInfo is null
            ? ResultWrapper<ChainLevelForRpc>.Fail($"Chain level {number} does not exist", ErrorCodes.ResourceNotFound)
            : ResultWrapper<ChainLevelForRpc>.Success(new ChainLevelForRpc(levelInfo));
    }

    public ResultWrapper<int> debug_deleteChainSlice(in long startNumber, bool force = false)
    {
        return ResultWrapper<int>.Success(debugBridge.DeleteChainSlice(startNumber, force));
    }

    public ResultWrapper<GethLikeTxTrace> debug_traceTransaction(Hash256 transactionHash, GethTraceOptions? options = null)
    {
        Hash256? blockHash = debugBridge.GetTransactionBlockHash(transactionHash);
        if (blockHash is null)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail($"Cannot find block hash for transaction {transactionHash}", ErrorCodes.ResourceNotFound);
        }

        var header = TryGetHeader<GethLikeTxTrace>(blockHash!, out var headerError);
        if (headerError is not null)
        {
            return headerError;
        }

        using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;
        GethLikeTxTrace? transactionTrace = debugBridge.GetTransactionTrace(transactionHash, cancellationToken, options);
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

        var header = TryGetHeader<GethLikeTxTrace>(blockParameter, out var headerError);
        if (headerError is not null)
        {
            return headerError;
        }

        // default to previous block gas if unspecified
        call.Gas ??= header.GasLimit;

        // enforces gas cap
        call.EnsureDefaults(jsonRpcConfig.GasCap);

        Transaction tx = call.ToTransaction();
        using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;

        GethLikeTxTrace transactionTrace = debugBridge.GetTransactionTrace(tx, blockParameter, cancellationToken, options);
        if (transactionTrace is null)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail($"Cannot find transactionTrace for hash: {tx.Hash}", ErrorCodes.ResourceNotFound);
        }

        if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceTransaction)} request {tx.Hash}, result: trace");
        return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
    }

    public ResultWrapper<GethLikeTxTrace> debug_traceTransactionByBlockhashAndIndex(Hash256 blockhash, int index, GethTraceOptions options = null)
    {
        var header = TryGetHeader<GethLikeTxTrace>(blockhash, out var headerError);
        if (headerError is not null)
        {
            return headerError;
        }

        using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;
        var transactionTrace = debugBridge.GetTransactionTrace(blockhash, index, cancellationToken, options);
        if (transactionTrace is null)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail($"Cannot find transactionTrace {blockhash}", ErrorCodes.ResourceNotFound);
        }

        if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceTransactionByBlockhashAndIndex)} request {blockhash}, result: trace");
        return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
    }

    public ResultWrapper<GethLikeTxTrace> debug_traceTransactionByBlockAndIndex(BlockParameter blockParameter, int index, GethTraceOptions options = null)
    {
        var header = TryGetHeader<GethLikeTxTrace>(blockParameter, out var headerError);
        if (headerError is not null)
        {
            return headerError;
        }

        using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;
        long? blockNo = blockParameter.BlockNumber;
        if (!blockNo.HasValue)
        {
            throw new InvalidDataException("Block number value incorrect");
        }

        var transactionTrace = debugBridge.GetTransactionTrace(blockNo.Value, index, cancellationToken, options);
        if (transactionTrace is null)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail($"Cannot find transactionTrace {blockNo}", ErrorCodes.ResourceNotFound);
        }

        if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceTransactionByBlockAndIndex)} request {blockNo}, result: trace");
        return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
    }

    public ResultWrapper<GethLikeTxTrace> debug_traceTransactionInBlockByHash(byte[] blockRlp, Hash256 transactionHash, GethTraceOptions options = null)
    {
        var block = TryGetBlock<GethLikeTxTrace>(new Rlp(blockRlp), out var blockError);
        if (blockError is not null)
        {
            return blockError;
        }

        using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;
        var transactionTrace = debugBridge.GetTransactionTrace(block, transactionHash, cancellationToken, options);
        if (transactionTrace is null)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail($"Trace is null for RLP {blockRlp.ToHexString()} and transactionTrace hash {transactionHash}", ErrorCodes.ResourceNotFound);
        }

        return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
    }

    public ResultWrapper<GethLikeTxTrace> debug_traceTransactionInBlockByIndex(byte[] blockRlp, int txIndex, GethTraceOptions options = null)
    {
        var block = TryGetBlock<GethLikeTxTrace>(new Rlp(blockRlp), out var blockError);
        if (blockError is not null)
        {
            return blockError;
        }

        using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;
        var blockTrace = debugBridge.GetBlockTrace(block, cancellationToken, options);
        var transactionTrace = blockTrace?.ElementAtOrDefault(txIndex);
        if (transactionTrace is null)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail($"Trace is null for RLP {blockRlp.ToHexString()} and transaction index {txIndex}", ErrorCodes.ResourceNotFound);
        }

        return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
    }

    public async Task<ResultWrapper<bool>> debug_migrateReceipts(long from, long to) =>
        ResultWrapper<bool>.Success(await debugBridge.MigrateReceipts(from, to));

    public Task<ResultWrapper<bool>> debug_insertReceipts(BlockParameter blockParameter, ReceiptForRpc[] receiptForRpc)
    {
        debugBridge.InsertReceipts(blockParameter, receiptForRpc.Select(static r => r.ToReceipt()).ToArray());
        return Task.FromResult(ResultWrapper<bool>.Success(true));
    }

    public ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>> debug_traceBlock(byte[] blockRlp, GethTraceOptions options = null)
    {
        var block = TryGetBlock<IReadOnlyCollection<GethLikeTxTrace>>(new Rlp(blockRlp), out var blockError);
        if (blockError is not null)
        {
            return blockError;
        }

        using CancellationTokenSource? timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;
        try
        {
            var blockTrace = debugBridge.GetBlockTrace(block, cancellationToken, options);

            if (blockTrace is null)
                return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Trace is null for RLP {blockRlp.ToHexString()}", ErrorCodes.ResourceNotFound);

            if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceBlock)} request {blockRlp.ToHexString()}, result: {blockTrace}");

            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Success(blockTrace);
        }
        catch (RlpException)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Error decoding block RLP: {blockRlp.ToHexString()}", ErrorCodes.InvalidInput);
        }
        catch (ArgumentNullException)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Couldn't find any block", ErrorCodes.InvalidInput);
        }
    }

    public ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>> debug_traceBlockByNumber(BlockParameter blockNumber, GethTraceOptions options = null)
    {
        var header = TryGetHeader<IReadOnlyCollection<GethLikeTxTrace>>(blockNumber, out var headerError);
        if (headerError is not null)
        {
            return headerError;
        }

        using CancellationTokenSource? timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;

        IReadOnlyCollection<GethLikeTxTrace>? blockTrace = debugBridge.GetBlockTrace(blockNumber, cancellationToken, options);

        try
        {
            if (blockTrace is null)
                return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Trace is null for block {blockNumber}", ErrorCodes.ResourceNotFound);

            if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceBlockByNumber)} request {blockNumber}, result: {blockTrace}");

            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Success(blockTrace);
        }
        catch (ArgumentNullException)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Trace is null for block {blockNumber}", ErrorCodes.InvalidInput);
        }
    }

    public ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>> debug_traceBlockByHash(Hash256 blockHash, GethTraceOptions options = null)
    {
        var header = TryGetHeader<IReadOnlyCollection<GethLikeTxTrace>>(blockHash, out var headerError);
        if (headerError is not null)
        {
            return headerError;
        }

        using CancellationTokenSource? timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;
        IReadOnlyCollection<GethLikeTxTrace>? blockTrace = debugBridge.GetBlockTrace(new BlockParameter(blockHash), cancellationToken, options);

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
        byte[] rlp = debugBridge.GetBlockRlp(new BlockParameter(blockNumber));
        if (rlp is null)
        {
            return ResultWrapper<byte[]>.Fail($"Block {blockNumber} was not found", ErrorCodes.ResourceNotFound);
        }

        return ResultWrapper<byte[]>.Success(rlp);
    }

    public ResultWrapper<byte[]> debug_getBlockRlpByHash(Hash256 hash)
    {
        byte[] rlp = debugBridge.GetBlockRlp(new BlockParameter(hash));
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
        var dbValue = debugBridge.GetDbValue(dbName, key);
        return ResultWrapper<byte[]>.Success(dbValue);
    }

    public ResultWrapper<object> debug_getConfigValue(string category, string name)
    {
        var configValue = debugBridge.GetConfigValue(category, name);
        return ResultWrapper<object>.Success(configValue);
    }

    public ResultWrapper<bool> debug_resetHead(Hash256 blockHash)
    {
        debugBridge.UpdateHeadBlock(blockHash);
        return ResultWrapper<bool>.Success(true);
    }

    public ResultWrapper<string?> debug_getRawTransaction(Hash256 transactionHash)
    {
        Transaction? transaction = debugBridge.GetTransactionFromHash(transactionHash);
        if (transaction is null)
        {
            return ResultWrapper<string?>.Fail($"Transaction {transactionHash} was not found", ErrorCodes.ResourceNotFound);
        }

        RlpBehaviors encodingSettings = RlpBehaviors.SkipTypedWrapping | (transaction.IsInMempoolForm() ? RlpBehaviors.InMempoolForm : RlpBehaviors.None);

        using NettyRlpStream stream = TxDecoder.Instance.EncodeToNewNettyStream(transaction, encodingSettings);
        return ResultWrapper<string?>.Success(stream.AsSpan().ToHexString(false));
    }

    public ResultWrapper<byte[][]> debug_getRawReceipts(BlockParameter blockParameter)
    {
        TxReceipt[] receipts = debugBridge.GetReceiptsForBlock(blockParameter);
        if (receipts is null)
        {
            return ResultWrapper<byte[][]>.Fail($"Receipts are not found for block {blockParameter}", ErrorCodes.ResourceNotFound);
        }

        if (receipts.Length == 0)
        {
            return ResultWrapper<byte[][]>.Success([]);
        }
        RlpBehaviors behavior =
            (specProvider.GetReceiptSpec(receipts[0].BlockNumber).IsEip658Enabled ?
                RlpBehaviors.Eip658Receipts : RlpBehaviors.None) | RlpBehaviors.SkipTypedWrapping;
        var rlp = receipts.Select(tx => Rlp.Encode(tx, behavior).Bytes);
        return ResultWrapper<byte[][]>.Success(rlp.ToArray());
    }

    public ResultWrapper<byte[]> debug_getRawBlock(BlockParameter blockParameter)
    {
        var blockRLP = debugBridge.GetBlockRlp(blockParameter);
        if (blockRLP is null)
        {
            return ResultWrapper<byte[]>.Fail($"Block {blockParameter} was not found", ErrorCodes.ResourceNotFound);
        }
        return ResultWrapper<byte[]>.Success(blockRLP);
    }

    public ResultWrapper<byte[]> debug_getRawHeader(BlockParameter blockParameter)
    {
        var block = debugBridge.GetBlock(blockParameter);
        if (block is null)
        {
            return ResultWrapper<byte[]>.Fail($"Block {blockParameter} was not found", ErrorCodes.ResourceNotFound);
        }
        Rlp rlp = Rlp.Encode<BlockHeader>(block.Header);
        return ResultWrapper<byte[]>.Success(rlp.Bytes);
    }

    public Task<ResultWrapper<SyncReportSymmary>> debug_getSyncStage()
    {
        return ResultWrapper<SyncReportSymmary>.Success(debugBridge.GetCurrentSyncStage());
    }

    public ResultWrapper<IEnumerable<string>> debug_standardTraceBlockToFile(Hash256 blockHash, GethTraceOptions options = null)
    {
        using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;

        IEnumerable<string>? files = debugBridge.TraceBlockToFile(blockHash, cancellationToken, options);

        if (_logger.IsTrace) _logger.Trace($"{nameof(debug_standardTraceBlockToFile)} request {blockHash}, result: {files}");

        return ResultWrapper<IEnumerable<string>>.Success(files);
    }

    public ResultWrapper<IEnumerable<string>> debug_standardTraceBadBlockToFile(Hash256 blockHash, GethTraceOptions options = null)
    {
        using CancellationTokenSource cancellationTokenSource = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = cancellationTokenSource.Token;

        IEnumerable<string>? files = debugBridge.TraceBadBlockToFile(blockHash, cancellationToken, options);

        if (_logger.IsTrace) _logger.Trace($"{nameof(debug_standardTraceBadBlockToFile)} request {blockHash}, result: {files}");

        return ResultWrapper<IEnumerable<string>>.Success(files);
    }

    public ResultWrapper<IEnumerable<BadBlock>> debug_getBadBlocks()
    {
        IEnumerable<BadBlock> badBlocks = debugBridge.GetBadBlocks().Select(block => new BadBlock(block, true, specProvider, _blockDecoder));
        return ResultWrapper<IEnumerable<BadBlock>>.Success(badBlocks);
    }

    private CancellationTokenSource BuildTimeoutCancellationTokenSource() =>
        jsonRpcConfig.BuildTimeoutCancellationToken();

    public ResultWrapper<IReadOnlyList<SimulateBlockResult<GethLikeTxTrace>>> debug_simulateV1(
        SimulatePayload<TransactionForRpc> payload, BlockParameter? blockParameter = null, GethTraceOptions? options = null)
    {
        return new SimulateTxExecutor<GethLikeTxTrace>(blockchainBridge, blockFinder, jsonRpcConfig, new GethStyleSimulateBlockTracerFactory(options: options ?? GethTraceOptions.Default), _secondsPerSlot)
            .Execute(payload, blockParameter);
    }

    private static ResultWrapper<TResult> GetFailureResult<TResult, TSearch>(SearchResult<TSearch> searchResult, bool isTemporary)
        where TSearch : class =>
        ResultWrapper<TResult>.Fail(searchResult, isTemporary && searchResult.ErrorCode == ErrorCodes.ResourceNotFound);

    private static ResultWrapper<TResult> GetStateFailureResult<TResult>(BlockHeader header) =>
        ResultWrapper<TResult>.Fail($"No state available for block {header.ToString(BlockHeader.Format.FullHashAndNumber)}", ErrorCodes.ResourceUnavailable);

    private static ResultWrapper<TResult> GetRlpDecodingFailureResult<TResult>(Rlp blockRlp) =>
        ResultWrapper<TResult>.Fail($"Error decoding block RLP: {blockRlp.Bytes.ToHexString()}", ErrorCodes.InvalidInput);

    private BlockHeader? TryGetHeader<TResult>(BlockParameter blockParameter, out ResultWrapper<TResult>? error)
    {
        SearchResult<BlockHeader> searchResult = blockFinder.SearchForHeader(blockParameter);
        BlockHeader? header = searchResult.Object;

        if (searchResult.IsError)
        {
            error = GetFailureResult<TResult, BlockHeader>(searchResult, debugBridge.HaveNotSyncedHeadersYet());
            return null;
        }
        if (!HasStateForBlock(blockchainBridge, header))
        {
            error = GetStateFailureResult<TResult>(header);
            return null;
        }

        error = default!;
        return header;
    }

    private BlockHeader? TryGetHeader<TResult>(Hash256 blockHash, out ResultWrapper<TResult>? error)
    {
        BlockHeader? header = blockFinder.FindHeader(blockHash);

        if (header is null)
        {
            error = GetFailureResult<TResult, BlockHeader>(
                new SearchResult<BlockHeader>($"Cannot find header for block hash: {blockHash}", ErrorCodes.ResourceNotFound),
                debugBridge.HaveNotSyncedHeadersYet());
            return null;
        }
        if (!HasStateForBlock(blockchainBridge, header))
        {
            error = GetStateFailureResult<TResult>(header);
            return null;
        }

        error = default!;
        return header;
    }

    private Block? TryGetBlock<TResult>(Rlp blockRlp, out ResultWrapper<TResult>? error)
    {
        Block? block;

        try
        {
            block = _blockDecoder.Decode(blockRlp.Bytes);
            if (block is null)
            {
                error = GetRlpDecodingFailureResult<TResult>(blockRlp);
                return null;
            }

            if (block.TotalDifficulty is null)
            {
                block.Header.TotalDifficulty = 1;
            }
        }
        catch (RlpException)
        {
            error = GetRlpDecodingFailureResult<TResult>(blockRlp);
            return null;
        }

        if (!HasStateForBlock(blockchainBridge, block.Header))
        {
            error = GetStateFailureResult<TResult>(block.Header);
            return null;
        }

        error = default!;
        return block;
    }
}
