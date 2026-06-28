// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core.Extensions;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;

namespace Nethermind.Network.P2P.Utils;

/// <summary>
/// Executes a typed sync request for a protocol handler without storing a delegate in each scheduled request.
/// </summary>
/// <typeparam name="THandler">The protocol handler type that owns the request logic.</typeparam>
/// <typeparam name="TReq">The request message type.</typeparam>
/// <typeparam name="TRes">The response message type.</typeparam>
public interface ISyncServeRequestHandler<THandler, TReq, TRes>
    where THandler : ProtocolHandlerBase
    where TRes : P2PMessage
{
    /// <summary>
    /// Executes the request and returns the response message to send back to the peer.
    /// </summary>
    /// <param name="handler">The protocol handler instance.</param>
    /// <param name="request">The request message. The implementation owns and must dispose it.</param>
    /// <param name="cancellationToken">The cancellation token for scheduler shutdown or session closing.</param>
    /// <returns>The response message.</returns>
    static abstract Task<TRes> Execute(THandler handler, TReq request, CancellationToken cancellationToken);
}

/// <summary>
/// Some utility function for interacting with BackgroundTaskScheduler. Notably, disconnect and/or log on failure.
/// </summary>
/// <param name="handler"></param>
/// <param name="backgroundTaskScheduler"></param>
public class BackgroundTaskSchedulerWrapper(ProtocolHandlerBase handler, IBackgroundTaskScheduler backgroundTaskScheduler)
{
    internal bool TryScheduleSyncServe<TReq, TRes>(TReq request, Func<TReq, CancellationToken, Task<TRes>> fulfillFunc) where TRes : P2PMessage
    {
        SyncServeTaskRequest<TReq, TRes> syncServeRequest = new(handler, request, fulfillFunc);
        if (!backgroundTaskScheduler.TryScheduleTask(syncServeRequest, SyncServeTaskRequestRunner<TReq, TRes>.Run))
        {
            request.TryDispose();
            return false;
        }

        return true;
    }

    internal bool TryScheduleSyncServe<THandler, TReq, TRes, TRequestHandler>(THandler requestHandler, TReq request)
        where THandler : ProtocolHandlerBase
        where TRes : P2PMessage
        where TRequestHandler : struct, ISyncServeRequestHandler<THandler, TReq, TRes>
    {
        HandlerSyncServeTaskRequest<THandler, TReq, TRes, TRequestHandler> syncServeRequest = new(requestHandler, request);
        if (!backgroundTaskScheduler.TryScheduleTask(syncServeRequest, HandlerSyncServeTaskRequestRunner<THandler, TReq, TRes, TRequestHandler>.Run))
        {
            request.TryDispose();
            return false;
        }

        return true;
    }

    internal bool TryScheduleSyncServe<TReq, TRes>(TReq request, Func<TReq, CancellationToken, ValueTask<TRes>> fulfillFunc) where TRes : P2PMessage
    {
        SyncServeValueTaskRequest<TReq, TRes> syncServeRequest = new(handler, request, fulfillFunc);
        if (!backgroundTaskScheduler.TryScheduleTask(syncServeRequest, SyncServeValueTaskRequestRunner<TReq, TRes>.Run))
        {
            request.TryDispose();
            return false;
        }

        return true;
    }

    internal bool TryScheduleBackgroundTask<TReq>(TReq request, Func<TReq, CancellationToken, ValueTask> fulfillFunc) =>
        TryScheduleBackgroundTask(new BackgroundTaskRequest<TReq>(handler, request, fulfillFunc), request);

    private bool TryScheduleBackgroundTask<TReq>(BackgroundTaskRequest<TReq> backgroundTaskRequest, TReq request)
    {
        if (backgroundTaskScheduler.TryScheduleTask(backgroundTaskRequest, BackgroundTaskRequestRunner<TReq>.Run))
        {
            return true;
        }

        request.TryDispose();
        return false;
    }

    private static void HandleBackgroundTaskFailure(ProtocolHandlerBase handler, Exception e)
    {
        DisconnectReason disconnectReason = e switch
        {
            EthSyncException => DisconnectReason.EthSyncException,
            RlpLimitException => DisconnectReason.MessageLimitsBreached,
            RlpException => DisconnectReason.BreachOfProtocol,
            _ => DisconnectReason.BackgroundTaskFailure
        };

        handler.Session.InitiateDisconnect(disconnectReason, e.Message);
        if (handler.Logger.IsDebug) handler.Logger.Debug($"Failure running background task on session {handler.Session}, {e}");
    }

