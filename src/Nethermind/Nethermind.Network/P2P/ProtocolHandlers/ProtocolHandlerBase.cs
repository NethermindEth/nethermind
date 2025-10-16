// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Utils;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;

namespace Nethermind.Network.P2P.ProtocolHandlers
{
    public abstract class ProtocolHandlerBase : IProtocolHandler
    {
        public abstract string Name { get; }
        public bool IsPriority { get; set; }
        protected INodeStatsManager StatsManager { get; }
        private readonly IMessageSerializationService _serializer;
        protected internal ISession Session { get; }
        protected long Counter;

        private readonly TaskCompletionSource<MessageBase> _initCompletionSource = new();

        protected ProtocolHandlerBase(ISession session,
            INodeStatsManager nodeStats,
            IMessageSerializationService serializer,
            IBackgroundTaskScheduler backgroundTaskScheduler,
            ILogManager logManager)
        {
            StatsManager = nodeStats ?? throw new ArgumentNullException(nameof(nodeStats));
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            Session = session ?? throw new ArgumentNullException(nameof(session));
            Logger = logManager?.GetClassLogger<ProtocolHandlerBase>() ?? throw new ArgumentNullException(nameof(logManager));
            BackgroundTaskScheduler = new BackgroundTaskSchedulerWrapper(this, backgroundTaskScheduler);
        }

        protected internal ILogger Logger { get; }

        protected abstract TimeSpan InitTimeout { get; }

        protected BackgroundTaskSchedulerWrapper BackgroundTaskScheduler { get; }

        protected T Deserialize<T>(byte[] data) where T : P2PMessage
        {
            var size = data.Length;
            try
            {
                return _serializer.Deserialize<T>(data);
            }
            catch (RlpLimitException e)
            {
                HandleRlpLimitException<T>(size, e);
                throw;
            }
            catch (RlpException e)
            {
                HandleRlpException<T>(size, e);
                throw;
            }
        }

        private void HandleRlpException<T>(int dataLength, RlpException e) where T : P2PMessage
        {
            if (Logger.IsDebug) Logger.Debug($"Failed to deserialize message {typeof(T).Name} on session {Session}, with exception {e}");
            ReportIn($"{typeof(T).Name} - Deserialization exception", dataLength);
        }

        private void HandleRlpLimitException<T>(int dataLength, RlpLimitException e) where T : P2PMessage
        {
            Session.InitiateDisconnect(DisconnectReason.MessageLimitsBreached, e.Message);
            if (Logger.IsDebug) Logger.Debug($"Failed to deserialize message {typeof(T).Name} on session {Session} due to rlp limits, with exception {e}");
            ReportIn($"{typeof(T).Name} - Deserialization limit exception", dataLength);
        }

        protected T Deserialize<T>(IByteBuffer data) where T : P2PMessage
        {
            int size = data.ReadableBytes;
            try
            {
                int originalReaderIndex = data.ReaderIndex;
                T result = _serializer.Deserialize<T>(data);
                if (data.IsReadable()) ThrowIncompleteDeserializationException(data, originalReaderIndex);
                return result;
            }
            catch (RlpLimitException e)
            {
                HandleRlpLimitException<T>(size, e);
                throw;
            }
            catch (RlpException e)
            {
                HandleRlpException<T>(size, e);
                throw;
            }
        }

        [DoesNotReturn]
        private static void ThrowIncompleteDeserializationException(IByteBuffer data, int originalReaderIndex)
        {
            throw new IncompleteDeserializationException($"Incomplete deserialization detected. Buffer is still readable. Read bytes: {data.ReaderIndex - originalReaderIndex}. Readable bytes: {data.ReadableBytes}");
        }

        protected internal void Send<T>(T message) where T : P2PMessage
        {
            Interlocked.Increment(ref Counter);
            if (Logger.IsTrace) Logger.Trace($"{Counter} Sending {typeof(T).Name}");
            if (NetworkDiagTracer.IsEnabled)
            {
                string messageString = message.ToString();
                int size = Session.DeliverMessage(message);
                NetworkDiagTracer.ReportOutgoingMessage(Session.Node?.Address, Name, messageString, size);
            }
            else
                Session.DeliverMessage(message);
        }

        protected async Task CheckProtocolInitTimeout()
        {
            try
            {
                Task<MessageBase> receivedInitMsgTask = _initCompletionSource.Task;
                CancellationTokenSource delayCancellation = new();
                Task firstTask = await Task.WhenAny(receivedInitMsgTask, Task.Delay(InitTimeout, delayCancellation.Token));

                if (firstTask != receivedInitMsgTask)
                {
                    if (Logger.IsTrace)
                    {
                        Logger.Trace($"Disconnecting due to timeout for protocol init message ({Name}): {Session.RemoteNodeId}");
                    }

                    Session.InitiateDisconnect(DisconnectReason.ProtocolInitTimeout, "protocol init timeout");
                }
                else
                {
                    delayCancellation.Cancel();
                }
            }
            catch (Exception e)
            {
                if (Logger.IsError)
                {
                    Logger.Error("Error during p2pProtocol handler timeout logic", e);
                }
            }
        }

        protected void ReceivedProtocolInitMsg(MessageBase msg)
        {
            _initCompletionSource?.SetResult(msg);
        }

        protected void ReportIn(MessageBase msg, int size)
        {
            if (Logger.IsTrace || NetworkDiagTracer.IsEnabled)
            {
                ReportIn(msg.ToString() ?? "", size);
            }
        }

        protected void ReportIn(string messageInfo, int size)
        {
            if (Logger.IsTrace)
                Logger.Trace($"OUT {Counter:D5} {messageInfo}");

            if (NetworkDiagTracer.IsEnabled)
                NetworkDiagTracer.ReportIncomingMessage(Session?.Node?.Address, Name, messageInfo, size);
        }

        protected void HandleInBackground<TReq, TRes>(ZeroPacket message, Func<TReq, CancellationToken, Task<TRes>> handle) where TReq : P2PMessage where TRes : P2PMessage =>
            BackgroundTaskScheduler.ScheduleSyncServe(DeserializeAndReport<TReq>(message), handle);

        protected void HandleInBackground<TReq, TRes>(ZeroPacket message, Func<TReq, CancellationToken, ValueTask<TRes>> handle) where TReq : P2PMessage where TRes : P2PMessage =>
            BackgroundTaskScheduler.ScheduleSyncServe(DeserializeAndReport<TReq>(message), handle);

        protected void HandleInBackground<TReq>(ZeroPacket message, Func<TReq, CancellationToken, ValueTask> handle) where TReq : P2PMessage =>
            BackgroundTaskScheduler.ScheduleBackgroundTask(DeserializeAndReport<TReq>(message), handle);

        private TReq DeserializeAndReport<TReq>(ZeroPacket message) where TReq : P2PMessage
        {
            TReq messageObject = Deserialize<TReq>(message.Content);
            ReportIn(messageObject, message.Content.ReadableBytes);
            return messageObject;
        }

        public abstract void Dispose();

        public abstract byte ProtocolVersion { get; }

        public abstract string ProtocolCode { get; }

        public abstract int MessageIdSpaceSize { get; }

        public abstract void Init();

        public abstract void HandleMessage(Packet message);

        public abstract void DisconnectProtocol(DisconnectReason disconnectReason, string details);

        public abstract event EventHandler<ProtocolInitializedEventArgs> ProtocolInitialized;

        public abstract event EventHandler<ProtocolEventArgs> SubprotocolRequested;
    }

    public class IncompleteDeserializationException(string msg) : Exception(msg);
}
