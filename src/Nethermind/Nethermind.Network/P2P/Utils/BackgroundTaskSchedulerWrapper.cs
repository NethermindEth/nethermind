// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core.Extensions;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V72.Messages;
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
    /// <summary>The maximum number of sync-serve requests of one protocol handler that can be queued or running.</summary>
    /// <remarks>
    /// The scheduler queue is shared by all peers, so without a limit one peer can fill it and delay the requests of all other peers.
    /// A peer cannot have more requests in flight on this side than it has outstanding. go-ethereum has one request for each
    /// request type. Nethermind with the default <c>ISyncConfig.AllocationSlots</c> has at most 9 sync requests on eth and 4 on snap.
    /// The transaction requests (<see cref="GetPooledTransactionsMessage"/> and <see cref="GetCellsMessage72"/>) have their own
    /// counter with the same limit: the requester sends one for each announcement, and they wait in the scheduler queue during
    /// block processing, so they must not use up the slots of the sync requests.
    /// The extra requests are dropped, the same as when the scheduler queue is full.
    /// </remarks>
    internal const int MaxSyncServeRequestsInFlight = 32;

    private int _syncServeRequestsInFlight;
    private int _txServeRequestsInFlight;

    internal bool TryScheduleSyncServe<TReq, TRes>(TReq request, Func<TReq, CancellationToken, Task<TRes>> fulfillFunc) where TRes : P2PMessage
    {
        if (!TryReserveSyncServe(request)) return false;

        SyncServeTaskRequest<TReq, TRes> syncServeRequest = new(this, handler, request, fulfillFunc);
        if (!backgroundTaskScheduler.TryScheduleTask(syncServeRequest, SyncServeTaskRequestRunner<TReq, TRes>.Run))
        {
            ReleaseSyncServe<TReq>();
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
        if (!TryReserveSyncServe(request)) return false;

        HandlerSyncServeTaskRequest<THandler, TReq, TRes, TRequestHandler> syncServeRequest = new(this, requestHandler, request);
        if (!backgroundTaskScheduler.TryScheduleTask(syncServeRequest, HandlerSyncServeTaskRequestRunner<THandler, TReq, TRes, TRequestHandler>.Run))
        {
            ReleaseSyncServe<TReq>();
            request.TryDispose();
            return false;
        }

        return true;
    }

    internal bool TryScheduleSyncServe<TReq, TRes>(TReq request, Func<TReq, CancellationToken, ValueTask<TRes>> fulfillFunc) where TRes : P2PMessage
    {
        if (!TryReserveSyncServe(request)) return false;

        SyncServeValueTaskRequest<TReq, TRes> syncServeRequest = new(this, handler, request, fulfillFunc);
        if (!backgroundTaskScheduler.TryScheduleTask(syncServeRequest, SyncServeValueTaskRequestRunner<TReq, TRes>.Run))
        {
            ReleaseSyncServe<TReq>();
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

    private bool TryReserveSyncServe<TReq>(TReq request)
    {
        if (Interlocked.Increment(ref InFlightCounter<TReq>()) <= MaxSyncServeRequestsInFlight)
        {
            return true;
        }

        ReleaseSyncServe<TReq>();
        request.TryDispose();
        Interlocked.Increment(ref Metrics.SyncServeRequestsDroppedOverPeerLimit);
        if (handler.Logger.IsDebug) handler.Logger.Debug($"Dropping {typeof(TReq).Name} on session {handler.Session}, too many sync-serve requests in flight");
        return false;
    }

    private void ReleaseSyncServe<TReq>() => Interlocked.Decrement(ref InFlightCounter<TReq>());

    private ref int InFlightCounter<TReq>() =>
        ref typeof(TReq) == typeof(GetPooledTransactionsMessage) || typeof(TReq) == typeof(GetCellsMessage72)
            ? ref _txServeRequestsInFlight
            : ref _syncServeRequestsInFlight;

    /// <summary>Disposes the request when the transport channel of the peer cannot take new messages.</summary>
    /// <remarks>
    /// The session drops a message when the outbound buffer is full or the channel is closed, so making the response would only waste work.
    /// The fulfill function does not run, so its request validation (and a disconnect for an invalid request) is skipped too.
    /// </remarks>
    /// <returns><see langword="true"/> when the request was disposed and must not be served.</returns>
    private static bool SkipIfResponseCannotBeSent<TReq>(ProtocolHandlerBase handler, TReq request)
    {
        if (!handler.Session.IsOutboundBufferFull) return false;

        request.TryDispose();
        if (handler.Logger.IsTrace) handler.Logger.Trace($"Skipping {typeof(TReq).Name} on session {handler.Session}, outbound buffer is full or channel is closed");
        return true;
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
        if (handler.Logger.IsDebug) handler.Logger.Debug($"Failure running background task on session {handler.Session:hide}, {e:hide}");
    }

    private readonly struct SyncServeTaskRequest<TReq, TRes>(
        BackgroundTaskSchedulerWrapper owner,
        ProtocolHandlerBase handler,
        TReq request,
        Func<TReq, CancellationToken, Task<TRes>> fulfillFunc) : IBackgroundTaskRequest<SyncServeTaskRequest<TReq, TRes>>
        where TRes : P2PMessage
    {
        public static int TaskId => BackgroundTaskTypeId<TReq>.Id;

        public async Task Execute(CancellationToken cancellationToken)
        {
            try
            {
                if (SkipIfResponseCannotBeSent(handler, request)) return;

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
            finally
            {
                owner.ReleaseSyncServe<TReq>();
            }
        }
    }

    private readonly struct HandlerSyncServeTaskRequest<THandler, TReq, TRes, TRequestHandler>(
        BackgroundTaskSchedulerWrapper owner,
        THandler handler,
        TReq request) : IBackgroundTaskRequest<HandlerSyncServeTaskRequest<THandler, TReq, TRes, TRequestHandler>>
        where THandler : ProtocolHandlerBase
        where TRes : P2PMessage
        where TRequestHandler : struct, ISyncServeRequestHandler<THandler, TReq, TRes>
    {
        public static int TaskId => BackgroundTaskTypeId<TReq>.Id;

        public async Task Execute(CancellationToken cancellationToken)
        {
            try
            {
                if (SkipIfResponseCannotBeSent(handler, request)) return;

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
            finally
            {
                owner.ReleaseSyncServe<TReq>();
            }
        }
    }

    private readonly struct SyncServeValueTaskRequest<TReq, TRes>(
        BackgroundTaskSchedulerWrapper owner,
        ProtocolHandlerBase handler,
        TReq request,
        Func<TReq, CancellationToken, ValueTask<TRes>> fulfillFunc) : IBackgroundTaskRequest<SyncServeValueTaskRequest<TReq, TRes>>
        where TRes : P2PMessage
    {
        public static int TaskId => BackgroundTaskTypeId<TReq>.Id;

        public async Task Execute(CancellationToken cancellationToken)
        {
            try
            {
                if (SkipIfResponseCannotBeSent(handler, request)) return;

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
            finally
            {
                owner.ReleaseSyncServe<TReq>();
            }
        }
    }

    private readonly struct BackgroundTaskRequest<TReq>(
        ProtocolHandlerBase handler,
        TReq request,
        Func<TReq, CancellationToken, ValueTask> fulfillFunc) : IBackgroundTaskRequest<BackgroundTaskRequest<TReq>>
    {
        public static int TaskId => BackgroundTaskTypeId<TReq>.Id;

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
