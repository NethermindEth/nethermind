// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
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

    public ResultWrapper<TransitionConfigurationV1> engine_exchangeTransitionConfigurationV1(
        TransitionConfigurationV1 beaconTransitionConfiguration) => _transitionConfigurationHandler.Handle(beaconTransitionConfiguration);

    public async Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV1(ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes = null)
        => await ForkchoiceUpdated(forkchoiceState, payloadAttributes, 1);

    public Task<ResultWrapper<ExecutionPayload?>> engine_getPayloadV1(byte[] payloadId) =>
        _getPayloadHandlerV1.HandleAsync(payloadId);

    public async Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV1(ExecutionPayload executionPayload)
        => await NewPayload(executionPayload, 1);

    private async Task<ResultWrapper<ForkchoiceUpdatedV1Result>> ForkchoiceUpdated(ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes, int version)
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

    private async Task<ResultWrapper<PayloadStatusV1>> NewPayload(ExecutionPayload executionPayload, int version)
    {
        if (!executionPayload.Validate(_specProvider, version, out string? error))
        {
            if (_logger.IsWarn) _logger.Warn(error);
            return ResultWrapper<PayloadStatusV1>.Fail(error, ErrorCodes.InvalidParams);
        }

        if (await _locker.WaitAsync(_timeout))
        {
            long totalSize = 100.MB();
            bool noGcRegion = GC.TryStartNoGCRegion(totalSize, true);

            try
            {
                Stopwatch watch = Stopwatch.StartNew();
                try
                {
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
            finally
            {
                if (noGcRegion)
                {
                    if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion)
                    {
                        try
                        {
                            GC.EndNoGCRegion();
                        }
                        catch (InvalidOperationException)
                        {
                            if (_logger.IsWarn) _logger.Warn($"Failed to keep in NoGCRegion with Exception with {totalSize} bytes");
                        }
                    }
                    else if (_logger.IsWarn) _logger.Warn($"Failed to keep in NoGCRegion with {totalSize} bytes");
                }
                else if (_logger.IsWarn) _logger.Warn($"Failed to start NoGCRegion with {totalSize} bytes");
            }
        }
        else
        {
            if (_logger.IsWarn) _logger.Warn($"engine_newPayloadV{version} timed out");
            return ResultWrapper<PayloadStatusV1>.Fail("Timed out", ErrorCodes.Timeout);
        }
    }

}
