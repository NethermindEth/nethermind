// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core.Extensions;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols;
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
/// <remarks>
/// Scheduler cancellation and local serving failures do not penalize the peer.
/// Network startup validates the configured and reported scheduler capacities against the minimum serving envelope.
/// An unreported capacity uses bounded compatibility quotas without a scheduler-wide identity guarantee.
/// Non-network producers share the scheduler queue outside these per-peer quotas.
/// </remarks>
/// <param name="handler"></param>
/// <param name="backgroundTaskScheduler"></param>
public class BackgroundTaskSchedulerWrapper(ProtocolHandlerBase handler, IBackgroundTaskScheduler backgroundTaskScheduler, TimeProvider timeProvider)
{
    internal const int MaxSyncServeRequestsInFlight = 32;
    private int _syncServeRequestsInFlight;
    private int _txServeRequestsInFlight;

    private static readonly ConditionalWeakTable<IBackgroundTaskScheduler, PeerWorkQuota> PeerQuotas = [];
    private readonly PeerWorkQuota _peerQuota = PeerQuotas.GetValue(backgroundTaskScheduler, static scheduler => new PeerWorkQuota(scheduler.QueueCapacity));

    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly NodeFilter.IpSubnetKey? _source = IPAddress.TryParse(handler.Session.RemoteHost, out IPAddress? address)
        ? NodeFilter.IpSubnetKey.DefaultKey(address, v6BucketPrefixBits: 56)
        : null;

    internal bool TryScheduleSyncServe<TReq, TRes>(TReq request, Func<TReq, CancellationToken, Task<TRes>> fulfillFunc) where TRes : P2PMessage
    {
        if (!TryReserveSyncServe(request, out PeerWorkQuota.Reservation? reservation)) return false;
        SyncServeTaskRequest<TReq, TRes> syncServeRequest = new(this, handler, request, fulfillFunc, reservation);
        if (!TryScheduleTask(syncServeRequest, SyncServeTaskRequestRunner<TReq, TRes>.Run, reservation))
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
        if (!TryReserveSyncServe(request, out PeerWorkQuota.Reservation? reservation)) return false;
        HandlerSyncServeTaskRequest<THandler, TReq, TRes, TRequestHandler> syncServeRequest = new(this, requestHandler, request, reservation);
        if (!TryScheduleTask(syncServeRequest, HandlerSyncServeTaskRequestRunner<THandler, TReq, TRes, TRequestHandler>.Run, reservation))
        {
            ReleaseSyncServe<TReq>();
            request.TryDispose();
            return false;
        }

        return true;
    }

    internal bool TryScheduleSyncServe<TReq, TRes>(TReq request, Func<TReq, CancellationToken, ValueTask<TRes>> fulfillFunc) where TRes : P2PMessage
    {
        if (!TryReserveSyncServe(request, out PeerWorkQuota.Reservation? reservation)) return false;
        SyncServeValueTaskRequest<TReq, TRes> syncServeRequest = new(this, handler, request, fulfillFunc, reservation);
        if (!TryScheduleTask(syncServeRequest, SyncServeValueTaskRequestRunner<TReq, TRes>.Run, reservation))
        {
            ReleaseSyncServe<TReq>();
            request.TryDispose();
            return false;
        }

        return true;
    }

    internal bool TryScheduleBackgroundTask<TReq>(TReq request, Func<TReq, CancellationToken, ValueTask> fulfillFunc,
        bool solicited = false, Func<TReq, IDisposable?, TReq>? bindReservation = null,
        PeerWorkQuota.Reservation? replyReservation = null, bool cellsReply = false, bool serving = false)
    {
        PeerWorkQuota.Reservation? reservation = null;
        if (solicited)
        {
            if (handler.Session.IsClosing)
            {
                request.TryDispose();
                return false;
            }
            if (replyReservation is not null) reservation = replyReservation.Retain();
            else if (!TryReserveReply(cellsReply ? PeerWorkQuota.WorkKind.CellsReply : PeerWorkQuota.WorkKind.PooledReply, out reservation))
            {
                request.TryDispose();
                return false;
            }
        }
        else if (!TryReserve(request, out reservation, serving)) return false;
        try
        {
            if (bindReservation is not null) request = bindReservation(request, reservation);
            BackgroundTaskRequest<TReq> backgroundTaskRequest = new(handler, request, fulfillFunc, reservation, peerPayload: !serving);
            if (TryScheduleTask(backgroundTaskRequest, BackgroundTaskRequestRunner<TReq>.Run, reservation)) return true;
        }
        catch (Exception exception)
        {
            reservation?.Dispose();
            if (handler.Logger.IsDebug) handler.Logger.Debug($"Could not prepare background request for {handler.Session.Node:c}: {exception}");
        }

        request.TryDispose();
        return false;
    }

