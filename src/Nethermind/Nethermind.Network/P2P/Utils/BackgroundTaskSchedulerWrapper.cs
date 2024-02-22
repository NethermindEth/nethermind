// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Scheduler;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
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
    internal void ScheduleSyncServe<TReq, TRes>(TReq request, Func<TReq, CancellationToken, Task<TRes>> fulfillFunc) where TRes : P2PMessage
    {
        ScheduleBackgroundTask((request, fulfillFunc), BackgroundSyncSender);
    }

    internal void ScheduleSyncServe<TReq, TRes>(TReq request, Func<TReq, CancellationToken, ValueTask<TRes>> fulfillFunc) where TRes : P2PMessage
    {
        ScheduleBackgroundTask((request, fulfillFunc), BackgroundSyncSenderValueTask);
    }

    internal void ScheduleBackgroundTask<TReq>(TReq request, Func<TReq, CancellationToken, ValueTask> fulfillFunc)
    {
        backgroundTaskScheduler.ScheduleTask((request, fulfillFunc), BackgroundTaskFailureHandlerValueTask);
    }

    // I just don't want to create a closure.. so this happens.
    private async ValueTask BackgroundSyncSender<TReq, TRes>(
        (TReq Request, Func<TReq, CancellationToken, Task<TRes>> FullfillFunc) input, CancellationToken cancellationToken) where TRes : P2PMessage
    {
        TRes response = await input.FullfillFunc.Invoke(input.Request, cancellationToken);
        handler.Send(response);
    }

    private async ValueTask BackgroundSyncSenderValueTask<TReq, TRes>(
        (TReq Request, Func<TReq, CancellationToken, ValueTask<TRes>> FullfillFunc) input, CancellationToken cancellationToken) where TRes : P2PMessage
    {
        TRes response = await input.FullfillFunc.Invoke(input.Request, cancellationToken);
        handler.Send(response);
    }

    private async Task BackgroundTaskFailureHandlerValueTask<TReq>((TReq Request, Func<TReq, CancellationToken, ValueTask> BackgroundTask) input, CancellationToken cancellationToken)
    {
        try
        {
            await input.BackgroundTask.Invoke(input.Request, cancellationToken);
        }
        catch (Exception e)
        {
            if (e is EthSyncException)
            {
                handler.Session.InitiateDisconnect(DisconnectReason.EthSyncException, e.Message);
            }
            else
            {
                handler.Session.InitiateDisconnect(DisconnectReason.BackgroundTaskFailure, e.Message);
            }

            if (handler.Logger.IsDebug) handler.Logger.Debug($"Failure running background task on session {handler.Session}, {e}");
        }
    }
}
