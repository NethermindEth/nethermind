// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Common.Utilities;
using Nethermind.Logging;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;

namespace Nethermind.Network.P2P.ProtocolHandlers
{
    public abstract class ZeroProtocolHandlerBase : ProtocolHandlerBase, IZeroProtocolHandler
    {
        protected ZeroProtocolHandlerBase(ISession session, INodeStatsManager nodeStats, IMessageSerializationService serializer, ILogManager logManager)
            : base(session, nodeStats, serializer, logManager)
        {
        }

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

        protected async Task<TResponse> SendRequestGeneric<TRequest, TResponse>(
            MessageQueue<TRequest, TResponse> messageQueue,
            TRequest message,
            TransferSpeedType speedType,
            Func<TRequest, string> describeRequestFunc,
            CancellationToken token
        ) where TRequest : MessageBase
        {
            Request<TRequest, TResponse> request = new(message);
            messageQueue.Send(request);

            return await HandleResponse(request, speedType, describeRequestFunc, token);
        }

        protected async Task<TResponse> HandleResponse<TRequest, TResponse>(
            Request<TRequest, TResponse> request,
            TransferSpeedType speedType,
            Func<TRequest, string> describeRequestFunc,
            CancellationToken token
        )
            where TRequest : MessageBase
        {
            Task<TResponse> task = request.CompletionSource.Task;
            using CancellationTokenSource delayCancellation = new();
            using CancellationTokenSource compositeCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(token, delayCancellation.Token);
            Task firstTask = await Task.WhenAny(task, Task.Delay(Timeouts.Eth, compositeCancellation.Token));
            if (firstTask.IsCanceled)
            {
                token.ThrowIfCancellationRequested();
            }

            if (firstTask == task)
            {
                delayCancellation.Cancel();
                long elapsed = request.FinishMeasuringTime();
                long bytesPerMillisecond = (long)((decimal)request.ResponseSize / Math.Max(1, elapsed));
                if (Logger.IsTrace) Logger.Trace($"{this} speed is {request.ResponseSize}/{elapsed} = {bytesPerMillisecond}");
                StatsManager.ReportTransferSpeedEvent(Session.Node, speedType, bytesPerMillisecond);

                return task.Result;
            }

            StatsManager.ReportTransferSpeedEvent(Session.Node, speedType, 0L);
            throw new TimeoutException($"{Session} Request timeout in {describeRequestFunc(request.Message)}");
        }
    }
}
