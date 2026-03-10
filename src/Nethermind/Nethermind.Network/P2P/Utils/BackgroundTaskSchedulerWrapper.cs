// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Scheduler;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;

namespace Nethermind.Network.P2P.Utils;

/// <summary>
/// Some utility function for interacting with BackgroundTaskScheduler. Notably, disconnect and/or log on failure.
/// </summary>
/// <param name="handler"></param>
/// <param name="backgroundTaskScheduler"></param>
public class BackgroundTaskSchedulerWrapper(ProtocolHandlerBase handler, IBackgroundTaskScheduler backgroundTaskScheduler)
{
    private readonly ProtocolHandlerBase _handler = handler;

    internal bool TryScheduleSyncServe<TReq, TRes>(TReq request, Func<TReq, CancellationToken, Task<TRes>> fulfillFunc) where TRes : P2PMessage =>
        TryScheduleBackgroundTask((Wrapper: this, Request: request, FulfillFunc: fulfillFunc), SyncSenderCache<TReq, TRes>.Handler);

    internal bool TryScheduleSyncServe<TReq, TRes>(TReq request, Func<TReq, CancellationToken, ValueTask<TRes>> fulfillFunc) where TRes : P2PMessage =>
        TryScheduleBackgroundTask((Wrapper: this, Request: request, FulfillFunc: fulfillFunc), SyncSenderValueTaskCache<TReq, TRes>.Handler);

    internal bool TryScheduleBackgroundTask<TReq>(TReq request, Func<TReq, CancellationToken, ValueTask> fulfillFunc) =>
        backgroundTaskScheduler.TryScheduleTask((Wrapper: this, Request: request, BackgroundTask: fulfillFunc), FailureHandlerCache<TReq>.Handler);

    internal bool TryScheduleBackgroundTask<THandler, TReq>(
        THandler backgroundTaskHandler,
        TReq request,
        Func<THandler, TReq, CancellationToken, ValueTask> fulfillFunc) =>
        backgroundTaskScheduler.TryScheduleTask(
            (Wrapper: this, Handler: backgroundTaskHandler, Request: request, BackgroundTask: fulfillFunc),
            FailureHandlerWithStateCache<THandler, TReq>.Handler);

    private static async ValueTask BackgroundSyncSender<TReq, TRes>(
        (BackgroundTaskSchedulerWrapper Wrapper, TReq Request, Func<TReq, CancellationToken, Task<TRes>> FulfillFunc) input, CancellationToken cancellationToken) where TRes : P2PMessage
    {
        TRes response = await input.FulfillFunc(input.Request, cancellationToken);
        input.Wrapper._handler.Send(response);
    }

    private static async ValueTask BackgroundSyncSenderValueTask<TReq, TRes>(
        (BackgroundTaskSchedulerWrapper Wrapper, TReq Request, Func<TReq, CancellationToken, ValueTask<TRes>> FulfillFunc) input, CancellationToken cancellationToken) where TRes : P2PMessage
    {
        TRes response = await input.FulfillFunc(input.Request, cancellationToken);
        input.Wrapper._handler.Send(response);
    }

    private static async Task BackgroundTaskFailureHandlerValueTask<TReq>(
        (BackgroundTaskSchedulerWrapper Wrapper, TReq Request, Func<TReq, CancellationToken, ValueTask> BackgroundTask) input,
        CancellationToken cancellationToken)
    {
        try
        {
            await input.BackgroundTask(input.Request, cancellationToken);
        }
        catch (Exception e)
        {
            HandleBackgroundTaskFailure(input.Wrapper, e);
        }
    }

    private static async Task BackgroundTaskFailureHandlerValueTask<THandler, TReq>(
        (BackgroundTaskSchedulerWrapper Wrapper, THandler Handler, TReq Request, Func<THandler, TReq, CancellationToken, ValueTask> BackgroundTask) input,
        CancellationToken cancellationToken)
    {
        try
        {
            await input.BackgroundTask(input.Handler, input.Request, cancellationToken);
        }
        catch (Exception e)
        {
            HandleBackgroundTaskFailure(input.Wrapper, e);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void HandleBackgroundTaskFailure(BackgroundTaskSchedulerWrapper wrapper, Exception e)
    {
        DisconnectReason disconnectReason = e switch
        {
            EthSyncException => DisconnectReason.EthSyncException,
            RlpLimitException => DisconnectReason.MessageLimitsBreached,
            RlpException => DisconnectReason.BreachOfProtocol,
            _ => DisconnectReason.BackgroundTaskFailure
        };

        ProtocolHandlerBase h = wrapper._handler;
        h.Session.InitiateDisconnect(disconnectReason, e.Message);
        if (h.Logger.IsDebug) DebugBackgroundTaskFailure(h, e);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void DebugBackgroundTaskFailure(ProtocolHandlerBase h, Exception e)
            => h.Logger.Debug($"Failure running background task on session {h.Session}, {e}");
    }

    private static class FailureHandlerCache<TReq>
    {
        internal static readonly Func<(BackgroundTaskSchedulerWrapper Wrapper, TReq Request, Func<TReq, CancellationToken, ValueTask> BackgroundTask), CancellationToken, Task>
            Handler = BackgroundTaskFailureHandlerValueTask;
    }

    private static class FailureHandlerWithStateCache<THandler, TReq>
    {
        internal static readonly Func<(BackgroundTaskSchedulerWrapper Wrapper, THandler Handler, TReq Request, Func<THandler, TReq, CancellationToken, ValueTask> BackgroundTask), CancellationToken, Task>
            Handler = BackgroundTaskFailureHandlerValueTask;
    }

    private static class SyncSenderCache<TReq, TRes> where TRes : P2PMessage
    {
        internal static readonly Func<(BackgroundTaskSchedulerWrapper Wrapper, TReq Request, Func<TReq, CancellationToken, Task<TRes>> FulfillFunc), CancellationToken, ValueTask>
            Handler = BackgroundSyncSender;
    }

    private static class SyncSenderValueTaskCache<TReq, TRes> where TRes : P2PMessage
    {
        internal static readonly Func<(BackgroundTaskSchedulerWrapper Wrapper, TReq Request, Func<TReq, CancellationToken, ValueTask<TRes>> FulfillFunc), CancellationToken, ValueTask>
            Handler = BackgroundSyncSenderValueTask;
    }
}