    private readonly struct SyncServeTaskRequest<TReq, TRes>(
        ProtocolHandlerBase handler,
        TReq request,
        Func<TReq, CancellationToken, Task<TRes>> fulfillFunc) : IBackgroundTaskRequest<SyncServeTaskRequest<TReq, TRes>>
        where TRes : P2PMessage
    {
        public static int TaskId => BackgroundTaskTypeId<SyncServeTaskRequest<TReq, TRes>>.Id;

        public async Task Execute(CancellationToken cancellationToken)
        {
            try
            {
                TRes response = await fulfillFunc(request, cancellationToken);
                handler.Send(response);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && handler.Session.IsClosing)
            {
                // Session shutdown or disconnect canceled the task; do not treat it as a background task failure.
                return;
            }
            catch (Exception e)
            {
                HandleBackgroundTaskFailure(handler, e);
            }
        }
    }

    private readonly struct HandlerSyncServeTaskRequest<THandler, TReq, TRes, TRequestHandler>(
        THandler handler,
        TReq request) : IBackgroundTaskRequest<HandlerSyncServeTaskRequest<THandler, TReq, TRes, TRequestHandler>>
        where THandler : ProtocolHandlerBase
        where TRes : P2PMessage
        where TRequestHandler : struct, ISyncServeRequestHandler<THandler, TReq, TRes>
    {
        public static int TaskId => BackgroundTaskTypeId<HandlerSyncServeTaskRequest<THandler, TReq, TRes, TRequestHandler>>.Id;

        public async Task Execute(CancellationToken cancellationToken)
        {
            try
            {
                TRes response = await TRequestHandler.Execute(handler, request, cancellationToken);
                handler.Send(response);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && handler.Session.IsClosing)
            {
                // Session shutdown or disconnect canceled the task; do not treat it as a background task failure.
                return;
            }
            catch (Exception e)
            {
                HandleBackgroundTaskFailure(handler, e);
            }
        }
    }

    private readonly struct SyncServeValueTaskRequest<TReq, TRes>(
        ProtocolHandlerBase handler,
        TReq request,
        Func<TReq, CancellationToken, ValueTask<TRes>> fulfillFunc) : IBackgroundTaskRequest<SyncServeValueTaskRequest<TReq, TRes>>
        where TRes : P2PMessage
    {
        public static int TaskId => BackgroundTaskTypeId<SyncServeValueTaskRequest<TReq, TRes>>.Id;

        public async Task Execute(CancellationToken cancellationToken)
        {
            try
            {
                TRes response = await fulfillFunc(request, cancellationToken);
                handler.Send(response);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && handler.Session.IsClosing)
            {
                // Session shutdown or disconnect canceled the task; do not treat it as a background task failure.
                return;
            }
            catch (Exception e)
            {
                HandleBackgroundTaskFailure(handler, e);
            }
        }
    }

    private readonly struct BackgroundTaskRequest<TReq>(
        ProtocolHandlerBase handler,
        TReq request,
        Func<TReq, CancellationToken, ValueTask> fulfillFunc) : IBackgroundTaskRequest<BackgroundTaskRequest<TReq>>
    {
        public static int TaskId => BackgroundTaskTypeId<BackgroundTaskRequest<TReq>>.Id;

        public async Task Execute(CancellationToken cancellationToken)
        {
            try
            {
                await fulfillFunc(request, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && handler.Session.IsClosing)
            {
                // Session shutdown or disconnect canceled the task; do not treat it as a background task failure.
                return;
            }
            catch (Exception e)
            {
                HandleBackgroundTaskFailure(handler, e);
            }
        }
    }

    private static class SyncServeTaskRequestRunner<TReq, TRes>
        where TRes : P2PMessage
    {
        public static readonly Func<SyncServeTaskRequest<TReq, TRes>, CancellationToken, Task> Run =
            static (request, cancellationToken) => request.Execute(cancellationToken);
    }

    private static class HandlerSyncServeTaskRequestRunner<THandler, TReq, TRes, TRequestHandler>
        where THandler : ProtocolHandlerBase
        where TRes : P2PMessage
        where TRequestHandler : struct, ISyncServeRequestHandler<THandler, TReq, TRes>
    {
        public static readonly Func<HandlerSyncServeTaskRequest<THandler, TReq, TRes, TRequestHandler>, CancellationToken, Task> Run =
            static (request, cancellationToken) => request.Execute(cancellationToken);
    }

    private static class SyncServeValueTaskRequestRunner<TReq, TRes>
        where TRes : P2PMessage
    {
        public static readonly Func<SyncServeValueTaskRequest<TReq, TRes>, CancellationToken, Task> Run =
            static (request, cancellationToken) => request.Execute(cancellationToken);
    }

    private static class BackgroundTaskRequestRunner<TReq>
    {
        public static readonly Func<BackgroundTaskRequest<TReq>, CancellationToken, Task> Run =
            static (request, cancellationToken) => request.Execute(cancellationToken);
    }
}
