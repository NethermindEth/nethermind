// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Consensus.Producers;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin.KeepAlive;

/// <summary>Asks the engine keep-alive for a tick as an <c>engine_getBlobs</c> request arrives.</summary>
internal sealed class KeepAliveGetBlobsHandler<TRequest, TResult>(IAsyncHandler<TRequest, TResult> inner, EngineKeepAliveService keepAlive)
    : IAsyncHandler<TRequest, TResult>
{
    public Task<ResultWrapper<TResult>> HandleAsync(TRequest request)
    {
        keepAlive.Signal();
        return inner.HandleAsync(request);
    }
}

/// <summary>
/// Asks the engine keep-alive for a tick as an <c>engine_forkchoiceUpdated</c> request arrives, and starts it after
/// the first one answered <see cref="PayloadStatus.Valid"/>: the node is then following the chain head.
/// </summary>
internal sealed class KeepAliveForkchoiceUpdatedHandler(IForkchoiceUpdatedHandler inner, EngineKeepAliveService keepAlive)
    : IForkchoiceUpdatedHandler
{
    public Task<ResultWrapper<ForkchoiceUpdatedV1Result>> Handle(ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes, int fcuVersion)
    {
        keepAlive.Signal();
        Task<ResultWrapper<ForkchoiceUpdatedV1Result>> result = inner.Handle(forkchoiceState, payloadAttributes, fcuVersion);
        return keepAlive.IsStarted ? result : StartAfterValid(result);
    }

    private async Task<ResultWrapper<ForkchoiceUpdatedV1Result>> StartAfterValid(Task<ResultWrapper<ForkchoiceUpdatedV1Result>> pending)
    {
        ResultWrapper<ForkchoiceUpdatedV1Result> result = await pending;
        if (result.Data?.PayloadStatus.Status == PayloadStatus.Valid) keepAlive.Start();
        return result;
    }
}

/// <summary>Wraps the Engine API handlers when <see cref="IMergeConfig.EngineKeepAlive"/> is on; leaves them as they are when it is off.</summary>
internal static class KeepAliveDecoration
{
    public static T Decorate<T>(IComponentContext ctx, T inner, Func<T, EngineKeepAliveService, T> decorate) where T : class =>
        ctx.Resolve<IMergeConfig>().EngineKeepAlive == EngineKeepAliveMode.Off ? inner : decorate(inner, ctx.Resolve<EngineKeepAliveService>());
}
