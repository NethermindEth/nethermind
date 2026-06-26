// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Consensus.Tracing;
using Nethermind.JsonRpc.Data;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Synchronization.Reporting;
using System.Collections.Generic;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.State;
using Nethermind.Core.Specs;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Config;
using Nethermind.TxPool;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Facade;
using Nethermind.Facade.Simulate;
using Nethermind.Consensus.Stateless;

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
    private readonly ILogger _logger = logManager.GetClassLogger<DebugRpcModule>();
    private static readonly TxDecoder TxRlpDecoder = TxDecoder.Instance;
    private readonly BlockDecoder _blockDecoder = new();
    private readonly ulong _secondsPerSlot = blocksConfig.SecondsPerSlot;

    public ResultWrapper<ChainLevelForRpc> debug_getChainLevel(in long number)
    {
        if (number < 0)
        {
            return ResultWrapper<ChainLevelForRpc>.Fail($"Chain level must be non-negative (got {number})", ErrorCodes.InvalidParams);
        }
        ChainLevelInfo? levelInfo = debugBridge.GetLevelInfo((ulong)number);
        return levelInfo is null
            ? ResultWrapper<ChainLevelForRpc>.Fail($"Chain level {number} does not exist", ErrorCodes.ResourceNotFound)
            : ResultWrapper<ChainLevelForRpc>.Success(new ChainLevelForRpc(levelInfo));
    }

    public ResultWrapper<int> debug_deleteChainSlice(in long startNumber, bool force = false) =>
        startNumber < 0
            ? ResultWrapper<int>.Fail($"startNumber must be non-negative (got {startNumber})", ErrorCodes.InvalidParams)
            : ResultWrapper<int>.Success(debugBridge.DeleteChainSlice((ulong)startNumber, force));

    public ResultWrapper<GethLikeTxTrace> debug_traceTransaction(Hash256 transactionHash, GethTraceOptions? options = null)
    {
        Hash256? blockHash = debugBridge.GetTransactionBlockHash(transactionHash);
        if (blockHash is null)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail($"Cannot find block hash for transaction {transactionHash}", ErrorCodes.ResourceNotFound);
        }

        TryGetHeaderAndCheckState(blockHash!, out ResultWrapper<GethLikeTxTrace>? headerError);
        if (headerError is not null)
        {
            return headerError;
        }

        if (CanStreamStructLogs(options))
        {
            GethTraceOptions effective = options ?? GethTraceOptions.Default;
            return ResultWrapper<GethLikeTxTrace>.Success(BuildStreamingResult(
                (writer, pipeWriter, token) =>
                    debugBridge.GetTransactionTrace(transactionHash, token, effective, writer, pipeWriter)));
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

        BlockHeader? header = TryGetHeaderAndCheckState(blockParameter, out ResultWrapper<GethLikeTxTrace>? headerError);
        if (headerError is not null)
        {
            return headerError;
        }

        Result<Transaction> txResult = call.ToTransaction(validateUserInput: true, gasCap: jsonRpcConfig.GasCap, spec: specProvider.GetSpec(header!));
        if (!txResult.Success(out Transaction? tx, out string? error))
        {
            return ResultWrapper<GethLikeTxTrace>.Fail(error, ErrorCodes.InvalidInput);
        }

        if (CanStreamStructLogs(options))
        {
            GethTraceOptions effective = options ?? GethTraceOptions.Default;
            return ResultWrapper<GethLikeTxTrace>.Success(BuildStreamingResult(
                (writer, pipeWriter, token) =>
                    debugBridge.GetTransactionTrace(tx, blockParameter, token, effective, writer, pipeWriter)));
        }

        using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;

        GethLikeTxTrace? transactionTrace;
        try
        {
            transactionTrace = debugBridge.GetTransactionTrace(tx, blockParameter, cancellationToken, options);
        }
        catch (InsufficientBalanceException ex)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail(ErrorWrapper.DebugTrace(ex.Message), ErrorCodes.InvalidInput);
        }

        if (transactionTrace is null)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail($"Cannot find transactionTrace for hash: {tx.Hash}", ErrorCodes.ResourceNotFound);
        }

        if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceTransaction)} request {tx.Hash}, result: trace");
        return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
    }

    private bool CanStreamStructLogs(GethTraceOptions? options)
    {
        if (!string.IsNullOrEmpty(options?.Tracer)) return false;
        return options?.StreamMode ?? jsonRpcConfig.EnableTracingStreamMode;
    }

    private GethLikeTxTraceStreamingSingleResult BuildStreamingResult(
        Func<Utf8JsonWriter, PipeWriter?, CancellationToken, GethLikeTxTrace?> runTrace)
    {
        CancellationTokenSource timeoutCts = BuildTimeoutCancellationTokenSource();
        try
        {
            return new GethLikeTxTraceStreamingSingleResult(runTrace, timeoutCts, _logger);
        }
        catch
        {
            timeoutCts.Dispose();
            throw;
        }
    }

    private GethLikeTxTraceStreamingBlockResult BuildStreamingBlockResult(
        Action<Utf8JsonWriter, PipeWriter?, CancellationToken> runBlockTrace)
    {
        CancellationTokenSource timeoutCts = BuildTimeoutCancellationTokenSource();
        try
        {
            return new GethLikeTxTraceStreamingBlockResult(runBlockTrace, timeoutCts, _logger);
        }
        catch
        {
            timeoutCts.Dispose();
            throw;
        }
    }

    public ResultWrapper<GethLikeTxTrace> debug_traceTransactionByBlockhashAndIndex(Hash256 blockhash, int index, GethTraceOptions? options = null)
    {
        TryGetHeaderAndCheckState(blockhash, out ResultWrapper<GethLikeTxTrace>? headerError);
        if (headerError is not null)
        {
            return headerError;
        }

        if (CanStreamStructLogs(options))
        {
            GethTraceOptions effective = options ?? GethTraceOptions.Default;
            return ResultWrapper<GethLikeTxTrace>.Success(BuildStreamingResult(
                (writer, pipeWriter, token) =>
                    debugBridge.GetTransactionTrace(blockhash, index, token, effective, writer, pipeWriter)));
        }

        using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;
        GethLikeTxTrace? transactionTrace = debugBridge.GetTransactionTrace(blockhash, index, cancellationToken, options);
        if (transactionTrace is null)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail($"Cannot find transactionTrace {blockhash}", ErrorCodes.ResourceNotFound);
        }

        if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceTransactionByBlockhashAndIndex)} request {blockhash}, result: trace");
        return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
    }

    public ResultWrapper<GethLikeTxTrace> debug_traceTransactionByBlockAndIndex(BlockParameter blockParameter, int index, GethTraceOptions? options = null)
    {
        TryGetHeaderAndCheckState(blockParameter, out ResultWrapper<GethLikeTxTrace>? headerError);
        if (headerError is not null)
        {
            return headerError;
        }

        ulong? blockNo = blockParameter.BlockNumber;
        if (!blockNo.HasValue)
        {
            throw new InvalidDataException("Block number value incorrect");
        }

        if (CanStreamStructLogs(options))
        {
            GethTraceOptions effective = options ?? GethTraceOptions.Default;
            ulong resolvedBlockNo = blockNo.Value;
            return ResultWrapper<GethLikeTxTrace>.Success(BuildStreamingResult(
                (writer, pipeWriter, token) =>
                    debugBridge.GetTransactionTrace(resolvedBlockNo, index, token, effective, writer, pipeWriter)));
        }

        using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;

        GethLikeTxTrace? transactionTrace = debugBridge.GetTransactionTrace(blockNo.Value, index, cancellationToken, options);
        if (transactionTrace is null)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail($"Cannot find transactionTrace {blockNo}", ErrorCodes.ResourceNotFound);
        }

        if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceTransactionByBlockAndIndex)} request {blockNo}, result: trace");
        return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
    }

    public ResultWrapper<GethLikeTxTrace> debug_traceTransactionInBlockByHash(byte[] blockRlp, Hash256 transactionHash, GethTraceOptions? options = null)
    {
        Block? block = TryGetBlockAndCheckState(new Rlp(blockRlp), out ResultWrapper<GethLikeTxTrace>? blockError);
        if (blockError is not null)
        {
            return blockError;
        }

        if (CanStreamStructLogs(options))
        {
            GethTraceOptions effective = options ?? GethTraceOptions.Default;
            return ResultWrapper<GethLikeTxTrace>.Success(BuildStreamingResult(
                (writer, pipeWriter, token) =>
                    debugBridge.GetTransactionTrace(block!, transactionHash, token, effective, writer, pipeWriter)));
        }

        using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;
        GethLikeTxTrace? transactionTrace = debugBridge.GetTransactionTrace(block!, transactionHash, cancellationToken, options);
        if (transactionTrace is null)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail($"Trace is null for RLP {blockRlp.ToHexString()} and transactionTrace hash {transactionHash}", ErrorCodes.ResourceNotFound);
        }

        return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
    }

    public ResultWrapper<GethLikeTxTrace> debug_traceTransactionInBlockByIndex(byte[] blockRlp, int txIndex, GethTraceOptions? options = null)
    {
        Block? block = TryGetBlockAndCheckState(new Rlp(blockRlp), out ResultWrapper<GethLikeTxTrace>? blockError);
        if (blockError is not null)
        {
            return blockError;
        }

        if (CanStreamStructLogs(options))
        {
            if (txIndex < 0 || txIndex >= block!.Transactions.Length)
            {
                return ResultWrapper<GethLikeTxTrace>.Fail($"Trace is null for RLP {blockRlp.ToHexString()} and transaction index {txIndex}", ErrorCodes.ResourceNotFound);
            }

            Hash256? targetTxHash = block.Transactions[txIndex].Hash;
            GethTraceOptions effective = options ?? GethTraceOptions.Default;
            return ResultWrapper<GethLikeTxTrace>.Success(BuildStreamingResult(
                (writer, pipeWriter, token) =>
                    debugBridge.GetTransactionTrace(block, targetTxHash!, token, effective, writer, pipeWriter)));
        }

        using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;
        IReadOnlyCollection<GethLikeTxTrace>? blockTrace = debugBridge.GetBlockTrace(block!, cancellationToken, options);
        GethLikeTxTrace? transactionTrace = blockTrace?.ElementAtOrDefault(txIndex);
        if (transactionTrace is null)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail($"Trace is null for RLP {blockRlp.ToHexString()} and transaction index {txIndex}", ErrorCodes.ResourceNotFound);
        }

        return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
    }

    public async Task<ResultWrapper<bool>> debug_migrateReceipts(ulong from, ulong to) =>
        ResultWrapper<bool>.Success(await debugBridge.MigrateReceipts(from, to));

    public Task<ResultWrapper<bool>> debug_insertReceipts(BlockParameter blockParameter, ReceiptForRpc[] receiptForRpc)
    {
        debugBridge.InsertReceipts(blockParameter, receiptForRpc.Select(static r => r.ToReceipt()).ToArray());
        return Task.FromResult(ResultWrapper<bool>.Success(true));
    }

    public ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>> debug_traceBlock(byte[] blockRlp, GethTraceOptions? options = null)
    {
        Block? block = TryGetBlockAndCheckState(new Rlp(blockRlp), out ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>? blockError);
        if (blockError is not null)
        {
            return blockError;
        }

        if (CanStreamStructLogs(options))
        {
            GethTraceOptions effective = options ?? GethTraceOptions.Default;
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Success(BuildStreamingBlockResult(
                (writer, pipeWriter, token) =>
                    debugBridge.GetBlockTrace(block!, token, effective, writer, pipeWriter)));
        }

        using CancellationTokenSource? timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;
        try
        {
            IReadOnlyCollection<GethLikeTxTrace>? blockTrace = debugBridge.GetBlockTrace(block!, cancellationToken, options);

            if (blockTrace is null)
                return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Trace is null for RLP {blockRlp.ToHexString()}", ErrorCodes.ResourceNotFound);

            if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceBlock)} request {blockRlp.ToHexString()}, result: {blockTrace}");

            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Success(new GethLikeTxTraceStreamingResult(blockTrace));
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

    public ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>> debug_traceBlockByNumber(BlockParameter blockNumber, GethTraceOptions? options = null)
    {
        BlockHeader? header = TryGetHeaderAndCheckState(blockNumber, out ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>? headerError);
        if (headerError is not null)
        {
            return headerError;
        }
        if (header is null)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Cannot find header for block {blockNumber}", ErrorCodes.ResourceNotFound);
        }

        if (header.Number == 0)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail("genesis is not traceable", ErrorCodes.InvalidInput);
        }

        if (CanStreamStructLogs(options))
        {
            Block? resolvedBlock = blockFinder.FindBlock(blockNumber);
            if (resolvedBlock is null)
            {
                return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Block body not found for {blockNumber}", ErrorCodes.ResourceNotFound);
            }

            GethTraceOptions effective = options ?? GethTraceOptions.Default;
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Success(BuildStreamingBlockResult(
                (writer, pipeWriter, token) =>
                    debugBridge.GetBlockTrace(resolvedBlock, token, effective, writer, pipeWriter)));
        }

        using CancellationTokenSource? timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;

        try
        {
            IReadOnlyCollection<GethLikeTxTrace>? blockTrace = debugBridge.GetBlockTrace(blockNumber, cancellationToken, options);

            if (blockTrace is null)
                return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Trace is null for block {blockNumber}", ErrorCodes.ResourceNotFound);

            if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceBlockByNumber)} request {blockNumber}, result: {blockTrace}");

            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Success(new GethLikeTxTraceStreamingResult(blockTrace));
        }
        catch (ArgumentNullException)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Trace is null for block {blockNumber}", ErrorCodes.InvalidInput);
        }
    }

    public ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>> debug_traceBlockByHash(Hash256 blockHash, GethTraceOptions? options = null)
    {
        BlockHeader? header = TryGetHeaderAndCheckState(blockHash, out ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>? headerError);
        if (headerError is not null)
        {
            return headerError;
        }
        if (header is null)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Cannot find header for block hash: {blockHash}", ErrorCodes.ResourceNotFound);
        }

        if (header.Number == 0)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail("genesis is not traceable", ErrorCodes.InvalidInput);
        }

        if (CanStreamStructLogs(options))
        {
            Block? resolvedBlock = blockFinder.FindBlock(blockHash);
            if (resolvedBlock is null)
            {
                return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Block body not found for {blockHash}", ErrorCodes.ResourceNotFound);
            }

            GethTraceOptions effective = options ?? GethTraceOptions.Default;
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Success(BuildStreamingBlockResult(
                (writer, pipeWriter, token) =>
                    debugBridge.GetBlockTrace(resolvedBlock, token, effective, writer, pipeWriter)));
        }

        using CancellationTokenSource? timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;

        try
        {
            IReadOnlyCollection<GethLikeTxTrace>? blockTrace = debugBridge.GetBlockTrace(new BlockParameter(blockHash), cancellationToken, options);

            if (blockTrace is null)
                return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Trace is null for block {blockHash}", ErrorCodes.ResourceNotFound);

            if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceBlockByHash)} request {blockHash}, result: {blockTrace}");

            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Success(new GethLikeTxTraceStreamingResult(blockTrace));
        }
        catch (ArgumentNullException)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Trace is null for block {blockHash}", ErrorCodes.InvalidInput);
        }
    }

    public ResultWrapper<IReadOnlyCollection<Hash256>> debug_intermediateRoots(Hash256 blockHash, GethTraceOptions? options = null)
    {
        TryGetHeaderAndCheckState<IReadOnlyCollection<Hash256>>(blockHash, out ResultWrapper<IReadOnlyCollection<Hash256>>? headerError);
        if (headerError is not null)
        {
            return headerError;
        }

        using CancellationTokenSource? timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;

        IReadOnlyCollection<Hash256> roots;
        try
        {
            roots = debugBridge.GetBlockIntermediateRoots(blockHash, cancellationToken, options);
        }
        catch (GenesisNotTraceableException e)
        {
            return ResultWrapper<IReadOnlyCollection<Hash256>>.Fail(e.Message, ErrorCodes.InvalidInput);
        }

        if (_logger.IsTrace) _logger.Trace($"{nameof(debug_intermediateRoots)} request {blockHash}, roots: {roots.Count}");

        return ResultWrapper<IReadOnlyCollection<Hash256>>.Success(roots);
    }

    public ResultWrapper<GethLikeTxTrace[]> debug_traceBlockFromFile(string fileName, GethTraceOptions? options = null) => throw new NotImplementedException();

    public ResultWrapper<object> debug_dumpBlock(BlockParameter blockParameter) => throw new NotImplementedException();

    public ResultWrapper<GcStats> debug_gcStats() => throw new NotImplementedException();

    public ResultWrapper<byte[]> debug_getBlockRlp(ulong blockNumber) =>
        GetBlockRlpOrFail(new BlockParameter(blockNumber));

    public ResultWrapper<byte[]> debug_getBlockRlpByHash(Hash256 hash) =>
        GetBlockRlpOrFail(new BlockParameter(hash));

    public ResultWrapper<MemStats> debug_memStats(BlockParameter blockParameter) => throw new NotImplementedException();

    public ResultWrapper<byte[]> debug_seedHash(BlockParameter blockParameter) => throw new NotImplementedException();

    public ResultWrapper<bool> debug_setHead(BlockParameter blockParameter) => throw new NotImplementedException();

    public ResultWrapper<byte[]?> debug_getFromDb(string dbName, byte[] key)
    {
        byte[]? dbValue = debugBridge.GetDbValue(dbName, key);
        return ResultWrapper<byte[]?>.Success(dbValue);
    }

    public ResultWrapper<object?> debug_getConfigValue(string category, string name)
    {
        object? configValue = debugBridge.GetConfigValue(category, name);
        return ResultWrapper<object?>.Success(configValue);
    }

    public ResultWrapper<bool> debug_resetHead(Hash256 blockHash)
    {
        debugBridge.UpdateHeadBlock(blockHash);
        return ResultWrapper<bool>.Success(true);
    }

    public ResultWrapper<ArrayPoolList<byte>?> debug_getRawTransaction(Hash256 transactionHash)
    {
        Transaction? transaction = debugBridge.GetTransactionFromHash(transactionHash);
        if (transaction is null)
        {
            return ResultWrapper<ArrayPoolList<byte>?>.Success(null);
        }

        RlpBehaviors encodingSettings = RlpBehaviors.SkipTypedWrapping | (transaction.IsInMempoolForm() ? RlpBehaviors.InMempoolForm : RlpBehaviors.None);
        return ResultWrapper<ArrayPoolList<byte>?>.Success(TxRlpDecoder.EncodeToArrayPoolList(transaction, encodingSettings));
    }

    public ResultWrapper<RawReceiptsResult> debug_getRawReceipts(BlockParameter blockParameter)
    {
        TxReceipt[]? receipts = debugBridge.GetReceiptsForBlock(blockParameter);
        if (receipts is null)
        {
            return ResultWrapper<RawReceiptsResult>.Fail($"Receipts are not found for block {blockParameter}", ErrorCodes.ResourceNotFound);
        }

        if (receipts.Length == 0)
        {
            return ResultWrapper<RawReceiptsResult>.Success(new RawReceiptsResult(ArrayPoolList<ArrayPoolList<byte>>.Empty()));
        }

        RlpBehaviors behavior =
            (specProvider.GetReceiptSpec(receipts[0].BlockNumber).IsEip658Enabled ?
                RlpBehaviors.Eip658Receipts : RlpBehaviors.None) | RlpBehaviors.SkipTypedWrapping;
        IRlpDecoder<TxReceipt> receiptDecoder = Rlp.GetDecoder<TxReceipt>()!;

        ArrayPoolList<ArrayPoolList<byte>> encoded = new(receipts.Length);
        try
        {
            foreach (TxReceipt receipt in receipts)
            {
                ArrayPoolList<byte> receiptRlp = receiptDecoder.EncodeToArrayPoolList(receipt, behavior);
                try
                {
                    encoded.Add(receiptRlp);
                }
                catch
                {
                    receiptRlp.Dispose();
                    throw;
                }
            }
            return ResultWrapper<RawReceiptsResult>.Success(new RawReceiptsResult(encoded));
        }
        catch
        {
            foreach (ArrayPoolList<byte> buffer in encoded)
            {
                buffer.Dispose();
            }
            encoded.Dispose();
            throw;
        }
    }

    public ResultWrapper<ArrayPoolList<byte>> debug_getRawBlock(BlockParameter blockParameter)
    {
        Block? block = debugBridge.GetBlock(blockParameter);
        return block is null
            ? ResultWrapper<ArrayPoolList<byte>>.Fail($"Block {blockParameter} was not found", ErrorCodes.ResourceNotFound)
            : ResultWrapper<ArrayPoolList<byte>>.Success(_blockDecoder.EncodeToArrayPoolList(block));
    }

    public ResultWrapper<OwnedByteMemory> debug_getRawBlockAccessList(BlockParameter blockParameter)
    {
        Block? block = debugBridge.GetBlock(blockParameter);
        if (block is null || block.BlockAccessListHash is null)
        {
            return ResultWrapper<OwnedByteMemory>.Fail("Resource not found", ErrorCodes.BlockAccessListResourceNotFound);
        }

        MemoryManager<byte>? balRlp = blockchainBridge.GetBlockAccessListRlp(block.Number, block.Hash!);

        return balRlp is null
            ? ResultWrapper<OwnedByteMemory>.Fail(ErrorMessages.PrunedHistoryUnavailable, ErrorCodes.PrunedHistoryUnavailable)
            : ResultWrapper<OwnedByteMemory>.Success(new OwnedByteMemory(balRlp));
    }

    public ResultWrapper<ArrayPoolList<byte>> debug_getRawHeader(BlockParameter blockParameter)
    {
        Block? block = debugBridge.GetBlock(blockParameter);
        return block is null
            ? ResultWrapper<ArrayPoolList<byte>>.Fail($"Block {blockParameter} was not found", ErrorCodes.ResourceNotFound)
            : ResultWrapper<ArrayPoolList<byte>>.Success(Rlp.GetDecoder<BlockHeader>()!.EncodeToArrayPoolList(block.Header));
    }

    public Task<ResultWrapper<SyncReportSummary>> debug_getSyncStage() => ResultWrapper<SyncReportSummary>.Success(debugBridge.GetCurrentSyncStage());

    public ResultWrapper<IEnumerable<string>> debug_standardTraceBlockToFile(Hash256 blockHash, GethTraceOptions? options = null)
    {
        TryGetHeaderAndCheckState(blockHash, out ResultWrapper<IEnumerable<string>>? headerError);
        if (headerError is not null)
        {
            return headerError;
        }

        using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;

        IEnumerable<string>? files = debugBridge.TraceBlockToFile(blockHash, cancellationToken, options);

        if (_logger.IsTrace) _logger.Trace($"{nameof(debug_standardTraceBlockToFile)} request {blockHash}, result: {files}");

        return ResultWrapper<IEnumerable<string>>.Success(files);
    }

    public ResultWrapper<IEnumerable<string>> debug_standardTraceBadBlockToFile(Hash256 blockHash, GethTraceOptions? options = null)
    {
        TryGetHeaderAndCheckState(blockHash, out ResultWrapper<IEnumerable<string>>? headerError);
        if (headerError is not null)
        {
            return headerError;
        }

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
        SimulatePayload<TransactionForRpc> payload, BlockParameter? blockParameter = null, GethTraceOptions? options = null) => new SimulateTxExecutor<GethLikeTxTrace>(blockchainBridge, blockFinder, jsonRpcConfig, specProvider, new GethStyleSimulateBlockTracerFactory(options: options ?? GethTraceOptions.Default), _secondsPerSlot)
            .Execute(payload, blockParameter);

    public ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>> debug_traceCallMany(TransactionBundle[] bundles, BlockParameter? blockParameter = null, GethTraceOptions? options = null)
    {
        if (bundles is null)
            return ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>>.Fail("Bundles array cannot be null", ErrorCodes.InvalidParams);

        if (bundles.Length == 0)
            return ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>>.Success(Array.Empty<GethLikeTxTrace[]>());

        for (int i = 0; i < bundles.Length; i++)
        {
            if (bundles[i]?.Transactions is null)
                return ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>>.Fail($"Bundle at index {i} has null transactions", ErrorCodes.InvalidParams);
        }

        blockParameter ??= BlockParameter.Latest;

        BlockHeader? header = TryGetHeaderAndCheckState(blockParameter, out ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>>? headerError);
        if (headerError is not null)
        {
            return headerError;
        }

        return bundles.Any(b => b.BlockOverride is not null || b.StateOverrides is not null)
            ? TraceCallManyWithOverrides(bundles, options, header!)
            : TraceCallMany(bundles, blockParameter, options, header!);
    }

    private ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>> TraceCallMany(TransactionBundle[] bundles, BlockParameter blockParameter, GethTraceOptions? options, BlockHeader header)
    {
        if (CanStreamStructLogs(options))
        {
            return ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>>.Success(BuildStreamingBundleResult(bundles, blockParameter, options));
        }

        CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
        try
        {
            IEnumerable<IEnumerable<GethLikeTxTrace>> bundleTraces = debugBridge
                .GetBundleTraces(bundles, blockParameter, jsonRpcConfig.GasCap, timeout.Token, options);

            if (_logger.IsTrace)
            {
                int totalTransactions = bundles.Sum(b => b.Transactions?.Length ?? 0);
                _logger.Trace($"{nameof(debug_traceCallMany)} completed: {bundles.Length} bundles, {totalTransactions} transactions via simple path");
            }

            return ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>>.Success(StreamBundleTraces(bundleTraces, timeout));
        }
        catch
        {
            timeout.Dispose();
            throw;
        }
    }

    private GethLikeTxTraceStreamingBundleResult BuildStreamingBundleResult(
        TransactionBundle[] bundles,
        BlockParameter blockParameter,
        GethTraceOptions? options)
    {
        CancellationTokenSource timeoutCts = BuildTimeoutCancellationTokenSource();
        try
        {
            GethTraceOptions effective = options ?? GethTraceOptions.Default;
            return new GethLikeTxTraceStreamingBundleResult(
                debugBridge,
                bundles,
                blockParameter,
                jsonRpcConfig.GasCap,
                effective,
                timeoutCts,
                _logger);
        }
        catch
        {
            timeoutCts.Dispose();
            throw;
        }
    }

    // Bind the timeout CTS lifetime to enumerator disposal so the lazy bundle pipeline
    // can keep using the cancellation token after this method returns (JSON-RPC serializes
    // the result lazily). Disposing the CTS earlier breaks downstream token consumers
    // (e.g. WaitHandle access throws ObjectDisposedException).
    private static IEnumerable<IEnumerable<GethLikeTxTrace>> StreamBundleTraces(
        IEnumerable<IEnumerable<GethLikeTxTrace>> bundleTraces,
        CancellationTokenSource cancellationTokenSource)
    {
        using (cancellationTokenSource)
        {
            foreach (IEnumerable<GethLikeTxTrace> traces in bundleTraces)
            {
                yield return traces;
            }
        }
    }

    private ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>> TraceCallManyWithOverrides(TransactionBundle[] bundles, GethTraceOptions? options, BlockHeader header)
    {
        ulong? defaultGas = jsonRpcConfig.GasCap.IsGasCapped() ? jsonRpcConfig.GasCap : null;
        foreach (TransactionBundle bundle in bundles)
        {
            foreach (TransactionForRpc call in bundle.Transactions)
            {
                if (!call.Gas.IsGasCapped())
                {
                    call.Gas = defaultGas;
                }
            }
        }

        SimulatePayload<TransactionForRpc> simulatePayload = new()
        {
            BlockStateCalls = bundles.Select(bundle => new BlockStateCall<TransactionForRpc>
            {
                BlockOverrides = bundle.BlockOverride,
                StateOverrides = bundle.StateOverrides,
                Calls = bundle.Transactions
            }).ToList()
        };

        // SimulateTxExecutor inserts filler blocks between bundles when BlockOverride.Number has gaps.
        // Pre-compute the block number each bundle targets so we can drop fillers from the result and
        // keep a 1:1 mapping to the input bundles.
        HashSet<ulong> bundleBlockNumbers = new(bundles.Length);
        ulong lastBlockNumber = header.Number;
        foreach (TransactionBundle bundle in bundles)
        {
            ulong number = bundle.BlockOverride.GetBlockNumber(lastBlockNumber);
            bundleBlockNumbers.Add(number);
            lastBlockNumber = number;
        }

        BlockParameter concreteBlockParameter = new(header.Number);

        using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();

        ResultWrapper<IReadOnlyList<SimulateBlockResult<GethLikeTxTrace>>> simulationResult =
            new SimulateTxExecutor<GethLikeTxTrace>(
                blockchainBridge,
                blockFinder,
                jsonRpcConfig,
                specProvider,
                new GethStyleSimulateBlockTracerFactory(options: options ?? GethTraceOptions.Default),
                _secondsPerSlot
            ).Execute(simulatePayload, concreteBlockParameter);

        if (simulationResult.ErrorCode != 0)
        {
            string errorMessage = simulationResult.Result ? $"Simulation failed with error code {simulationResult.ErrorCode}." : simulationResult.Result.ToString();
            if (_logger.IsWarn) _logger.Warn($"debug_traceCallMany simulation failed: Code={simulationResult.ErrorCode}, Details={errorMessage}");
            return ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>>.Fail(errorMessage, simulationResult.ErrorCode);
        }

        IEnumerable<IEnumerable<GethLikeTxTrace>> bundleTraces = simulationResult.Data
            .Where(blockResult => blockResult.Number is ulong n && bundleBlockNumbers.Contains(n))
            .Select(blockResult => blockResult.Traces);

        return ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>>.Success(bundleTraces);
    }

    private ResultWrapper<byte[]> GetBlockRlpOrFail(BlockParameter blockParameter)
    {
        byte[]? rlp = debugBridge.GetBlockRlp(blockParameter);
        return rlp is null
            ? ResultWrapper<byte[]>.Fail($"Block {blockParameter} was not found", ErrorCodes.ResourceNotFound)
            : ResultWrapper<byte[]>.Success(rlp);
    }

    private static ResultWrapper<TResult> GetFailureResult<TResult, TSearch>(SearchResult<TSearch> searchResult, bool isTemporary)
        where TSearch : class =>
        ResultWrapper<TResult>.Fail(searchResult, isTemporary && searchResult.ErrorCode == ErrorCodes.ResourceNotFound);

    private static ResultWrapper<TResult> GetStateFailureResult<TResult>(BlockHeader header) =>
        ResultWrapper<TResult>.Fail($"No state available for block {header.ToString(BlockHeader.Format.FullHashAndNumber)}", ErrorCodes.ResourceUnavailable);

    private static ResultWrapper<TResult> GetRlpDecodingFailureResult<TResult>(Rlp blockRlp) =>
        ResultWrapper<TResult>.Fail($"Error decoding block RLP: {blockRlp.Bytes.ToHexString()}", ErrorCodes.InvalidInput);

    private BlockHeader? TryGetHeaderAndCheckState<TResult>(BlockParameter blockParameter, out ResultWrapper<TResult>? error)
    {
        SearchResult<BlockHeader> searchResult = blockFinder.SearchForHeader(blockParameter);
        BlockHeader? header = searchResult.Object;

        if (searchResult.IsError)
        {
            error = GetFailureResult<TResult, BlockHeader>(searchResult, debugBridge.HaveNotSyncedHeadersYet());
            return null;
        }
        if (!blockchainBridge.HasStateForBlock(header!))
        {
            error = GetStateFailureResult<TResult>(header!);
            return null;
        }

        error = null;
        return header;
    }

    private BlockHeader? TryGetHeaderAndCheckState<TResult>(Hash256 blockHash, out ResultWrapper<TResult>? error)
    {
        BlockHeader? header = blockFinder.FindHeader(blockHash);

        if (header is null)
        {
            error = GetFailureResult<TResult, BlockHeader>(
                new SearchResult<BlockHeader>($"Cannot find header for block hash: {blockHash}", ErrorCodes.ResourceNotFound),
                debugBridge.HaveNotSyncedHeadersYet());
            return null;
        }
        if (!blockchainBridge.HasStateForBlock(header))
        {
            error = GetStateFailureResult<TResult>(header);
            return null;
        }

        error = null;
        return header;
    }

    private Block? TryGetBlockAndCheckState<TResult>(Rlp blockRlp, out ResultWrapper<TResult>? error)
    {
        Block? block;

        try
        {
            RlpReader context = new(blockRlp.Bytes);
            block = _blockDecoder.Decode(ref context);
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

        if (!blockchainBridge.HasStateForBlock(block.Header))
        {
            error = GetStateFailureResult<TResult>(block.Header);
            return null;
        }

        error = null;
        return block;
    }

    public ResultWrapper<Witness> debug_executionWitness(BlockParameter blockParameter)
    {
        Block? block = blockFinder.FindBlock(blockParameter);
        if (block is null)
        {
            return ResultWrapper<Witness>.Fail($"Unable to find block {blockParameter}", ErrorCodes.ResourceNotFound);
        }
        else if (block.Number == 0)
        {
            // Cannot generate witness for genesis block as the block itself does not contain any transaction
            // responsible for the state setup. It is the weak subjectivity starting point to trust.
            return ResultWrapper<Witness>.Fail($"Cannot generate witness for genesis block", ErrorCodes.InvalidInput);
        }

        BlockHeader? parent = blockFinder.FindHeader(block.ParentHash!);
        if (parent is null)
        {
            return ResultWrapper<Witness>.Fail($"Unable to find parent for block {blockParameter}", ErrorCodes.ResourceNotFound);
        }
        return ResultWrapper<Witness>.Success(blockchainBridge.GenerateExecutionWitness(parent, block));
    }
}
