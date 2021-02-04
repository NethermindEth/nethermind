//  Copyright (c) 2020 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Witness;

namespace Nethermind.Network.P2P.Subprotocols.Wit
{
    public class WitProtocolHandler : ProtocolHandlerBase, IZeroProtocolHandler, IWitnessPeer
    {
        private readonly ISyncServer _syncServer;

        private readonly MessageQueue<GetBlockWitnessHashesMessage, Keccak[]> _witnessRequests;
        
        public WitProtocolHandler(ISession session,
            IMessageSerializationService serializer,
            INodeStatsManager nodeStats,
            ISyncServer syncServer,
            ILogManager logManager) : base(session, nodeStats, serializer, logManager)
        {
            _syncServer = syncServer ?? throw new ArgumentNullException(nameof(syncServer));
            _witnessRequests = new MessageQueue<GetBlockWitnessHashesMessage, Keccak[]>(Send);
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

        public override void HandleMessage(Packet message)
        {
            ZeroPacket zeroPacket = new ZeroPacket(message);
            HandleMessage(zeroPacket);
            zeroPacket.Release();
        }

        public void HandleMessage(ZeroPacket message)
        {
            int size = message.Content.ReadableBytes;
            int packetType = message.PacketType;
            switch (packetType)
            {
                case WitMessageCode.GetBlockWitnessHashes:
                    GetBlockWitnessHashesMessage requestMsg = Deserialize<GetBlockWitnessHashesMessage>(message.Content);
                    ReportIn(requestMsg);
                    Handle(requestMsg);
                    break;
                case WitMessageCode.BlockWitnessHashes:
                    BlockWitnessHashesMessage responseMsg = Deserialize<BlockWitnessHashesMessage>(message.Content);
                    ReportIn(responseMsg);
                    Handle(responseMsg, size);
                    break;
            }
        }

        private void Handle(GetBlockWitnessHashesMessage requestMsg)
        {
            Keccak[] hashes = _syncServer.GetBlockWitnessHashes(requestMsg.BlockHash);
            BlockWitnessHashesMessage msg = new BlockWitnessHashesMessage(requestMsg.RequestId, hashes);
            Send(msg);
        }

        private void Handle(BlockWitnessHashesMessage responseMsg, long size)
        {
            _witnessRequests.Handle(responseMsg.Hashes, size);
        }

        private static long _requestId;
        
        public async Task<Keccak[]> GetBlockWitnessHashes(Keccak blockHash, CancellationToken token)
        {
            long requestId = Interlocked.Increment(ref _requestId);
            GetBlockWitnessHashesMessage msg = new GetBlockWitnessHashesMessage(requestId, blockHash);

            if (Logger.IsTrace) Logger.Trace(
                $"{Counter:D5} {nameof(WitMessageCode.GetBlockWitnessHashes)} to {Session}");
            Keccak[] witnessHashes = await SendRequest(msg, token);
            return witnessHashes;
        }
        
        private async Task<Keccak[]> SendRequest(GetBlockWitnessHashesMessage message, CancellationToken token)
        {
            if (Logger.IsTrace)
            {
                Logger.Trace($"Sending block witness hashes request: {message.BlockHash}");
            }

            Request<GetBlockWitnessHashesMessage, Keccak[]> request
                = new Request<GetBlockWitnessHashesMessage, Keccak[]>(message);
            _witnessRequests.Send(request);

            Task<Keccak[]> task = request.CompletionSource.Task;
            using CancellationTokenSource delayCancellation = new CancellationTokenSource();
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
