// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin;

public partial class EngineRpcModule : IEngineRpcModule
{
    private static IEnumerable<string>? _capabilities;
    private readonly IHandler<ExecutionStatusResult> _executionStatusHandler;
    private readonly IAsyncHandler<Keccak[], ExecutionPayloadBodyV1Result?[]> _executionGetPayloadBodiesByHashV1Handler;
    private readonly IGetPayloadBodiesByRangeV1Handler _executionGetPayloadBodiesByRangeV1Handler;
    private readonly ISpecProvider _specProvider;
    private readonly ILogger _logger;

    public EngineRpcModule(
        IAsyncHandler<byte[], ExecutionPayload?> getPayloadHandlerV1,
        IAsyncHandler<byte[], GetPayloadV2Result?> getPayloadHandlerV2,
        IAsyncHandler<ExecutionPayload, PayloadStatusV1> newPayloadV1Handler,
        IForkchoiceUpdatedHandler forkchoiceUpdatedV1Handler,
        IHandler<ExecutionStatusResult> executionStatusHandler,
        IAsyncHandler<Keccak[], ExecutionPayloadBodyV1Result?[]> executionGetPayloadBodiesByHashV1Handler,
        IGetPayloadBodiesByRangeV1Handler executionGetPayloadBodiesByRangeV1Handler,
        IHandler<TransitionConfigurationV1, TransitionConfigurationV1> transitionConfigurationHandler,
        ISpecProvider specProvider,
        ILogManager logManager)
    {
        _getPayloadHandlerV1 = getPayloadHandlerV1;
        _getPayloadHandlerV2 = getPayloadHandlerV2;
        _newPayloadV1Handler = newPayloadV1Handler;
        _forkchoiceUpdatedV1Handler = forkchoiceUpdatedV1Handler;
        _executionStatusHandler = executionStatusHandler;
        _executionGetPayloadBodiesByHashV1Handler = executionGetPayloadBodiesByHashV1Handler;
        _executionGetPayloadBodiesByRangeV1Handler = executionGetPayloadBodiesByRangeV1Handler;
        _transitionConfigurationHandler = transitionConfigurationHandler;
        _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        _logger = logManager.GetClassLogger();
    }

    public async Task<ResultWrapper<IEnumerable<string>>> engine_exchangeCapabilities(IEnumerable<string> methods)
    {
        ArgumentNullException.ThrowIfNull(methods);

        if (await _locker.WaitAsync(1_000))
        {
            var watch = Stopwatch.StartNew();

            try
            {
                _capabilities ??= typeof(IEngineRpcModule).GetMethods()
                    .Select(m => m.Name)
                    .Where(m => !m.Equals(nameof(engine_exchangeCapabilities), StringComparison.Ordinal))
                    .Order();

                var unsupported = methods.Except(_capabilities);

                if (unsupported.Any())
                {
                    if (_logger.IsWarn) _logger.Warn($"Unsupported capabilities: {string.Join(", ", unsupported)}");
                }

                return ResultWrapper<IEnumerable<string>>.Success(_capabilities);
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
            if (_logger.IsWarn) _logger.Warn($"{nameof(engine_exchangeCapabilities)} timed out");

            return ResultWrapper<IEnumerable<string>>.Fail("Timed out", ErrorCodes.Timeout);
        }
    }

    public ResultWrapper<ExecutionStatusResult> engine_executionStatus() => _executionStatusHandler.Handle();

    public async Task<ResultWrapper<ExecutionPayloadBodyV1Result?[]>> engine_getPayloadBodiesByHashV1(Keccak[] blockHashes)
    {
        return await _executionGetPayloadBodiesByHashV1Handler.HandleAsync(blockHashes);
    }

    public async Task<ResultWrapper<ExecutionPayloadBodyV1Result?[]>> engine_getPayloadBodiesByRangeV1(long start, long count)
    {
        return await _executionGetPayloadBodiesByRangeV1Handler.Handle(start, count);
    }
}
