// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Common.Utilities;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;

namespace Nethermind.Network.P2P.ProtocolHandlers
{
    public abstract class ZeroProtocolHandlerBase(
        ISession session,
        INodeStatsManager nodeStats,
        IMessageSerializationService serializer,
        IBackgroundTaskScheduler backgroundTaskScheduler,
        ILogManager logManager)
        : ProtocolHandlerBase(session, nodeStats, serializer, backgroundTaskScheduler, logManager), IZeroProtocolHandler
    {
        protected readonly INodeStats _nodeStats = nodeStats.GetOrAdd(session.Node);

        public override void HandleMessage(Packet message)
        {
            ZeroPacket zeroPacket = new(message);
            try
            {
                HandleMessage(zeroPacket);
            }
            finally
            {
                zeroPacket.SafeRelease();
            }
        }

        public abstract void HandleMessage(ZeroPacket message);

        protected Task<TResponse> SendRequestGeneric<TRequest, TResponse>(
            MessageQueue<TRequest, TResponse> messageQueue,
            TRequest message,
            TransferSpeedType speedType,
            Func<TRequest, string> describeRequestFunc,
            CancellationToken token
        ) where TRequest : MessageBase
        {
            Request<TRequest, TResponse> request = new(message);
            messageQueue.Send(request);

            return HandleResponse(request, speedType, describeRequestFunc, token);
        }

        protected Task<TResponse> HandleResponse<TRequest, TResponse>(
            Request<TRequest, TResponse> request,
            TransferSpeedType speedType,
            Func<TRequest, string> describeRequestFunc,
            CancellationToken token)
            => HandleResponseInner(request, speedType, describeRequestFunc, token).Unwrap();

        private async Task<Task<TResponse>> HandleResponseInner<TRequest, TResponse>(
            Request<TRequest, TResponse> request,
            TransferSpeedType speedType,
            Func<TRequest, string> describeRequestFunc,
            CancellationToken token
        )
        {
            Task<TResponse> task = request.CompletionSource.Task;

            using CancellationTokenSource delayCancellation = new();
            using CancellationTokenSource compositeCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, delayCancellation.Token);
            CancellationToken cancellationToken = compositeCancellation.Token;

            Task firstTask = await Task.WhenAny(task, Task.Delay(Timeouts.Eth, cancellationToken));

            if (ReferenceEquals(firstTask, task))
            {
                long elapsed = request.FinishMeasuringTime();

                delayCancellation.Cancel();

                long bytesPerMillisecond = (long)((decimal)request.ResponseSize / Math.Max(1, elapsed));
                if (Logger.IsTrace) Logger.Trace($"{this} speed is {request.ResponseSize}/{elapsed} = {bytesPerMillisecond}");
                StatsManager.ReportTransferSpeedEvent(Session.Node, speedType, bytesPerMillisecond);
            }
            else
            {
                _ = task.ContinueWith(static t =>
                {
                    if (t.IsCompletedSuccessfully)
                    {
                        t.Result.TryDispose();
                    }
                });

                request.CompletionSource.TrySetCanceled(cancellationToken);
                StatsManager.ReportTransferSpeedEvent(Session.Node, speedType, 0L);

                if (Logger.IsDebug) Logger.Debug($"{Session} Request timeout in {describeRequestFunc(request.Message)}");
            }

            return task;
        }
    }
}
