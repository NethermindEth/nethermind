// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Wit.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;

namespace Nethermind.Network.P2P.Subprotocols.Wit
{
    public class WitProtocolHandler : ZeroProtocolHandlerBase, IWitnessPeer
    {
        private readonly ISyncServer _syncServer;

        private readonly MessageQueue<GetBlockWitnessHashesMessage, Hash256[]> _witnessRequests;

        public WitProtocolHandler(ISession session,
            IMessageSerializationService serializer,
            INodeStatsManager nodeStats,
            ISyncServer syncServer,
            ILogManager logManager) : base(session, nodeStats, serializer, logManager)
        {
            _syncServer = syncServer ?? throw new ArgumentNullException(nameof(syncServer));
            _witnessRequests = new MessageQueue<GetBlockWitnessHashesMessage, Hash256[]>(Send);
        }

        public override byte ProtocolVersion => 0;

        public override string ProtocolCode => Protocol.Wit;

        public override int MessageIdSpaceSize => 3;

        public override string Name => "wit0";

        protected override TimeSpan InitTimeout => Timeouts.Eth;

        public override event EventHandler<ProtocolInitializedEventArgs> ProtocolInitialized;

        public override event EventHandler<ProtocolEventArgs> SubprotocolRequested
        {
            add { }
            remove { }
        }

        public override void Init()
        {
            ProtocolInitialized?.Invoke(this, new ProtocolInitializedEventArgs(this));
            // GetBlockWitnessHashes(Keccak.Zero, CancellationToken.None);
        }

        public override void HandleMessage(ZeroPacket message)
        {
            int size = message.Content.ReadableBytes;
            int packetType = message.PacketType;
            switch (packetType)
            {
                case WitMessageCode.GetBlockWitnessHashes:
                    {
                        using GetBlockWitnessHashesMessage requestMsg = Deserialize<GetBlockWitnessHashesMessage>(message.Content);
                        ReportIn(requestMsg, size);
                        Handle(requestMsg);
                        break;
                    }
                case WitMessageCode.BlockWitnessHashes:
                    {
                        BlockWitnessHashesMessage responseMsg = Deserialize<BlockWitnessHashesMessage>(message.Content);
                        ReportIn(responseMsg, size);
                        Handle(responseMsg, size);
                        break;
                    }
            }
        }

        private void Handle(GetBlockWitnessHashesMessage requestMsg)
        {
            Hash256[] hashes = _syncServer.GetBlockWitnessHashes(requestMsg.BlockHash);
            BlockWitnessHashesMessage msg = new(requestMsg.RequestId, hashes);
            Send(msg);
        }

        private void Handle(BlockWitnessHashesMessage responseMsg, long size)
        {
            _witnessRequests.Handle(responseMsg.Hashes, size);
        }

        private static long _requestId;

        public async Task<Hash256[]> GetBlockWitnessHashes(Hash256 blockHash, CancellationToken token)
        {
            long requestId = Interlocked.Increment(ref _requestId);
            GetBlockWitnessHashesMessage msg = new(requestId, blockHash);

            if (Logger.IsTrace) Logger.Trace(
                $"{Counter:D5} {nameof(WitMessageCode.GetBlockWitnessHashes)} to {Session}");
            Hash256[] witnessHashes = await SendRequest(msg, token);
            return witnessHashes;
        }

        private async Task<Hash256[]> SendRequest(GetBlockWitnessHashesMessage message, CancellationToken token)
        {
            if (Logger.IsTrace)
            {
                Logger.Trace($"Sending block witness hashes request: {message.BlockHash}");
            }

            Request<GetBlockWitnessHashesMessage, Hash256[]> request = new(message);
            _witnessRequests.Send(request);

            Task<Hash256[]> task = request.CompletionSource.Task;
            using CancellationTokenSource delayCancellation = new();
            using CancellationTokenSource compositeCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, delayCancellation.Token);
            Task firstTask = await Task.WhenAny(task, Task.Delay(Timeouts.Eth, compositeCancellation.Token));
            if (firstTask.IsCanceled)
            {
                token.ThrowIfCancellationRequested();
            }

            if (firstTask == task)
            {
                delayCancellation.Cancel();
                return task.Result;
            }

            throw new TimeoutException($"{Session} Request timeout in {nameof(GetBlockWitnessHashes)} for {message.BlockHash}");
        }

        #region Cleanup

        private int _isDisposed;

        public override void DisconnectProtocol(DisconnectReason disconnectReason, string details)
        {
            Dispose();
        }

        public override void Dispose()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) == 0) { }
        }

        #endregion
    }
}
