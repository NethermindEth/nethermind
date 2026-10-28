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
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native;
using Nethermind.Consensus.Tracing;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Exceptions;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Int256;
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
using Nethermind.Facade.Eth;
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
    IBlockFinder blockFinder,
    IBlockForRpcFactory blockForRpcFactory,
    ICodeInfoRepository codeInfoRepository)
    : IDebugRpcModule
{
    private readonly ILogger _logger = logManager.GetClassLogger<DebugRpcModule>();
    private static readonly TxDecoder TxRlpDecoder = TxDecoder.Instance;
    // Registry-resolved so AuRa chains encode/decode the block seal (step + signature) correctly.
    private readonly IRlpDecoder<Block> _blockDecoder = Rlp.GetDecoderOrThrow<Block>();
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
        startNumber <= 0
            ? ResultWrapper<int>.Fail($"startNumber must be positive (got {startNumber})", ErrorCodes.InvalidParams)
            : debugBridge.DeleteChainSlice((ulong)startNumber, force);

    public ResultWrapper<GethLikeTxTrace> debug_traceTransaction(Hash256 transactionHash, GethTraceOptions? options = null)
    {
        Hash256? blockHash = debugBridge.GetTransactionBlockHash(transactionHash);
        if (blockHash is null)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail("transaction not found", ErrorCodes.ResourceNotFound);
        }

        BlockHeader? header = TryGetHeader(blockHash, out ResultWrapper<GethLikeTxTrace>? headerError);
        if (headerError is not null)
        {
            return headerError;
        }

        if (CheckTraceBaseState<GethLikeTxTrace>(header) is { } stateError)
        {
            return stateError;
        }

        if (CanStreamStructLogs(options))
        {
            return ResultWrapper<GethLikeTxTrace>.Success(BuildStreamingResult(
                (writer, pipeWriter, token) =>
                    debugBridge.GetTransactionTrace(blockHash, transactionHash, token, options, writer, pipeWriter)
                    ?? throw new RpcErrorException(ErrorCodes.ResourceNotFound, "transaction not found")));
        }

        using RentedTimeout timeout = jsonRpcConfig.RentTimeout();
        CancellationToken cancellationToken = timeout.Token;
        GethLikeTxTrace? transactionTrace;
        try
        {
            transactionTrace = debugBridge.GetTransactionTrace(blockHash, transactionHash, cancellationToken, WithBufferedLimit(options));
        }
        catch (NativeTracerConfigException exception)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail(exception.Message, ErrorCodes.InvalidInput);
        }
        catch (TraceDurationFormatException ex)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail(ex.Message, ErrorCodes.InvalidInput);
        }
        if (transactionTrace is null)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail("transaction not found", ErrorCodes.ResourceNotFound);
        }

        if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceTransaction)} request {transactionHash}, result: trace");
        return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
    }

    private Result<Transaction> ToTraceCallTransaction(TransactionForRpc call, IReleaseSpec spec)
    {
        if (call is not BlobTransactionForRpc { MaxFeePerBlobGas.IsZero: true })
            return call.ToCallTransaction(spec, jsonRpcConfig.GasCap);

        // Geth permits a zero blob fee cap for simulated calls; retain all other RPC validation.
        Result<Transaction> result = BlobTransactionForRpc.WithZeroBlobFeeCapOmitted(call).ToCallTransaction(spec, jsonRpcConfig.GasCap);
        if (result) result.Data.MaxFeePerBlobGas = UInt256.Zero;
        return result;
    }

    public ResultWrapper<GethLikeTxTrace> debug_traceCall(TransactionForRpc call, BlockParameter? blockParameter = null, GethTraceOptions? options = null)
    {
        if (options?.BlockOverrides?.Withdrawals is not null)
            return ResultWrapper<GethLikeTxTrace>.Fail(BlockOverride.WithdrawalsNotSupported, ErrorCodes.InvalidParams);

        blockParameter ??= BlockParameter.Latest;
        if (blockParameter.Type == BlockParameterType.Pending)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail("tracing on top of pending is not supported", ErrorCodes.InvalidInput);
        }

        BlockHeader? header = TryGetTraceCallHeader(blockParameter, options?.TxIndex, out ResultWrapper<GethLikeTxTrace>? headerError);
        if (headerError is not null)
        {
            return headerError;
        }

        blockParameter = new BlockParameter(header!.Hash!);

        if (options?.BlockOverrides?.BeaconRoot is not null)
            return ResultWrapper<GethLikeTxTrace>.Fail("block override \"beaconRoot\" is not supported for this RPC method", ErrorCodes.InvalidInput);

        // The call converts against the fork it runs in, which a block override can change.
        IReleaseSpec spec = specProvider.GetSpec(options?.BlockOverrides?.ApplyTo(header) ?? header);
        if (options?.StateOverrides is { } stateOverrides)
        {
            Address[] addresses = new Address[stateOverrides.Count];
            stateOverrides.Keys.CopyTo(addresses, 0);
            Array.Sort(addresses);
            foreach (Address address in addresses)
            {
                AccountOverride? accountOverride = stateOverrides[address];
                if (accountOverride?.MovePrecompileToAddress is { } destination)
                {
                    string? moveError = StateOverridesExtensions.GetPrecompileMoveError(address, destination,
                        codeInfoRepository.GetPrecompile(address, spec) is not null, stateOverrides);
                    if (moveError is not null)
                        return ResultWrapper<GethLikeTxTrace>.Fail(moveError, ErrorCodes.InvalidInput);
                }
                if (accountOverride is { State: not null, StateDiff: not null })
                    return ResultWrapper<GethLikeTxTrace>.Fail($"account {address.ToString(withEip55Checksum: true)} has both 'state' and 'stateDiff'", ErrorCodes.InvalidInput);
            }
        }

        if (call is LegacyTransactionForRpc { ChainId: { } requestedChainId } && requestedChainId != specProvider.ChainId)
            return ResultWrapper<GethLikeTxTrace>.Fail($"chainId does not match node's (have={requestedChainId}, want={specProvider.ChainId})", ErrorCodes.InvalidInput);

        Result<Transaction> txResult = ToTraceCallTransaction(call, spec);
        if (!txResult.Success(out Transaction? tx, out string? error))
        {
            return ResultWrapper<GethLikeTxTrace>.Fail(error, ErrorCodes.InvalidInput);
        }

        GethTraceOptions effective = (options ?? GethTraceOptions.Default) with
        {
            NoBaseFee = !call.ShouldSetBaseFee()
        };

        if (CanStreamStructLogs(options))
        {
            return ResultWrapper<GethLikeTxTrace>.Success(BuildStreamingResult(
                (writer, pipeWriter, token) =>
                    debugBridge.GetTransactionTrace(tx, blockParameter, token, effective, writer, pipeWriter)));
        }

        using RentedTimeout timeout = jsonRpcConfig.RentTimeout();
        CancellationToken cancellationToken = timeout.Token;

        GethLikeTxTrace? transactionTrace;
        try
        {
            transactionTrace = debugBridge.GetTransactionTrace(tx, blockParameter, cancellationToken, WithBufferedLimit(effective));
        }
        catch (InsufficientBalanceException ex)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail(ErrorWrapper.DebugTrace(ex.Message), ErrorCodes.InvalidInput);
        }
        catch (InvalidDataException ex) when (effective.Tracer == "muxTracer")
        {
            return ResultWrapper<GethLikeTxTrace>.Fail(ex.Message, ErrorCodes.InvalidInput);
        }
        catch (ArgumentException ex) when (effective.Tracer == "prestateTracer" && ex.Message == "cannot use diffMode with includeEmpty")
        {
            return ResultWrapper<GethLikeTxTrace>.Fail(ex.Message, ErrorCodes.InvalidInput);
        }

        if (transactionTrace is null)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail($"Cannot find transactionTrace for hash: {tx.Hash}", ErrorCodes.ResourceNotFound);
        }

        if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceTransaction)} request {tx.Hash}, result: trace");
        return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
    }

    private BlockHeader? TryGetTraceCallHeader(BlockParameter parameter, ulong? txIndex, out ResultWrapper<GethLikeTxTrace>? error)
    {
        if (txIndex is null) return TryGetHeaderAndCheckState(parameter, out error);
        return TryGetBlockAtTransaction(parameter, txIndex.Value, out error)?.Header;
    }

    /// <summary>
    /// Resolves the block holding transaction <paramref name="txIndex"/> and checks that its parent state is available.
    /// </summary>
    /// <remarks>An empty block accepts index 0, as geth does.</remarks>
    /// <returns>The resolved block, or <see langword="null"/> when <paramref name="error"/> is set.</returns>
    private Block? TryGetBlockAtTransaction<TResult>(BlockParameter parameter, ulong txIndex, out ResultWrapper<TResult>? error)
    {
        SearchResult<Block> search = blockFinder.SearchForBlock(parameter);
        if (search.IsError)
        {
            error = GetFailureResult<TResult, Block>(search, debugBridge.HaveNotSyncedHeadersYet());
            return null;
        }
        Block block = search.Object!;
        if (block.IsGenesis)
        {
            error = ResultWrapper<TResult>.Fail("no transaction in genesis", ErrorCodes.InvalidInput);
            return null;
        }
        _ = TryGetHeaderAndCheckState(block.ParentHash!, out error);
        if (error is not null) return null;
        if (txIndex >= (ulong)Math.Max(block.Transactions.Length, 1))
        {
            error = ResultWrapper<TResult>.Fail($"transaction index {txIndex} out of range for block {block.Hash}", ErrorCodes.InvalidInput);
            return null;
        }
        return block;
    }

    private bool CanStreamStructLogs(GethTraceOptions? options)
    {
        if (!string.IsNullOrEmpty(options?.Tracer)) return false;
        return options?.StreamMode ?? jsonRpcConfig.EnableTracingStreamMode;
    }

    /// <remarks>
    /// A malformed native config is not streamed: the buffered path fails it as a JSON-RPC error before any output,
    /// and never parses it for an empty block.
    /// </remarks>
    private bool CanStreamBlockTrace(GethTraceOptions? options)
    {
        if (!(options?.StreamMode ?? jsonRpcConfig.EnableTracingStreamMode)) return false;
        if (string.IsNullOrEmpty(options?.Tracer)) return true;
        if (options.TxHash is not null || !GethLikeNativeTracerFactory.IsNativeTracer(options.Tracer)) return false;

        try
        {
            GethLikeNativeTracerFactory.ValidateTracerConfig(options);
            return true;
        }
        catch (NativeTracerConfigException)
        {
            return false;
        }
    }

    /// <summary>
    /// Lowers the opcode log byte budget of a trace held in memory to <see cref="IJsonRpcConfig.MaxBufferedTraceLogSize"/>.
    /// </summary>
    private GethTraceOptions WithBufferedLimit(GethTraceOptions? options)
    {
        GethTraceOptions effective = options ?? GethTraceOptions.Default;
        long max = jsonRpcConfig.MaxBufferedTraceLogSize;
        return max <= 0 || (effective.Limit != 0 && effective.Limit <= max) ? effective : effective with { Limit = max };
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

    /// <remarks>
    /// The body is looked up only so a missing one fails before the stream opens. The trace goes by hash, as the
    /// <see cref="Block"/> overload never takes the indexed parallel path.
    /// </remarks>
    private ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>> StreamBlockTrace(BlockHeader header, GethTraceOptions? options, BlockParameter? selector = null, bool validateStructLogTimeout = false)
    {
        Block? resolvedBlock = blockFinder.FindBlock(header.Hash!, header.Number);
        if (resolvedBlock is null)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail(
                selector?.Type == BlockParameterType.BlockNumber ? $"block #{selector.BlockNumber} not found" : $"Block body not found for {header.Hash}",
                ErrorCodes.ResourceNotFound);
        }

        GethTraceOptions effective = options ?? GethTraceOptions.Default;
        if (validateStructLogTimeout && string.IsNullOrEmpty(effective.Tracer))
        {
            bool hasTransactionToTrace = effective.TxHash is { } txHash
                ? resolvedBlock.GetTransactionIndex(txHash.ValueHash256) >= 0
                : resolvedBlock.Transactions.Length != 0;
            if (hasTransactionToTrace && effective.ParsedTimeout.Error is { } durationError)
            {
                return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail(durationError, ErrorCodes.InvalidInput);
            }
        }
        BlockParameter blockParameter = new(header.Hash!);
        return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Success(BuildStreamingBlockResult(
            (writer, pipeWriter, token) => debugBridge.GetBlockTrace(blockParameter, token, effective, writer, pipeWriter)));
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

    public ResultWrapper<GethLikeTxTrace> debug_traceTransactionByBlockhashAndIndex(Hash256 blockhash, int index, GethTraceOptions options = null)
    {
        BlockHeader? header = TryGetHeader(blockhash, out ResultWrapper<GethLikeTxTrace>? error);
        if (error is not null)
        {
            return error;
        }

        error = CheckTraceBaseState<GethLikeTxTrace>(header!);
        return error ?? TraceTransactionAtIndex(blockhash, index, options, nameof(debug_traceTransactionByBlockhashAndIndex));
    }

    public ResultWrapper<GethLikeTxTrace> debug_traceTransactionByBlockAndIndex(BlockParameter blockParameter, int index, GethTraceOptions options = null)
    {
        BlockHeader? header = TryGetHeader(blockParameter, out ResultWrapper<GethLikeTxTrace>? error);
        if (error is not null)
        {
            return error;
        }

        error = CheckTraceBaseState<GethLikeTxTrace>(header!);
        // Trace the block that was resolved, not the canonical one at its height: a block hash parameter need not be canonical
        return error ?? TraceTransactionAtIndex(header!.Hash!, index, options, nameof(debug_traceTransactionByBlockAndIndex));
    }

    private ResultWrapper<GethLikeTxTrace> TraceTransactionAtIndex(Hash256 blockHash, int index, GethTraceOptions? options, string method)
    {
        if (CanStreamStructLogs(options))
        {
            return ResultWrapper<GethLikeTxTrace>.Success(BuildStreamingResult(
                (writer, pipeWriter, token) =>
                    debugBridge.GetTransactionTrace(blockHash, index, token, options, writer, pipeWriter)));
        }

        using RentedTimeout timeout = jsonRpcConfig.RentTimeout();
        GethLikeTxTrace? transactionTrace = debugBridge.GetTransactionTrace(blockHash, index, timeout.Token, WithBufferedLimit(options));
        if (transactionTrace is null)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail($"Cannot find transactionTrace {blockHash}", ErrorCodes.ResourceNotFound);
        }

        if (_logger.IsTrace) _logger.Trace($"{method} request {blockHash}, result: trace");
        return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
    }

    public ResultWrapper<GethLikeTxTrace> debug_traceTransactionInBlockByHash(byte[] blockRlp, Hash256 transactionHash, GethTraceOptions options = null)
    {
        Block? block = TryGetBlockAndCheckState(new Rlp(blockRlp), out ResultWrapper<GethLikeTxTrace>? blockError);
        if (blockError is not null)
        {
            return blockError;
        }

        if (CanStreamStructLogs(options))
        {
            return ResultWrapper<GethLikeTxTrace>.Success(BuildStreamingResult(
                (writer, pipeWriter, token) =>
                    debugBridge.GetTransactionTrace(block!, transactionHash, token, options, writer, pipeWriter)));
        }

        using RentedTimeout timeout = jsonRpcConfig.RentTimeout();
        CancellationToken cancellationToken = timeout.Token;
        GethLikeTxTrace? transactionTrace = debugBridge.GetTransactionTrace(block, transactionHash, cancellationToken, WithBufferedLimit(options));
        if (transactionTrace is null)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail($"Trace is null for RLP {blockRlp.ToHexString()} and transactionTrace hash {transactionHash}", ErrorCodes.ResourceNotFound);
        }

        return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
    }

    public ResultWrapper<GethLikeTxTrace> debug_traceTransactionInBlockByIndex(byte[] blockRlp, int txIndex, GethTraceOptions options = null)
    {
        Block? block = TryGetBlockAndCheckState(new Rlp(blockRlp), out ResultWrapper<GethLikeTxTrace>? blockError);
        if (blockError is not null)
        {
            return blockError;
        }

        if (txIndex < 0 || txIndex >= block!.Transactions.Length)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail($"Trace is null for RLP {blockRlp.ToHexString()} and transaction index {txIndex}", ErrorCodes.ResourceNotFound);
        }

        Hash256 targetTxHash = block.Transactions[txIndex].Hash!;
        if (CanStreamStructLogs(options))
        {
            return ResultWrapper<GethLikeTxTrace>.Success(BuildStreamingResult(
                (writer, pipeWriter, token) =>
                    debugBridge.GetTransactionTrace(block, targetTxHash, token, options, writer, pipeWriter)));
        }

        // Like the streaming path, stops after the target.
        using RentedTimeout timeout = jsonRpcConfig.RentTimeout();
        GethLikeTxTrace? transactionTrace = debugBridge.GetTransactionTrace(block, targetTxHash, timeout.Token, WithBufferedLimit(options));

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
        // EIP-8141: a frame transaction always executes at least one frame and always settles on a payer,
        // so a receipt missing either is malformed. The codec refuses both; rejecting them here answers
        // invalid params rather than an internal error.
        for (int i = 0; i < receiptForRpc.Length; i++)
        {
            ReceiptForRpc receipt = receiptForRpc[i];
            if (receipt is null)
            {
                return Task.FromResult(ResultWrapper<bool>.Fail($"Receipt at index {i} is null", ErrorCodes.InvalidParams));
            }

            if (receipt.Type == TxType.FrameTx && receipt.FrameReceipts is not { Length: > 0 })
            {
                return Task.FromResult(ResultWrapper<bool>.Fail(
                    $"Receipt at index {i} is a frame transaction receipt carrying no frame receipts",
                    ErrorCodes.InvalidParams));
            }

            if (receipt.Type == TxType.FrameTx && receipt.Payer is null)
            {
                return Task.FromResult(ResultWrapper<bool>.Fail(
                    $"Receipt at index {i} is a frame transaction receipt carrying no payer",
                    ErrorCodes.InvalidParams));
            }
        }

        debugBridge.InsertReceipts(blockParameter, receiptForRpc.Select(static r => r.ToReceipt()).ToArray());
        return Task.FromResult(ResultWrapper<bool>.Success(true));
    }

    public ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>> debug_traceBlock(byte[] blockRlp, GethTraceOptions options = null)
    {
        if (options?.ParsedTimeout.Error is not null && string.IsNullOrEmpty(options.Tracer))
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail("invalid argument 1", ErrorCodes.InvalidParams);

        Block? block = TryGetBlockAndCheckState(new Rlp(blockRlp), out ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>? blockError, requireCompleteInput: true);
        if (blockError is not null)
        {
            return blockError;
        }

        if (options?.ParsedTimeout.Error is null && CanStreamBlockTrace(options))
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Success(BuildStreamingBlockResult(
                (writer, pipeWriter, token) =>
                    debugBridge.GetBlockTrace(block!, token, options, writer, pipeWriter)));
        }

        using RentedTimeout timeout = jsonRpcConfig.RentTimeout();
        CancellationToken cancellationToken = timeout.Token;
        try
        {
            IReadOnlyCollection<GethLikeTxTrace> blockTrace = debugBridge.GetBlockTrace(block, cancellationToken, WithBufferedLimit(options));

            if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceBlock)} request {blockRlp.ToHexString()}, result: {blockTrace}");

            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Success(new GethLikeTxTraceStreamingResult(blockTrace));
        }
        catch (NativeTracerConfigException exception)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail(exception.Message, ErrorCodes.InvalidInput);
        }
        catch (TraceDurationFormatException)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail("invalid argument 1", ErrorCodes.InvalidParams);
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
        BlockHeader? header = TryGetHeader(blockNumber, out ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>? headerError);
        if (headerError is not null)
        {
            if (headerError.ErrorCode == ErrorCodes.ResourceNotFound
                && headerError.Result.Error == BlockFinderExtensions.HeaderNotFound
                && blockNumber.Type == BlockParameterType.BlockNumber)
                return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"block #{blockNumber.BlockNumber} not found", ErrorCodes.ResourceNotFound, headerError.IsTemporary);
            return headerError;
        }

        if (header.Number == 0)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail("genesis is not traceable", ErrorCodes.InvalidInput);
        }

        // HasBlock excludes cache-only bodies that FindBlock can still serve.
        if (blockNumber.Type == BlockParameterType.BlockNumber
            && !blockFinder.HasBlock(header.Number, header.Hash!)
            && blockFinder.FindBlock(header.Hash!, header.Number) is null)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"block #{blockNumber.BlockNumber} not found", ErrorCodes.ResourceNotFound);
        }

        ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>? stateError = CheckTraceBaseState<IReadOnlyCollection<GethLikeTxTrace>>(header);
        if (stateError is not null)
        {
            return stateError;
        }

        // A malformed native duration must reach the real constructor before it can fail.
        if ((options?.ParsedTimeout.Error is null || string.IsNullOrEmpty(options.Tracer)) && CanStreamBlockTrace(options))
            return StreamBlockTrace(header, options, blockNumber, validateStructLogTimeout: true);

        if (blockNumber.Type == BlockParameterType.BlockNumber && blockFinder.FindBlock(header.Hash!, header.Number) is null)
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"block #{blockNumber.BlockNumber} not found", ErrorCodes.ResourceNotFound);

        using RentedTimeout timeout = jsonRpcConfig.RentTimeout();
        CancellationToken cancellationToken = timeout.Token;

        try
        {
            IReadOnlyCollection<GethLikeTxTrace> blockTrace = debugBridge.GetBlockTrace(new BlockParameter(header.Hash!), cancellationToken,
                WithBufferedLimit(options) with { EnforceExecutionDeadline = true });

            if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceBlockByNumber)} request {blockNumber}, result: {blockTrace}");

            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Success(new GethLikeTxTraceStreamingResult(blockTrace));
        }
        catch (NativeTracerConfigException exception)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail(exception.Message, ErrorCodes.InvalidInput);
        }
        catch (ArgumentNullException)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Trace is null for block {blockNumber}", ErrorCodes.InvalidInput);
        }
        catch (TraceDurationFormatException exception)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail(exception.Message, ErrorCodes.InvalidInput);
        }
        catch (GethTraceDeadlineException exception)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail(exception.Message, ErrorCodes.InvalidInput);
        }
    }

    public ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>> debug_traceBlockByHash(Hash256 blockHash, GethTraceOptions options = null)
    {
        BlockHeader? header = TryGetHeader(blockHash, out ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>? headerError);
        if (headerError is not null)
        {
            return headerError;
        }

        if (header.Number == 0)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail("genesis is not traceable", ErrorCodes.InvalidInput);
        }

        ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>? stateError = CheckTraceBaseState<IReadOnlyCollection<GethLikeTxTrace>>(header);
        if (stateError is not null)
        {
            return stateError;
        }

        if ((options?.ParsedTimeout.Error is null || string.IsNullOrEmpty(options.Tracer)) && CanStreamBlockTrace(options))
            return StreamBlockTrace(header, options, validateStructLogTimeout: true);

        using RentedTimeout timeout = jsonRpcConfig.RentTimeout();
        CancellationToken cancellationToken = timeout.Token;

        try
        {
            IReadOnlyCollection<GethLikeTxTrace> blockTrace = debugBridge.GetBlockTrace(new BlockParameter(blockHash), cancellationToken, WithBufferedLimit(options));

            if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceBlockByHash)} request {blockHash}, result: {blockTrace}");

            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Success(new GethLikeTxTraceStreamingResult(blockTrace));
        }
        catch (NativeTracerConfigException exception)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail(exception.Message, ErrorCodes.InvalidInput);
        }
        catch (ArgumentNullException)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Trace is null for block {blockHash}", ErrorCodes.InvalidInput);
        }
        catch (TraceDurationFormatException exception)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail(exception.Message, ErrorCodes.InvalidInput);
        }
    }

    public ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>> debug_traceBadBlock(Hash256 blockHash, GethTraceOptions? options = null)
    {
        Block? block = debugBridge.GetBadBlocks().FirstOrDefault(b => b.Hash == blockHash);
        if (block is null)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"bad block {blockHash} not found", ErrorCodes.ResourceNotFound);
        }

        if (block.IsGenesis)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail("genesis is not traceable", ErrorCodes.InvalidInput);
        }

        ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>? stateError = CheckTraceBaseState<IReadOnlyCollection<GethLikeTxTrace>>(block.Header);
        if (stateError is not null)
        {
            return stateError;
        }

        if (CanStreamBlockTrace(options))
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Success(BuildStreamingBlockResult(
                (writer, pipeWriter, token) => debugBridge.GetBlockTrace(block, token, options, writer, pipeWriter)));
        }

        using RentedTimeout timeout = jsonRpcConfig.RentTimeout();
        IReadOnlyCollection<GethLikeTxTrace> blockTrace = debugBridge.GetBlockTrace(block, timeout.Token, WithBufferedLimit(options));

        if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceBadBlock)} request {blockHash}, result: {blockTrace}");

        return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Success(new GethLikeTxTraceStreamingResult(blockTrace));
    }

    public ResultWrapper<IReadOnlyCollection<Hash256>> debug_intermediateRoots(Hash256 blockHash, GethTraceOptions? options = null)
    {
        BlockHeader? header = TryGetHeader<IReadOnlyCollection<Hash256>>(blockHash, out ResultWrapper<IReadOnlyCollection<Hash256>>? headerError, searchBadBlocks: true, requireBody: true);
        headerError ??= CheckTraceBaseState<IReadOnlyCollection<Hash256>>(header!);
        if (headerError is not null)
        {
            return headerError;
        }

        using RentedTimeout timeout = jsonRpcConfig.RentTimeout();
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

    public ResultWrapper<StorageRangeResult> debug_storageRangeAt(BlockParameter blockParameter, int txIndex, Address address, byte[] keyStart, int maxResult)
    {
        if (txIndex < 0)
            return ResultWrapper<StorageRangeResult>.Fail($"transaction index {txIndex} must not be negative", ErrorCodes.InvalidParams);
        if (keyStart.Length > ValueHash256.MemorySize)
            return ResultWrapper<StorageRangeResult>.Fail($"keyStart must be at most {ValueHash256.MemorySize} bytes", ErrorCodes.InvalidParams);

        Block? block = TryGetBlockAtTransaction(blockParameter, (ulong)txIndex, out ResultWrapper<StorageRangeResult>? error);
        if (error is not null) return error;

        // A shorter start key is a prefix of the hashed keys to iterate from, as in geth's trie iterator seek.
        ValueHash256 startKey = new(keyStart.PadRight(ValueHash256.MemorySize));
        int limit = Math.Clamp(maxResult, 0, StorageRangeResult.MaxResults);

        using RentedTimeout timeout = jsonRpcConfig.RentTimeout();
        StorageRangeVisitor range;
        try
        {
            range = debugBridge.GetStorageRange(block!, txIndex, address, startKey, limit, timeout.Token);
        }
        catch (NotSupportedException e)
        {
            return ResultWrapper<StorageRangeResult>.Fail(e.Message, ErrorCodes.InvalidInput);
        }

        return ResultWrapper<StorageRangeResult>.Success(new StorageRangeResult(range));
    }

    public ResultWrapper<GethLikeTxTrace[]> debug_traceBlockFromFile(string fileName, GethTraceOptions options = null) => throw new NotImplementedException();

    public ResultWrapper<object> debug_dumpBlock(BlockParameter blockParameter) => throw new NotImplementedException();

    public ResultWrapper<GcStats> debug_gcStats() => throw new NotImplementedException();

    public ResultWrapper<byte[]> debug_getBlockRlp(ulong blockNumber) =>
        GetBlockRlpOrFail(new BlockParameter(blockNumber));

    public ResultWrapper<byte[]> debug_getBlockRlpByHash(Hash256 hash) =>
        GetBlockRlpOrFail(new BlockParameter(hash));

    public ResultWrapper<MemStats> debug_memStats(BlockParameter blockParameter) => throw new NotImplementedException();

    public ResultWrapper<byte[]> debug_seedHash(BlockParameter blockParameter) => throw new NotImplementedException();

    public ResultWrapper<bool> debug_setHead(BlockParameter blockParameter) =>
        ResultWrapper<bool>.Success(debugBridge.UpdateHeadBlock(blockParameter));

    public ResultWrapper<byte[]> debug_getFromDb(string dbName, byte[] key)
    {
        byte[] dbValue = debugBridge.GetDbValue(dbName, key);
        return ResultWrapper<byte[]>.Success(dbValue);
    }

    public ResultWrapper<object> debug_getConfigValue(string category, string name)
    {
        object configValue = debugBridge.GetConfigValue(category, name);
        return ResultWrapper<object>.Success(configValue);
    }

    public ResultWrapper<bool> debug_resetHead(Hash256 blockHash) =>
        ResultWrapper<bool>.Success(debugBridge.UpdateHeadBlock(blockHash));

    public ResultWrapper<ArrayPoolList<byte>> debug_getRawTransaction(Hash256 transactionHash)
    {
        Transaction? transaction = debugBridge.GetTransactionFromHash(transactionHash);
        if (transaction is null)
        {
            // execution-apis types the result as bytes with no not-found null, so an unknown transaction is empty bytes.
            return ResultWrapper<ArrayPoolList<byte>>.Success(ArrayPoolList<byte>.Empty());
        }

        RlpBehaviors encodingSettings = RlpBehaviors.SkipTypedWrapping | (transaction.IsInMempoolForm() ? RlpBehaviors.InMempoolForm : RlpBehaviors.None);
        return ResultWrapper<ArrayPoolList<byte>>.Success(TxRlpDecoder.EncodeToArrayPoolList(transaction, encodingSettings));
    }

    public ResultWrapper<RawReceiptsResult> debug_getRawReceipts(BlockParameter blockParameter)
    {
        Block? block;
        if (blockParameter.BlockHash is { } blockHash)
        {
            // As in geth, a hash is read as given, ignoring requireCanonical, and a block that is not stored has no receipts.
            block = blockFinder.FindBlock(blockHash, BlockTreeLookupOptions.None);

            // Deliberately unlike geth, which returns [] here: pruned history is reported rather than shown as an empty block.
            if (block is null && blockFinder.IsBlockPruned(blockParameter))
            {
                return ResultWrapper<RawReceiptsResult>.Fail($"{ErrorMessages.PrunedHistoryUnavailable} for block {blockParameter}", ErrorCodes.PrunedHistoryUnavailable);
            }
        }
        else
        {
            SearchResult<Block> searchResult = blockFinder.SearchForBlock(blockParameter);
            if (searchResult.IsError)
            {
                // Unknown block numbers yield null; pruned history and unknown safe/finalized blocks keep their error.
                return searchResult.Error == BlockFinderExtensions.HeaderNotFound
                    ? ResultWrapper<RawReceiptsResult>.Success(null)
                    : ResultWrapper<RawReceiptsResult>.Fail(searchResult);
            }

            block = searchResult.Object;
        }

        TxReceipt[] receipts = block is null ? [] : debugBridge.GetReceiptsForBlock(block);
        if (receipts.Length == 0)
        {
            return ResultWrapper<RawReceiptsResult>.Success(new RawReceiptsResult(ArrayPoolList<ArrayPoolList<byte>>.Empty()));
        }

        RlpBehaviors behavior =
            (specProvider.GetReceiptSpec(receipts[0].BlockNumber).IsEip658Enabled ?
                RlpBehaviors.Eip658Receipts : RlpBehaviors.None) | RlpBehaviors.SkipTypedWrapping;
        IRlpDecoder<TxReceipt> receiptDecoder = Rlp.GetDecoderOrThrow<TxReceipt>();

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
        // The access list is stored apart from the body, so a block whose body expired can still serve it,
        // and a pre-Amsterdam one stays -32001 rather than 4444.
        SearchResult<BlockHeader> searchResult = blockFinder.SearchForHeader(blockParameter);
        if (searchResult.IsError && searchResult.Error != BlockFinderExtensions.HeaderNotFound)
        {
            return ResultWrapper<OwnedByteMemory>.Fail(searchResult);
        }

        BlockHeader? header = searchResult.Object;
        if (header?.BlockAccessListHash is null)
        {
            return ResultWrapper<OwnedByteMemory>.Fail("Resource not found", ErrorCodes.BlockAccessListResourceNotFound);
        }

        MemoryManager<byte>? balRlp = blockchainBridge.GetBlockAccessListRlp(header.Number, header.Hash!);

        return balRlp is null
            ? ResultWrapper<OwnedByteMemory>.Fail(ErrorMessages.PrunedHistoryUnavailable, ErrorCodes.PrunedHistoryUnavailable)
            : ResultWrapper<OwnedByteMemory>.Success(new OwnedByteMemory(balRlp));
    }

    public ResultWrapper<ArrayPoolList<byte>> debug_getRawHeader(BlockParameter blockParameter)
    {
        BlockHeader? header = blockFinder.FindHeader(blockParameter);
        return header is null
            ? ResultWrapper<ArrayPoolList<byte>>.Fail($"Block {blockParameter} was not found", ErrorCodes.ResourceNotFound)
            : ResultWrapper<ArrayPoolList<byte>>.Success(Rlp.GetDecoderOrThrow<BlockHeader>().EncodeToArrayPoolList(header));
    }

    public Task<ResultWrapper<SyncReportSummary>> debug_getSyncStage() => ResultWrapper<SyncReportSummary>.Success(debugBridge.GetCurrentSyncStage());

    public ResultWrapper<IEnumerable<string>> debug_standardTraceBlockToFile(Hash256 blockHash, GethTraceOptions options = null)
    {
        BlockHeader? header = TryGetHeader<IEnumerable<string>>(blockHash, out ResultWrapper<IEnumerable<string>>? headerError, requireBody: true);
        headerError ??= CheckTraceBaseState<IEnumerable<string>>(header!);
        if (headerError is not null)
        {
            return headerError;
        }

        using RentedTimeout timeout = jsonRpcConfig.RentTimeout();
        CancellationToken cancellationToken = timeout.Token;

        IEnumerable<string>? files = debugBridge.TraceBlockToFile(blockHash, cancellationToken, options);

        if (_logger.IsTrace) _logger.Trace($"{nameof(debug_standardTraceBlockToFile)} request {blockHash}, result: {files}");

        return ResultWrapper<IEnumerable<string>>.Success(files);
    }

    public ResultWrapper<IEnumerable<string>> debug_standardTraceBadBlockToFile(Hash256 blockHash, GethTraceOptions options = null)
    {
        BlockHeader? header = TryGetHeader<IEnumerable<string>>(blockHash, out ResultWrapper<IEnumerable<string>>? headerError, badBlocksOnly: true);
        headerError ??= CheckTraceBaseState<IEnumerable<string>>(header!);
        if (headerError is not null)
        {
            return headerError;
        }

        using RentedTimeout cancellationTokenSource = jsonRpcConfig.RentTimeout();
        CancellationToken cancellationToken = cancellationTokenSource.Token;

        IEnumerable<string>? files = debugBridge.TraceBadBlockToFile(blockHash, cancellationToken, options);

        if (_logger.IsTrace) _logger.Trace($"{nameof(debug_standardTraceBadBlockToFile)} request {blockHash}, result: {files}");

        return ResultWrapper<IEnumerable<string>>.Success(files);
    }

    private const string ExistingFileError = "location would overwrite an existing file";

    public ResultWrapper<IEnumerable<BadBlock>> debug_getBadBlocks(string? file = null)
    {
        IEnumerable<BadBlock> badBlocks = debugBridge.GetBadBlocks().Select(block => new BadBlock(block, true, specProvider, _blockDecoder, blockForRpcFactory));
        if (file is null)
        {
            return ResultWrapper<IEnumerable<BadBlock>>.Success(badBlocks);
        }

        // Non-string arguments arrive as their raw JSON text, so a relative path could be "123" or "{}".
        // Two leading separators make a UNC or device path on Windows, which would open a connection to another host.
        if (!Path.IsPathFullyQualified(file) || file is ['/' or '\\', '/' or '\\', ..])
        {
            return ResultWrapper<IEnumerable<BadBlock>>.Fail("file path must be an absolute local path", ErrorCodes.InvalidParams);
        }

        // Checked up front to fail before serializing, and so an existing directory fails the same way on every
        // platform; the move below stays the final guard.
        if (Path.Exists(file))
        {
            return ResultWrapper<IEnumerable<BadBlock>>.Fail(ExistingFileError, ErrorCodes.Default);
        }

        // Written next to the target and moved into place, so the target never holds a partial file and a failed
        // write only removes the file this call created.
        string temporaryFile = $"{file}.{Guid.NewGuid():N}.tmp";
        try
        {
            using FileStream stream = new(temporaryFile, FileMode.CreateNew, FileAccess.Write);
            EthereumJsonSerializer.SerializeToStream(stream, badBlocks);
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"{nameof(debug_getBadBlocks)} failed to write {file}: {e.Message}");
            DeleteTemporaryFile(temporaryFile);
            return ResultWrapper<IEnumerable<BadBlock>>.Fail(e.Message, ErrorCodes.Default);
        }

        try
        {
            File.Move(temporaryFile, file, overwrite: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DeleteTemporaryFile(temporaryFile);
            return ResultWrapper<IEnumerable<BadBlock>>.Fail(e is IOException && Path.Exists(file) ? ExistingFileError : e.Message, ErrorCodes.Default);
        }

        return ResultWrapper<IEnumerable<BadBlock>>.Success(null);

        void DeleteTemporaryFile(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (_logger.IsWarn) _logger.Warn($"{nameof(debug_getBadBlocks)} failed to remove temporary file {path}: {e.Message}");
            }
        }
    }

    private CancellationTokenSource BuildTimeoutCancellationTokenSource() =>
        jsonRpcConfig.BuildTimeoutCancellationToken();

    public ResultWrapper<IReadOnlyList<SimulateBlockResult<GethLikeTxTrace>>> debug_simulateV1(
        SimulatePayload<TransactionForRpc> payload, BlockParameter? blockParameter = null, GethTraceOptions? options = null) => new SimulateTxExecutor<GethLikeTxTrace>(blockchainBridge, blockFinder, jsonRpcConfig, specProvider, new GethStyleSimulateBlockTracerFactory(options: WithBufferedLimit(options)), _secondsPerSlot)
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
            if (bundles[i].BlockOverride?.Withdrawals is not null)
                return ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>>.Fail(BlockOverride.WithdrawalsNotSupported, ErrorCodes.InvalidParams);
        }

        blockParameter ??= BlockParameter.Latest;

        BlockHeader header = TryGetHeaderAndCheckState(blockParameter, out ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>>? headerError);
        if (headerError is not null)
        {
            return headerError;
        }

        if (options?.TxIndex is not null) options = options with { TxIndex = null };

        return bundles.Any(b => b.BlockOverride is not null || b.StateOverrides is not null)
            ? TraceCallManyWithOverrides(bundles, options, header)
            : TraceCallMany(bundles, new BlockParameter(header.Hash!), options, header);
    }

    private ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>> TraceCallMany(TransactionBundle[] bundles, BlockParameter blockParameter, GethTraceOptions? options, BlockHeader header)
    {
        if (CanStreamStructLogs(options))
        {
            return ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>>.Success(BuildStreamingBundleResult(bundles, blockParameter, options, header));
        }

        CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
        try
        {
            IEnumerable<IEnumerable<GethLikeTxTrace>> bundleTraces = debugBridge
                .GetBundleTraces(bundles, blockParameter, jsonRpcConfig.GasCap, timeout.Token, WithBufferedLimit(options));

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
        GethTraceOptions? options,
        BlockHeader header)
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
                _logger)
            {
                // Bundles without their own block overrides run on the header with the trace options' overrides applied.
                Spec = specProvider.GetSpec(options?.BlockOverrides?.ApplyTo(header) ?? header)
            };
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
        // Pre-compute the block each bundle targets so we can drop fillers from the result, keeping a 1:1
        // mapping to the input bundles, and cap its default gas by that block's spec.
        HashSet<ulong> bundleBlockNumbers = new(bundles.Length);
        ulong lastBlockNumber = header.Number;
        ulong lastBlockTime = header.Timestamp;
        foreach (TransactionBundle bundle in bundles)
        {
            ulong number = bundle.BlockOverride.GetBlockNumber(lastBlockNumber);
            // SimulateTxExecutor's clock: each filler and the bundle's own block advance one slot. Out-of-order
            // numbers make this meaningless, but SimulateTxExecutor rejects those before anything runs.
            ulong time = bundle.BlockOverride?.Time ?? lastBlockTime + (number - lastBlockNumber) * _secondsPerSlot;
            bundleBlockNumbers.Add(number);
            FillOmittedGas(bundle, specProvider.GetSpec(number, time));
            lastBlockNumber = number;
            lastBlockTime = time;
        }

        ResultWrapper<IReadOnlyList<SimulateBlockResult<GethLikeTxTrace>>> simulationResult =
            new SimulateTxExecutor<GethLikeTxTrace>(
                blockchainBridge,
                blockFinder,
                jsonRpcConfig,
                specProvider,
                new GethStyleSimulateBlockTracerFactory(options: WithBufferedLimit(options)),
                _secondsPerSlot,
                // These are calls, not a simulation, so a lone blob fee cap is rejected as it is without overrides.
                readLoneBlobFeeCapAsAbsent: false,
                // A bundle call runs as eth_call does: the block gas limit does not bound it, which FillOmittedGas relies on.
                callSemantics: true
            ).Execute(simulatePayload, null, searchResult: new(header));

        if (simulationResult.ErrorCode != 0)
        {
            string errorMessage = simulationResult.Result.Error ?? $"Simulation failed with error code {simulationResult.ErrorCode}.";
            if (_logger.IsWarn) _logger.Warn($"debug_traceCallMany simulation failed: Code={simulationResult.ErrorCode}, Details={errorMessage}");
            return ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>>.Fail(errorMessage, simulationResult.ErrorCode);
        }

        IEnumerable<IEnumerable<GethLikeTxTrace>> bundleTraces = simulationResult.Data
            .Where(blockResult => blockResult.Number is ulong n && bundleBlockNumbers.Contains(n))
            .Select(blockResult => blockResult.Traces);

        return ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>>.Success(bundleTraces);
    }

    /// <summary>Defaults each omitted or zero gas in <paramref name="bundle"/> to the RPC gas cap.</summary>
    /// <remarks>
    /// The fill-in makes the gas look caller-supplied to <see cref="SimulateTxExecutor{TTrace}"/>, which then skips
    /// both gas-less clamps, so cap it here as the gas-less arm of <c>ToTransaction</c> does, by the spec of the
    /// block the bundle actually runs in rather than its parent's.
    /// </remarks>
    private void FillOmittedGas(TransactionBundle bundle, IReleaseSpec spec)
    {
        ulong? defaultGas = jsonRpcConfig.GasCap.IsGasCapped()
            ? Math.Min(jsonRpcConfig.GasCap!.Value, spec.GetProcessorEnforcedTxGasLimitCap())
            : null;
        foreach (TransactionForRpc call in bundle.Transactions)
        {
            if (!call.Gas.IsGasCapped())
            {
                call.Gas = defaultGas;
            }
        }
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

    /// <summary>
    /// Resolves the header for <paramref name="blockParameter"/>, without checking state availability.
    /// </summary>
    /// <returns>The resolved header, or <see langword="null"/> when <paramref name="error"/> is set.</returns>
    private BlockHeader? TryGetHeader<TResult>(BlockParameter blockParameter, out ResultWrapper<TResult>? error)
    {
        SearchResult<BlockHeader> searchResult = blockFinder.SearchForHeader(blockParameter);

        if (searchResult.IsError)
        {
            error = GetFailureResult<TResult, BlockHeader>(searchResult, debugBridge.HaveNotSyncedHeadersYet());
            return null;
        }

        error = null;
        return searchResult.Object;
    }

    /// <summary>
    /// Resolves the header for <paramref name="blockHash"/>, without checking state availability.
    /// </summary>
    /// <returns>The resolved header, or <see langword="null"/> when <paramref name="error"/> is set.</returns>
    /// <remarks>
    /// Lookup order must match the replay: intermediate roots use <paramref name="searchBadBlocks"/> to fall back
    /// to the bad-block store after the block tree. Bad-block file tracing uses <paramref name="badBlocksOnly"/>
    /// to bypass the block tree, taking precedence over <paramref name="searchBadBlocks"/>.
    /// Whole-block replays use <paramref name="requireBody"/> to reject tree headers whose bodies are missing.
    /// </remarks>
    private BlockHeader? TryGetHeader<TResult>(Hash256 blockHash, out ResultWrapper<TResult>? error, bool searchBadBlocks = false, bool badBlocksOnly = false, bool requireBody = false)
    {
        BlockHeader? header = badBlocksOnly ? null : blockFinder.FindHeader(blockHash);
        if (requireBody && header is not null
            && !blockFinder.HasBlock(header.Number, blockHash) && blockFinder.FindBlock(blockHash, header.Number) is null)
        {
            header = null;
        }
        if (header is null && (searchBadBlocks || badBlocksOnly))
        {
            foreach (Block block in debugBridge.GetBadBlocks())
            {
                if (block.Hash != blockHash) continue;
                header = block.Header;
                break;
            }
        }

        if (header is null)
        {
            error = GetFailureResult<TResult, BlockHeader>(
                new SearchResult<BlockHeader>($"Cannot find header for block hash: {blockHash}", ErrorCodes.ResourceNotFound),
                debugBridge.HaveNotSyncedHeadersYet());
            return null;
        }

        error = null;
        return header;
    }

    /// <summary>
    /// Resolves the header for <paramref name="blockParameter"/> and checks that its own state is available.
    /// </summary>
    /// <returns>The resolved header, or <see langword="null"/> when <paramref name="error"/> is set.</returns>
    private BlockHeader? TryGetHeaderAndCheckState<TResult>(BlockParameter blockParameter, out ResultWrapper<TResult>? error)
    {
        BlockHeader? header = TryGetHeader<TResult>(blockParameter, out error);
        if (header is null)
        {
            return null;
        }

        if (!blockchainBridge.HasStateForBlock(header))
        {
            error = GetStateFailureResult<TResult>(header);
            return null;
        }

        return header;
    }

    /// <summary>
    /// Resolves the header for <paramref name="blockHash"/> and checks that its own state is available.
    /// </summary>
    /// <returns>The resolved header, or <see langword="null"/> when <paramref name="error"/> is set.</returns>
    private BlockHeader? TryGetHeaderAndCheckState<TResult>(Hash256 blockHash, out ResultWrapper<TResult>? error)
    {
        BlockHeader? header = TryGetHeader<TResult>(blockHash, out error);
        if (header is null)
        {
            return null;
        }

        if (!blockchainBridge.HasStateForBlock(header))
        {
            error = GetStateFailureResult<TResult>(header);
            return null;
        }

        return header;
    }

    /// <summary>
    /// Checks that the state a whole-block replay of <paramref name="header"/> starts from is available.
    /// </summary>
    /// <returns>The failure to return to the caller, or <see langword="null"/> when the state is available.</returns>
    /// <remarks>
    /// Replaying a block re-executes it on top of its parent, so the state that has to be available is the
    /// parent's and not the block's own: <see cref="GethStyleTracer"/> seeds the processing scope with the
    /// parent header. Genesis has no parent, so it is replayed from the pre-genesis empty state
    /// (<see cref="Nethermind.Evm.State.IWorldState.PreGenesis"/>) and needs no committed state at all.
    /// </remarks>
    private ResultWrapper<TResult>? CheckTraceBaseState<TResult>(BlockHeader header)
    {
        if (header.IsGenesis)
        {
            return null;
        }

        BlockHeader? parent = blockFinder.FindParentHeader(header, BlockTreeLookupOptions.None);

        if (parent is null)
        {
            return ResultWrapper<TResult>.Fail(
                $"Cannot find parent header for block {header.ToString(BlockHeader.Format.FullHashAndNumber)}",
                ErrorCodes.ResourceUnavailable);
        }

        return blockchainBridge.HasStateForBlock(parent) ? null : GetStateFailureResult<TResult>(parent);
    }

    private Block? TryGetBlockAndCheckState<TResult>(Rlp blockRlp, out ResultWrapper<TResult>? error, bool requireCompleteInput = false)
    {
        Block? block;

        try
        {
            RlpReader context = new(blockRlp.Bytes);
            block = requireCompleteInput ? _blockDecoder.DecodeComplete(ref context) : _blockDecoder.Decode(ref context);
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

        error = CheckTraceBaseState<TResult>(block.Header);
        return error is null ? block : null;
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
