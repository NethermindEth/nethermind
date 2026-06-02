// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using Nethermind.Blockchain.Find;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Facade.Simulate;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Modules.Eth;

public class SimulateTxExecutor<TTrace>(
    IBlockchainBridge blockchainBridge,
    IBlockFinder blockFinder,
    IJsonRpcConfig rpcConfig,
    ISpecProvider specProvider,
    ISimulateBlockTracerFactory<TTrace> simulateBlockTracerFactory,
    ulong? secondsPerSlot = null,
    ILogManager? logManager = null)
    : ExecutorBase<IReadOnlyList<SimulateBlockResult<TTrace>>, SimulatePayload<TransactionForRpc>,
    SimulatePayload<TransactionWithSourceDetails>>(blockchainBridge, blockFinder, rpcConfig)
{
    private readonly long _blocksLimit = rpcConfig.MaxSimulateBlocksCap ?? 256;
    private readonly ulong _secondsPerSlot = secondsPerSlot ?? new BlocksConfig().SecondsPerSlot;
    private readonly ILogger _logger = (logManager ?? LimboLogs.Instance).GetClassLogger<SimulateTxExecutor<TTrace>>();
    // The buffered JSON-RPC pipeline serializes SimulateBlockResult<TTrace> with the standard
    // (non-indented) Ethereum options; reusing the same JsonSerializerOptions instance for each
    // streamed block keeps the emitted bytes property-for-property identical and lets the
    // serializer cache its reflection metadata across blocks.
    private static readonly JsonSerializerOptions _streamingJsonOptions = EthereumJsonSerializer.JsonOptions;

    protected override Result<SimulatePayload<TransactionWithSourceDetails>> Prepare(SimulatePayload<TransactionForRpc> call, BlockHeader header)
    {
        List<BlockStateCall<TransactionWithSourceDetails>>? blockStateCalls = null;

        if (call.BlockStateCalls is not null)
        {
            blockStateCalls = new List<BlockStateCall<TransactionWithSourceDetails>>(call.BlockStateCalls.Count);

            foreach (BlockStateCall<TransactionForRpc> blockStateCall in call.BlockStateCalls)
            {
                TransactionWithSourceDetails[]? calls = null;

                if (blockStateCall.Calls is not null)
                {
                    calls = new TransactionWithSourceDetails[blockStateCall.Calls.Length];

                    for (int i = 0; i < blockStateCall.Calls.Length; i++)
                    {
                        TransactionForRpc callTransactionModel = blockStateCall.Calls[i];
                        LegacyTransactionForRpc? asLegacy = callTransactionModel as LegacyTransactionForRpc;
                        bool hadGasLimitInRequest = asLegacy?.Gas is not null;
                        bool hadNonceInRequest = asLegacy?.Nonce is not null;

                        IReleaseSpec spec = specProvider.GetSpec(header);
                        Result<Transaction> txResult = callTransactionModel.ToTransaction(validateUserInput: call.Validation, gasCap: _rpcConfig.GasCap, spec: spec);
                        if (!txResult.Success(out Transaction? tx, out string? error))
                        {
                            return error;
                        }

                        tx.ChainId = _blockchainBridge.GetChainId();

                        calls[i] = new TransactionWithSourceDetails
                        {
                            HadGasLimitInRequest = hadGasLimitInRequest,
                            HadNonceInRequest = hadNonceInRequest,
                            Transaction = tx
                        };
                    }
                }

                blockStateCalls.Add(new BlockStateCall<TransactionWithSourceDetails>
                {
                    BlockOverrides = blockStateCall.BlockOverrides,
                    StateOverrides = blockStateCall.StateOverrides,
                    Calls = calls
                });
            }
        }

        return new SimulatePayload<TransactionWithSourceDetails>
        {
            TraceTransfers = call.TraceTransfers,
            Validation = call.Validation,
            ReturnFullTransactionObjects = call.ReturnFullTransactionObjects,
            BlockStateCalls = blockStateCalls
        };
    }

    /// <summary>
    /// When enabled, <see cref="Execute(SimulatePayload{TransactionForRpc}, BlockParameter?, Dictionary{Address, AccountOverride}?, SearchResult{BlockHeader}?)"/>
    /// will, after performing all input validation, return a <see cref="SimulateStreamingResult{TTrace}"/>
    /// (typed as <see cref="IReadOnlyList{T}"/>) that emits one <see cref="SimulateBlockResult{TTrace}"/>
    /// JSON object to the wire as each simulated block completes. Validation errors are still
    /// returned as buffered <see cref="ResultWrapper{T}.Fail"/> envelopes before any output bytes,
    /// so HTTP status semantics are preserved.
    /// </summary>
    public bool StreamingEnabled { get; init; } = rpcConfig.EnableSimulateStreamMode;

    public override ResultWrapper<IReadOnlyList<SimulateBlockResult<TTrace>>> Execute(
        SimulatePayload<TransactionForRpc> call,
        BlockParameter? blockParameter,
        Dictionary<Address, AccountOverride>? stateOverride = null,
        SearchResult<BlockHeader>? searchResult = null)
    {
        if (call.BlockStateCalls is null)
            return ResultWrapper<IReadOnlyList<SimulateBlockResult<TTrace>>>.Fail("Must contain BlockStateCalls", ErrorCodes.InvalidParams);

        if (call.BlockStateCalls!.Count > _rpcConfig.MaxSimulateBlocksCap)
            return ResultWrapper<IReadOnlyList<SimulateBlockResult<TTrace>>>.Fail(
                $"This node is configured to support only {_rpcConfig.MaxSimulateBlocksCap} blocks", ErrorCodes.ClientLimitExceededError);

        searchResult ??= _blockFinder.SearchForHeader(blockParameter);

        if (searchResult.Value.IsError || searchResult.Value.Object is null)
            return ResultWrapper<IReadOnlyList<SimulateBlockResult<TTrace>>>.Fail(searchResult.Value);

        BlockHeader header = searchResult.Value.Object;

        if (!_blockchainBridge.HasStateForBlock(header!))
            return ResultWrapper<IReadOnlyList<SimulateBlockResult<TTrace>>>.Fail($"No state available for block {header.ToString(BlockHeader.Format.FullHashAndNumber)}",
                ErrorCodes.ResourceNotFound);

        if (call.BlockStateCalls?.Count > _blocksLimit)
            return ResultWrapper<IReadOnlyList<SimulateBlockResult<TTrace>>>.Fail(
                $"Too many blocks provided, node is configured to simulate up to {_blocksLimit} while {call.BlockStateCalls?.Count} were given",
                ErrorCodes.InvalidParams);

        if (call.BlockStateCalls is not null)
        {
            long lastBlockNumber = header.Number;
            ulong lastBlockTime = header.Timestamp;

            using ArrayPoolListRef<BlockStateCall<TransactionForRpc>> completeBlockStateCalls = new(call.BlockStateCalls.Count);

            foreach (BlockStateCall<TransactionForRpc>? blockToSimulate in call.BlockStateCalls)
            {
                blockToSimulate.BlockOverrides ??= new BlockOverride();
                ulong givenNumber = blockToSimulate.BlockOverrides.GetBlockNumber(lastBlockNumber);

                if (givenNumber > long.MaxValue)
                    return ResultWrapper<IReadOnlyList<SimulateBlockResult<TTrace>>>.Fail($"Block number too big {givenNumber}!", ErrorCodes.InvalidParams);

                if (givenNumber <= (ulong)lastBlockNumber)
                    return ResultWrapper<IReadOnlyList<SimulateBlockResult<TTrace>>>.Fail(SimulateErrorMessages.BlockNumberNotIncreasing, ErrorCodes.InvalidInputBlocksOutOfOrder);

                // if the no. of filler blocks are greater than maximum simulate blocks cap
                if (givenNumber - (ulong)lastBlockNumber > (ulong)_blocksLimit)
                    return ResultWrapper<IReadOnlyList<SimulateBlockResult<TTrace>>>.Fail($"too many blocks", ErrorCodes.ClientLimitExceededError);

                for (ulong fillBlockNumber = (ulong)lastBlockNumber + 1; fillBlockNumber < givenNumber; fillBlockNumber++)
                {
                    ulong fillBlockTime = lastBlockTime + _secondsPerSlot;
                    completeBlockStateCalls.Add(new BlockStateCall<TransactionForRpc>
                    {
                        BlockOverrides = new BlockOverride { Number = fillBlockNumber, Time = fillBlockTime },
                        StateOverrides = null,
                        Calls = []
                    });
                    lastBlockTime = fillBlockTime;
                }

                blockToSimulate.BlockOverrides.Number = givenNumber;

                if (blockToSimulate.BlockOverrides.Time is not null)
                {
                    if (blockToSimulate.BlockOverrides.Time <= lastBlockTime)
                    {
                        return ResultWrapper<IReadOnlyList<SimulateBlockResult<TTrace>>>.Fail($"Block timestamp out of order {blockToSimulate.BlockOverrides.Time} is <= than given base timestamp of {lastBlockTime}!", ErrorCodes.BlockTimestampNotIncreased);
                    }
                    lastBlockTime = (ulong)blockToSimulate.BlockOverrides.Time;
                }
                else
                {
                    blockToSimulate.BlockOverrides.Time = lastBlockTime + _secondsPerSlot;
                    lastBlockTime = (ulong)blockToSimulate.BlockOverrides.Time;
                }
                lastBlockNumber = (long)givenNumber;

                if (blockToSimulate.StateOverrides is not null)
                {
                    IReleaseSpec spec = specProvider.GetSpec((long)givenNumber, blockToSimulate.BlockOverrides.Time);
                    foreach ((Address address, AccountOverride accountOverride) in blockToSimulate.StateOverrides)
                    {
                        if (accountOverride.MovePrecompileToAddress is null) continue;

                        if (spec.IsPrecompile(address) && accountOverride.MovePrecompileToAddress == address)
                            return ResultWrapper<IReadOnlyList<SimulateBlockResult<TTrace>>>.Fail(
                                "MovePrecompileToAddress referenced itself in replacement",
                                ErrorCodes.MovePrecompileSelfReference);
                    }
                }

                completeBlockStateCalls.Add(blockToSimulate);
            }
            call.BlockStateCalls = [.. completeBlockStateCalls];
        }

        if (StreamingEnabled)
        {
            Result<SimulatePayload<TransactionWithSourceDetails>> streamingPrepare = Prepare(call, header);
            if (!streamingPrepare.Success(out SimulatePayload<TransactionWithSourceDetails>? streamingData, out string? streamingPrepError))
            {
                return ResultWrapper<IReadOnlyList<SimulateBlockResult<TTrace>>>.Fail(streamingPrepError, ErrorCodes.InvalidInput);
            }
            return BuildStreamingResult(header.Clone(), streamingData, stateOverride);
        }

        using CancellationTokenSource timeout = _rpcConfig.BuildTimeoutCancellationToken();

        Result<SimulatePayload<TransactionWithSourceDetails>> prepareResult = Prepare(call, header);
        return !prepareResult.Success(out SimulatePayload<TransactionWithSourceDetails>? data, out string? error)
            ? ResultWrapper<IReadOnlyList<SimulateBlockResult<TTrace>>>.Fail(error, ErrorCodes.InvalidInput)
            : Execute(header.Clone(), data, stateOverride, timeout.Token);
    }

    private ResultWrapper<IReadOnlyList<SimulateBlockResult<TTrace>>> BuildStreamingResult(
        BlockHeader header,
        SimulatePayload<TransactionWithSourceDetails> payload,
        Dictionary<Address, AccountOverride>? stateOverride)
    {
        // The streaming result owns the timeoutCts and disposes it after the response writes
        // through StreamingResultBase.Dispose; the buffered `using CancellationTokenSource timeout`
        // pattern from the non-streaming branch would dispose the CTS before the lazy response
        // pipeline actually runs.
        CancellationTokenSource timeoutCts = _rpcConfig.BuildTimeoutCancellationToken();
        SimulateStreamingResult<TTrace> streamingResult;
        try
        {
            streamingResult = new SimulateStreamingResult<TTrace>(
                (writer, pipeWriter, token) => RunStreamingSimulation(header, payload, stateOverride, writer, pipeWriter, token),
                timeoutCts,
                _logger);
        }
        catch
        {
            timeoutCts.Dispose();
            throw;
        }

        return ResultWrapper<IReadOnlyList<SimulateBlockResult<TTrace>>>.Success(streamingResult);
    }

    private void RunStreamingSimulation(
        BlockHeader header,
        SimulatePayload<TransactionWithSourceDetails> payload,
        Dictionary<Address, AccountOverride>? stateOverride,
        Utf8JsonWriter writer,
        PipeWriter? pipeWriter,
        CancellationToken cancellationToken)
    {
        // The cross-block accumulator is never materialized — each block is serialized
        // and flushed inline as BlockProcessor.ProcessOne returns for it.
        SimulateOutput<TTrace> results = _blockchainBridge.SimulateStreaming(
            header,
            payload,
            simulateBlockTracerFactory,
            blockResult =>
            {
                ApplyCallErrorCodeMapping(blockResult);
                JsonSerializer.Serialize(writer, blockResult, _streamingJsonOptions);
                FlushBetweenBlocks(writer, pipeWriter, cancellationToken);
            },
            _rpcConfig.GasCap!.Value,
            cancellationToken);

        if (results.Error is null) return;

        int errorCode = results.IsInvalidInput
            ? ErrorCodes.Default
            : results.TransactionResult.TransactionExecuted
                ? ErrorCodes.InternalError
                : MapSimulateErrorCode(results.TransactionResult);
        string message = MapSimulateErrorMessage(results.TransactionResult) ?? results.Error;
        // Streaming has already begun: failures are inlined into the outer array so the
        // client distinguishes them by the `error`/`errorCode` keys (regular blocks have `number`).
        SimulateEnvelopeWriter.WriteFailureObject(writer, message, errorCode);
    }

    private static void ApplyCallErrorCodeMapping(SimulateBlockResult<TTrace> blockResult)
    {
        if (blockResult is not SimulateBlockResult<SimulateCallResult> callResult) return;
        foreach (SimulateCallResult? c in callResult.Calls)
        {
            if (c is { Error: not null } && !string.IsNullOrEmpty(c.Error.Message))
            {
                EvmExceptionType exception = c.Error.EvmException;
                c.Error.Code = MapEvmExceptionType(exception);
                if (exception != EvmExceptionType.Revert)
                {
                    c.Error.Message = c.Error.EvmException.GetEvmExceptionDescription();
                    c.Error.Data = null;
                }
            }
        }
    }

    private static void FlushBetweenBlocks(Utf8JsonWriter writer, PipeWriter? pipeWriter, CancellationToken cancellationToken)
    {
        if (pipeWriter is null) return;
        writer.Flush();
        // Mirrors the synchronous flush pattern in GethLikeTxDirectStreamingTracer.MaybeFlushToWire
        // and GethLikeTxTraceStreamingBundleResult.FlushBetweenBundles. The EmitContent callback is
        // synchronous by design so the streaming result types don't have to plumb async tracers.
        pipeWriter.FlushAsync(cancellationToken).GetAwaiter().GetResult();
    }

    protected override ResultWrapper<IReadOnlyList<SimulateBlockResult<TTrace>>> Execute(
        BlockHeader header,
        SimulatePayload<TransactionWithSourceDetails> tx,
        Dictionary<Address, AccountOverride>? stateOverride,
        CancellationToken token)
    {
        SimulateOutput<TTrace> results = _blockchainBridge.Simulate(header, tx, simulateBlockTracerFactory, _rpcConfig.GasCap!.Value, token);

        foreach (SimulateBlockResult<TTrace> item in results.Items)
        {
            if (item is SimulateBlockResult<SimulateCallResult> result)
            {
                foreach (SimulateCallResult? call in result.Calls)
                {
                    if (call is { Error: not null } && !string.IsNullOrEmpty(call.Error.Message))
                    {
                        EvmExceptionType exception = call.Error.EvmException;
                        call.Error.Code = MapEvmExceptionType(exception);
                        if (exception != EvmExceptionType.Revert)
                        {
                            call.Error.Message = call.Error.EvmException.GetEvmExceptionDescription();
                            call.Error.Data = null;
                        }
                    }
                }
            }
        }

        int? errorCode = results.TransactionResult.TransactionExecuted
            ? null
            : MapSimulateErrorCode(results.TransactionResult);
        if (results.IsInvalidInput) errorCode = ErrorCodes.Default;

        if (results.Error is null)
            return ResultWrapper<IReadOnlyList<SimulateBlockResult<TTrace>>>.Success([.. results.Items]);

        if (errorCode is not null)
        {
            string message = MapSimulateErrorMessage(results.TransactionResult) ?? results.Error!;
            return ResultWrapper<IReadOnlyList<SimulateBlockResult<TTrace>>>.Fail(message, errorCode.Value);
        }

        return ResultWrapper<IReadOnlyList<SimulateBlockResult<TTrace>>>.Fail(results.Error);
    }

    private static int MapSimulateErrorCode(TransactionResult txResult)
    {
        if (txResult.Error != TransactionResult.ErrorType.None)
        {
            return txResult.Error switch
            {
                TransactionResult.ErrorType.BlockGasLimitExceeded => ErrorCodes.BlockGasLimitReached,
                TransactionResult.ErrorType.GasLimitBelowIntrinsicGas => ErrorCodes.IntrinsicGas,
                TransactionResult.ErrorType.InsufficientMaxFeePerGasForSenderBalance
                    or TransactionResult.ErrorType.InsufficientSenderBalance => ErrorCodes.InsufficientFunds,
                TransactionResult.ErrorType.MalformedTransaction => ErrorCodes.InternalError,
                TransactionResult.ErrorType.MaxFeePerGasBelowBaseFee
                    or TransactionResult.ErrorType.MinerPremiumNegative => ErrorCodes.FeeCapBelowBaseFee,
                TransactionResult.ErrorType.NonceOverflow => ErrorCodes.InternalError,
                TransactionResult.ErrorType.SenderHasDeployedCode => ErrorCodes.SenderIsNotEoa,
                TransactionResult.ErrorType.SenderNotSpecified => ErrorCodes.InternalError,
                TransactionResult.ErrorType.TransactionSizeOverMaxInitCodeSize => ErrorCodes.MaxInitCodeSizeExceeded,
                TransactionResult.ErrorType.TransactionNonceTooHigh => ErrorCodes.NonceTooHigh,
                TransactionResult.ErrorType.TransactionNonceTooLow => ErrorCodes.NonceTooLow,
                _ => ErrorCodes.InternalError
            };
        }

        return MapEvmExceptionType(txResult.EvmExceptionType);
    }

    /// <summary>
    /// Returns the spec-mandated error message for well-known eth_simulateV1 error types,
    /// or <see langword="null"/> when no override is required.
    /// </summary>
    private static string? MapSimulateErrorMessage(TransactionResult txResult) =>
        txResult.Error switch
        {
            TransactionResult.ErrorType.GasLimitBelowIntrinsicGas
                => SimulateErrorMessages.IntrinsicGas,
            TransactionResult.ErrorType.MaxFeePerGasBelowBaseFee
                or TransactionResult.ErrorType.MinerPremiumNegative
                => SimulateErrorMessages.FeeCapBelowBaseFee,
            TransactionResult.ErrorType.InsufficientSenderBalance
                or TransactionResult.ErrorType.InsufficientMaxFeePerGasForSenderBalance
                => SimulateErrorMessages.InsufficientFunds,
            _ => null
        };

    private static int MapEvmExceptionType(EvmExceptionType type) => type switch
    {
        EvmExceptionType.Revert => ErrorCodes.ExecutionReverted,
        _ => ErrorCodes.VMError
    };
}