    private bool TryReserveSyncServe<TReq>(TReq request, out PeerWorkQuota.Reservation? reservation)
    {
        reservation = null;
        if (!TryReserveSyncServe(request)) return false;
        if (TryReserve(request, out reservation, serving: true)) return true;
        ReleaseSyncServe<TReq>();
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
        ref typeof(TReq).IsAssignableTo(typeof(ITxServeRequest))
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

    internal TimeProvider TimeProvider => _timeProvider;

    internal bool TryReservePooledReply([NotNullWhen(true)] out PeerWorkQuota.Reservation? reservation)
        => TryReserveReply(PeerWorkQuota.WorkKind.PooledReply, out reservation);

    private bool TryReserveReply(PeerWorkQuota.WorkKind kind, [NotNullWhen(true)] out PeerWorkQuota.Reservation? reservation)
    {
        reservation = null;
        return !handler.Session.IsClosing && _peerQuota.TryReserve(handler.Session.Node.Id, kind, null, out reservation, _source);
    }

    /// <summary>Reschedules work while retaining its existing identity reservation.</summary>
    /// <remarks>The caller retains ownership of the request when this returns false.</remarks>
    internal bool TryRescheduleBackgroundTask<TReq>(TReq request, Func<TReq, CancellationToken, ValueTask> fulfillFunc,
        IDisposable? existingReservation, Func<TReq, IDisposable?, TReq> bindReservation)
    {
        if (handler.Session.IsClosing || existingReservation is not PeerWorkQuota.Reservation existing) return false;
        PeerWorkQuota.Reservation? reservation = null;
        try
        {
            reservation = existing.Retain();
            request = bindReservation(request, reservation);
            BackgroundTaskRequest<TReq> backgroundTaskRequest = new(handler, request, fulfillFunc, reservation);
            return TryScheduleTask(backgroundTaskRequest, BackgroundTaskRequestRunner<TReq>.Run, reservation);
        }
        catch (Exception e)
        {
            reservation?.Dispose();
            if (handler.Logger.IsDebug) handler.Logger.Debug($"Failure rescheduling background task on session {handler.Session:hide}, {e:hide}");
            return false;
        }
    }

    private bool TryReserve<TReq>(TReq request, [NotNullWhen(true)] out PeerWorkQuota.Reservation? reservation, bool serving = false)
    {
        reservation = null;
        if (!handler.Session.IsClosing)
        {
            (string Protocol, int PacketType)? servingClass = serving && request is P2PMessage message
                ? (handler.ProtocolCode, message.PacketType) : null;
            if (_peerQuota.TryReserve(handler.Session.Node.Id, PeerWorkQuota.WorkKind.Ordinary, servingClass, out reservation, _source)) return true;
            Interlocked.Increment(ref Metrics.PeerBackgroundRequestsDropped);
            if (handler.Logger.IsDebug) handler.Logger.Debug($"Dropped background work from {handler.Session:hide}: peer quota full");
        }
        request.TryDispose();
        return false;
    }

    private bool TryScheduleTask<TReq>(TReq request, Func<TReq, CancellationToken, Task> execute, PeerWorkQuota.Reservation? reservation)
        where TReq : notnull, IBackgroundTaskRequest<TReq>
    {
        bool accepted = false;
        try
        {
            accepted = backgroundTaskScheduler.TryScheduleTask(request, execute);
            return accepted;
        }
        catch (Exception e)
        {
            if (handler.Logger.IsDebug) handler.Logger.Debug($"Failure scheduling background task on session {handler.Session:hide}, {e:hide}");
            return false;
        }
        finally
        {
            if (!accepted) reservation?.Dispose();
        }
    }

    private static void HandleBackgroundTaskFailure(ProtocolHandlerBase handler, Exception e, bool peerPayload = false)
    {
        DisconnectReason? disconnectReason = e switch
        {
            InvalidPeerRequestException invalid => invalid.Reason,
            EthSyncException when peerPayload => DisconnectReason.EthSyncException,
            SubprotocolException when peerPayload => DisconnectReason.BreachOfProtocol,
            _ => null
        };

        if (disconnectReason is { } reason) handler.Session.InitiateDisconnect(reason, e.Message);
        if (handler.Logger.IsDebug) handler.Logger.Debug($"Failure running background task on session {handler.Session:hide}, {e:hide}");
    }

    private readonly struct SyncServeTaskRequest<TReq, TRes>(
        BackgroundTaskSchedulerWrapper owner,
        ProtocolHandlerBase handler,
        TReq request,
        Func<TReq, CancellationToken, Task<TRes>> fulfillFunc,
        PeerWorkQuota.Reservation? reservation) : IBackgroundTaskRequest<SyncServeTaskRequest<TReq, TRes>>
        where TRes : P2PMessage
    {
        public static int TaskId => BackgroundTaskTypeId<TReq>.Id;

        public async Task Execute(CancellationToken cancellationToken)
        {
            using PeerWorkQuota.Reservation? lease = reservation;
            if (handler.Session.IsClosing) cancellationToken = new CancellationToken(true);
            try
            {
                if (SkipIfResponseCannotBeSent(handler, request)) return;

                TRes response = await fulfillFunc(request, cancellationToken);
                handler.Send(response);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
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
        TReq request,
        PeerWorkQuota.Reservation? reservation) : IBackgroundTaskRequest<HandlerSyncServeTaskRequest<THandler, TReq, TRes, TRequestHandler>>
        where THandler : ProtocolHandlerBase
        where TRes : P2PMessage
        where TRequestHandler : struct, ISyncServeRequestHandler<THandler, TReq, TRes>
    {
        public static int TaskId => BackgroundTaskTypeId<TReq>.Id;

        public async Task Execute(CancellationToken cancellationToken)
        {
            using PeerWorkQuota.Reservation? lease = reservation;
            if (handler.Session.IsClosing) cancellationToken = new CancellationToken(true);
            try
            {
                if (SkipIfResponseCannotBeSent(handler, request)) return;

                TRes response = await TRequestHandler.Execute(handler, request, cancellationToken);
                handler.Send(response);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
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
        Func<TReq, CancellationToken, ValueTask<TRes>> fulfillFunc,
        PeerWorkQuota.Reservation? reservation) : IBackgroundTaskRequest<SyncServeValueTaskRequest<TReq, TRes>>
        where TRes : P2PMessage
    {
        public static int TaskId => BackgroundTaskTypeId<TReq>.Id;

        public async Task Execute(CancellationToken cancellationToken)
        {
            using PeerWorkQuota.Reservation? lease = reservation;
            if (handler.Session.IsClosing) cancellationToken = new CancellationToken(true);
            try
            {
                if (SkipIfResponseCannotBeSent(handler, request)) return;

                TRes response = await fulfillFunc(request, cancellationToken);
                handler.Send(response);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
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
        Func<TReq, CancellationToken, ValueTask> fulfillFunc,
        PeerWorkQuota.Reservation? reservation,
        bool peerPayload = true) : IBackgroundTaskRequest<BackgroundTaskRequest<TReq>>
    {
        public static int TaskId => BackgroundTaskTypeId<TReq>.Id;

        public async Task Execute(CancellationToken cancellationToken)
        {
            using PeerWorkQuota.Reservation? lease = reservation;
            if (handler.Session.IsClosing) cancellationToken = new CancellationToken(true);
            try
            {
                await fulfillFunc(request, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                HandleBackgroundTaskFailure(handler, e, peerPayload);
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
