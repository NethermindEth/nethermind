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
/// Some utility function for interacting with BackgroundTaskScheduler. Notably, disconnect and/or log on failure.
/// </summary>
/// <param name="handler"></param>
/// <param name="backgroundTaskScheduler"></param>
public class BackgroundTaskSchedulerWrapper(ProtocolHandlerBase handler, IBackgroundTaskScheduler backgroundTaskScheduler)
{
    internal bool TryScheduleSyncServe<TReq, TRes>(TReq request, Func<TReq, CancellationToken, Task<TRes>> fulfillFunc) where TRes : P2PMessage
    {
        if (!TryScheduleBackgroundTask((request, fulfillFunc), BackgroundSyncSender, typeof(TReq).Name))
        {
            return false;
        }
        return true;
    }

    internal bool TryScheduleSyncServe<TReq, TRes>(TReq request, Func<TReq, CancellationToken, ValueTask<TRes>> fulfillFunc) where TRes : P2PMessage
    {
        if (!TryScheduleBackgroundTask((request, fulfillFunc), BackgroundSyncSenderValueTask, typeof(TReq).Name))
        {
            return false;
        }
        return true;
    }

    internal bool TryScheduleBackgroundTask<TReq>(TReq request, Func<TReq, CancellationToken, ValueTask> fulfillFunc, string? source = null) =>
        TryScheduleBackgroundTask(new BackgroundTaskRequest<TReq>(handler, request, fulfillFunc), request, source);

    private bool TryScheduleBackgroundTask<TReq>(BackgroundTaskRequest<TReq> backgroundTaskRequest, TReq request, string? source)
    {
        if (backgroundTaskScheduler.TryScheduleTask(backgroundTaskRequest, BackgroundTaskRequestRunner<TReq>.Run, source: source))
        {
            return true;
        }

        request.TryDispose();
        return false;
    }

    // I just don't want to create a closure... so this happens.
    private async ValueTask BackgroundSyncSender<TReq, TRes>(
        (TReq Request, Func<TReq, CancellationToken, Task<TRes>> FulfillFunc) input, CancellationToken cancellationToken) where TRes : P2PMessage
    {
        TRes response = await input.FulfillFunc(input.Request, cancellationToken);
        handler.Send(response);
    }

    private async ValueTask BackgroundSyncSenderValueTask<TReq, TRes>(
        (TReq Request, Func<TReq, CancellationToken, ValueTask<TRes>> FulfillFunc) input, CancellationToken cancellationToken) where TRes : P2PMessage
    {
        TRes response = await input.FulfillFunc(input.Request, cancellationToken);
        handler.Send(response);
    }

    private readonly struct BackgroundTaskRequest<TReq>(
        ProtocolHandlerBase handler,
        TReq request,
        Func<TReq, CancellationToken, ValueTask> fulfillFunc)
    {
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
        }
    }

    private static class BackgroundTaskRequestRunner<TReq>
    {
        public static readonly Func<BackgroundTaskRequest<TReq>, CancellationToken, Task> Run =
            static (request, cancellationToken) => request.Execute(cancellationToken);
    }
}