/// <summary>
/// Canonical eth_simulateV1 error message strings shared between the executor and tests.
/// </summary>
internal static class SimulateErrorMessages
{
    /// <summary>
    /// Returned when the transaction gas limit is below the intrinsic gas cost
    /// (error code <see cref="ErrorCodes.IntrinsicGas"/>).
    /// </summary>
    public const string IntrinsicGas = "Not enough gas provided to pay for intrinsic gas for a transaction";

    /// <summary>
    /// Returned when <c>maxFeePerGas</c> is below the block <c>baseFeePerGas</c>
    /// (error code <see cref="ErrorCodes.FeeCapBelowBaseFee"/>).
    /// </summary>
    public const string FeeCapBelowBaseFee = "max fee per gas less than block base fee";

    /// <summary>
    /// Returned when the sender does not have enough balance to cover gas + value
    /// (error code <see cref="ErrorCodes.InsufficientFunds"/>).
    /// </summary>
    public const string InsufficientFunds = "Insufficient funds to pay for gas fees and value for a transaction";

    /// <summary>
    /// Returned when a simulated block number is not strictly greater than the previous one
    /// (error code <see cref="ErrorCodes.InvalidInputBlocksOutOfOrder"/>). Mandated verbatim by
    /// the execution-apis spec.
    /// </summary>
    public const string BlockNumberNotIncreasing = "Block number in sequence did not increase";
}
