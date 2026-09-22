// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Memory;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using ValidationResult = Nethermind.Merge.Plugin.Data.ValidationResult;

namespace Nethermind.Merge.Plugin;

public partial class EngineRpcModule : IEngineRpcModule
{
    private readonly IAsyncHandler<byte[], ExecutionPayload?> _getPayloadHandlerV1 = getPayloadHandlerV1;
    private readonly IAsyncHandler<ExecutionPayload, PayloadStatusV1> _newPayloadV1Handler = newPayloadV1Handler;
    private readonly IForkchoiceUpdatedHandler _forkchoiceUpdatedV1Handler = forkchoiceUpdatedV1Handler;
    private readonly IHandler<TransitionConfigurationV1, TransitionConfigurationV1> _transitionConfigurationHandler = transitionConfigurationHandler;
    private readonly IEngineRequestsTracker _engineRequestsTracker = engineRequestsTracker;
    private readonly SemaphoreSlim _locker = new(1, 1);
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(8);
    private readonly GCKeeper _gcKeeper = gcKeeper;
    private readonly IBlockProcessingQueue _processingQueue = processingQueue;
    // How long the no-GC region outlives a VALID answer to cover the commit still running; the commit takes milliseconds.
    private static readonly TimeSpan NoGCRegionCommitBound = TimeSpan.FromSeconds(1);

    public ResultWrapper<TransitionConfigurationV1> engine_exchangeTransitionConfigurationV1(
        TransitionConfigurationV1 beaconTransitionConfiguration) => _transitionConfigurationHandler.Handle(beaconTransitionConfiguration);

    public Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV1(ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes = null)
        => ForkchoiceUpdated(forkchoiceState, payloadAttributes, EngineApiVersions.Fcu.V1);

    public Task<ResultWrapper<ExecutionPayload?>> engine_getPayloadV1(byte[] payloadId) =>
        _getPayloadHandlerV1.HandleAsync(payloadId);

    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV1(ExecutionPayload executionPayload)
        => NewPayload(executionPayload, EngineApiVersions.NewPayload.V1);

    protected async Task<ResultWrapper<ForkchoiceUpdatedV1Result>> ForkchoiceUpdated(
        ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes, int version)
    {
        _engineRequestsTracker.OnForkchoiceUpdatedCalled();
        if (await _locker.WaitAsync(_timeout))
        {
            long startTime = Stopwatch.GetTimestamp();
            try
            {
                return await _forkchoiceUpdatedV1Handler.Handle(forkchoiceState, payloadAttributes, version);
            }
            finally
            {
                Metrics.ForkchoiceUpdatedExecutionTime = (long)Stopwatch.GetElapsedTime(startTime).TotalMilliseconds;
                _locker.Release();
            }
        }
        else
        {
            if (_logger.IsWarn) _logger.Warn($"engine_forkchoiceUpdated{version} timed out");
            return ResultWrapper<ForkchoiceUpdatedV1Result>.Fail("Timed out", ErrorCodes.Timeout);
        }
    }


    private async Task EndNoGCRegionAfterCommitAsync(IDisposable region, Hash256 blockHash)
    {
        try
        {
            Task removed = _processingQueue.WaitUntilRemovedAsync(blockHash, executedOnly: true).AsTask();
            if (!removed.IsCompleted)
            {
                using CancellationTokenSource bound = new();
                if (await Task.WhenAny(removed, Task.Delay(NoGCRegionCommitBound, bound.Token)) == removed) bound.Cancel();
            }
        }
        finally
        {
            region.Dispose();
        }
    }

    protected async Task<ResultWrapper<PayloadStatusV1>> NewPayload(IExecutionPayloadParams executionPayloadParams, int version)
    {
        _engineRequestsTracker.OnNewPayloadCalled();
        ExecutionPayload executionPayload = executionPayloadParams.ExecutionPayload;
        executionPayload.ExecutionRequests = executionPayloadParams.ExecutionRequests;
        executionPayload.InclusionListTransactions = executionPayloadParams.InclusionListTransactions;

        if (!executionPayload.ValidateForkOnNewPayload(_specProvider, version))
        {
            if (_logger.IsWarn) _logger.Warn($"The payload is not supported by the current fork");
            return ResultWrapper<PayloadStatusV1>.Fail(MergeErrorMessages.UnsupportedFork, version < EngineApiVersions.NewPayload.V2 ? ErrorCodes.InvalidParams : MergeErrorCodes.UnsupportedFork);
        }

        IReleaseSpec releaseSpec = _specProvider.GetSpec(executionPayload.BlockNumber, executionPayload.Timestamp);

        ValidationResult validationResult = executionPayloadParams.ValidateParams(releaseSpec, version, out string? error);
        if (validationResult != ValidationResult.Success)
        {
            if (_logger.IsWarn) _logger.Warn(error!);
            return validationResult == ValidationResult.Fail
                ? ResultWrapper<PayloadStatusV1>.Fail(error!, ErrorCodes.InvalidParams)
                : ResultWrapper<PayloadStatusV1>.Success(PayloadStatusV1.Invalid(null, error));
        }

        if (await _locker.WaitAsync(_timeout))
        {
            long startTime = Stopwatch.GetTimestamp();
            try
            {
                // Start tx-root computation before asynchronous GC-region admission so it can
                // overlap that work; keep it inside the lock so competing requests cannot run
                // trie work concurrently.
                _ = executionPayload.StartTxRootComputation();
                IDisposable? region = _gcKeeper.TryStartNoGCRegion();
                try
                {
                    ResultWrapper<PayloadStatusV1> result = await _newPayloadV1Handler.HandleAsync(executionPayload);
                    // The answer is out before the block is committed; the region stays for the commit's allocations
                    // and ends when the block leaves the queue, on the thread that sees it leave.
                    _ = EndNoGCRegionAfterCommitAsync(region, executionPayload.BlockHash);
                    region = null;
                    return result;
                }
                finally
                {
                    region?.Dispose();
                }
            }
            catch (BlockchainException exception)
            {
                _logger.DebugError($"engine_newPayloadV{version} failed: {exception}");
                return ResultWrapper<PayloadStatusV1>.Fail(exception.Message, ErrorCodes.UnknownBlockError);
            }
            catch (Exception exception)
            {
                if (_logger.IsError) _logger.Error($"engine_newPayloadV{version} failed: {exception}");
                return ResultWrapper<PayloadStatusV1>.Fail(exception.Message);
            }
            finally
            {
                Metrics.NewPayloadExecutionTime = (long)Stopwatch.GetElapsedTime(startTime).TotalMilliseconds;
                _locker.Release();
            }
        }
        else
        {
            if (_logger.IsWarn) _logger.Warn($"engine_newPayloadV{version} timed out");
            return ResultWrapper<PayloadStatusV1>.Fail("Timed out", ErrorCodes.Timeout);
        }
    }
}
