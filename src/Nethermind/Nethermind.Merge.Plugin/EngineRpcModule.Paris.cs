// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.GC;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin;

public partial class EngineRpcModule : IEngineRpcModule
{
    private readonly IAsyncHandler<byte[], ExecutionPayload?> _getPayloadHandlerV1;
    private readonly IAsyncHandler<ExecutionPayload, PayloadStatusV1> _newPayloadV1Handler;
    private readonly IForkchoiceUpdatedHandler _forkchoiceUpdatedV1Handler;
    private readonly IHandler<TransitionConfigurationV1, TransitionConfigurationV1> _transitionConfigurationHandler;
    private readonly SemaphoreSlim _locker = new(1, 1);
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(8);
    private readonly GCKeeper _gcKeeper;

    public ResultWrapper<TransitionConfigurationV1> engine_exchangeTransitionConfigurationV1(
        TransitionConfigurationV1 beaconTransitionConfiguration) => _transitionConfigurationHandler.Handle(beaconTransitionConfiguration);

    public async Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV1(ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes = null)
        => await ForkchoiceUpdated(forkchoiceState, payloadAttributes, 1);

    public Task<ResultWrapper<ExecutionPayload?>> engine_getPayloadV1(byte[] payloadId) =>
        _getPayloadHandlerV1.HandleAsync(payloadId);

    public async Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV1(ExecutionPayload executionPayload)
        => await NewPayload(executionPayload, 1);

    protected async Task<ResultWrapper<ForkchoiceUpdatedV1Result>> ForkchoiceUpdated(ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes, int version)
    {
        if (payloadAttributes?.Validate(_specProvider, version, out string? error) == false)
        {
            if (_logger.IsWarn) _logger.Warn(error);
            return ResultWrapper<ForkchoiceUpdatedV1Result>.Fail(error, ErrorCodes.InvalidParams);
        }

        if (await _locker.WaitAsync(_timeout))
        {
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                return await _forkchoiceUpdatedV1Handler.Handle(forkchoiceState, payloadAttributes);
            }
            finally
            {
                watch.Stop();
                Metrics.ForkchoiceUpdedExecutionTime = watch.ElapsedMilliseconds;
                _locker.Release();
            }
        }
        else
        {
            if (_logger.IsWarn) _logger.Warn($"engine_forkchoiceUpdated{version} timed out");
            return ResultWrapper<ForkchoiceUpdatedV1Result>.Fail("Timed out", ErrorCodes.Timeout);
        }
    }

    protected async Task<ResultWrapper<PayloadStatusV1>> NewPayload(IExecutionPayloadParams executionPayloadParams, int version)
    {
        ExecutionPayload executionPayload = executionPayloadParams.ExecutionPayload;

        if (!executionPayload.ValidateFork(_specProvider))
        {
            if (_logger.IsWarn) _logger.Warn($"The payload is not supported by the current fork");
            return ResultWrapper<PayloadStatusV1>.Fail("unsupported fork", version < 2 ? ErrorCodes.InvalidParams : ErrorCodes.UnsupportedFork);
        }

        IReleaseSpec releaseSpec = _specProvider.GetSpec(executionPayload.BlockNumber, executionPayload.Timestamp);
        ValidationResult validationResult = executionPayloadParams.ValidateParams(releaseSpec, version, out string? error);
        if (validationResult != ValidationResult.Success)
        {
            if (_logger.IsWarn) _logger.Warn(error);
            return validationResult == ValidationResult.Fail
                ? ResultWrapper<PayloadStatusV1>.Fail(error!, ErrorCodes.InvalidParams)
                : ResultWrapper<PayloadStatusV1>.Success(PayloadStatusV1.Invalid(null, error));
        }

        if (await _locker.WaitAsync(_timeout))
        {
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                using IDisposable region = _gcKeeper.TryStartNoGCRegion();
                return await _newPayloadV1Handler.HandleAsync(executionPayload);
            }
            catch (Exception exception)
            {
                if (_logger.IsError) _logger.Error($"engine_newPayloadV{version} failed: {exception}");
                return ResultWrapper<PayloadStatusV1>.Fail(exception.Message);
            }
            finally
            {
                watch.Stop();
                Metrics.NewPayloadExecutionTime = watch.ElapsedMilliseconds;
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
