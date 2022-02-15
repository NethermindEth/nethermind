//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V65;
using Nethermind.Network.P2P.Subprotocols.Eth.V66;
using Nethermind.Network.P2P.Subprotocols.Snap.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.State.Snap;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.Synchronization.SnapSync;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Snap
{
    public class SnapProtocolHandler : ZeroProtocolHandlerBase, ISnapSyncPeer
    {
        public override string Name => "snap1";
        protected override TimeSpan InitTimeout => Timeouts.Eth;

        public override byte ProtocolVersion => 1;
        public override string ProtocolCode => Protocol.Snap;
        public override int MessageIdSpaceSize => 8;

        private readonly MessageQueue<GetAccountRangeMessage, AccountRangeMessage> _getAccountsRequests;


        public SnapProtocolHandler(ISession session, 
            INodeStatsManager nodeStats, 
            IMessageSerializationService serializer, 
            ILogManager logManager) 
            : base(session, nodeStats, serializer, logManager)
        {
            _getAccountsRequests = new(Send);
        }

        public override event EventHandler<ProtocolInitializedEventArgs> ProtocolInitialized;
        public override event EventHandler<ProtocolEventArgs>? SubprotocolRequested
        {
            add { }
            remove { }
        }

        public override void Init()
        {
            ProtocolInitialized?.Invoke(this, new ProtocolInitializedEventArgs(this));
        }

        public override void Dispose()
        {
        }
        
        public override void HandleMessage(ZeroPacket message)
        {
            int size = message.Content.ReadableBytes;

            switch (message.PacketType)
            {
                case SnapMessageCode.GetAccountRange:
                    GetAccountRangeMessage getAccountRangeMessage = Deserialize<GetAccountRangeMessage>(message.Content);
                    ReportIn(getAccountRangeMessage);
                    //Handle(getAccountRangeMessage);
                    break;
                case SnapMessageCode.AccountRange:
                    AccountRangeMessage accountRangeMessage = Deserialize<AccountRangeMessage>(message.Content);
                    ReportIn(accountRangeMessage);
                    Handle(accountRangeMessage, size);
                    break;
                case SnapMessageCode.GetStorageRanges:
                    GetStorageRangesMessage getStorageRangesMessage = Deserialize<GetStorageRangesMessage>(message.Content);
                    ReportIn(getStorageRangesMessage);
                    //Handle(msg);
                    break;
                case SnapMessageCode.StorageRanges:
                    StorageRangesMessage storageRangesMessage = Deserialize<StorageRangesMessage>(message.Content);
                    ReportIn(storageRangesMessage);
                    //Handle(msg);
                    break;
                case SnapMessageCode.GetByteCodes:
                    GetByteCodesMessage getByteCodesMessage = Deserialize<GetByteCodesMessage>(message.Content);
                    ReportIn(getByteCodesMessage);
                    //Handle(msg);
                    break;
                case SnapMessageCode.ByteCodes:
                    ByteCodesMessage byteCodesMessage = Deserialize<ByteCodesMessage>(message.Content);
                    ReportIn(byteCodesMessage);
                    //Handle(msg);
                    break;
                case SnapMessageCode.GetTrieNodes:
                    GetTrieNodesMessage getTrieNodesMessage = Deserialize<GetTrieNodesMessage>(message.Content);
                    ReportIn(getTrieNodesMessage);
                    //Handle(msg);
                    break;
                case SnapMessageCode.TrieNodes:
                    TrieNodesMessage trieNodesMessage = Deserialize<TrieNodesMessage>(message.Content);
                    ReportIn(trieNodesMessage);
                    //Handle(msg);
                    break;
            }
        }

        private void Handle(AccountRangeMessage msg, long size)
        {
            Metrics.Eth63NodeDataReceived++;
            _getAccountsRequests.Handle(msg, size);
        }

        private void Handle(GetAccountRangeMessage msg)
        {
            throw new NotImplementedException();
        }

        public override void DisconnectProtocol(DisconnectReason disconnectReason, string details)
        {
            Dispose();
        }

        public async Task<AccountsAndProofs> GetAccountRange(AccountRange range, CancellationToken token)
        {
            var request = new GetAccountRangeMessage()
            {
                AccountRange = range,
                ResponseBytes = 1000_000
            };

            AccountRangeMessage response = await SendRequest(request, token);

            return new AccountsAndProofs() { PathAndAccounts = response.PathsWithAccounts, Proofs = response.Proofs};
        }

        private async Task<AccountRangeMessage> SendRequest(GetAccountRangeMessage msg, CancellationToken token)
        {
            Request<GetAccountRangeMessage, AccountRangeMessage> batch = new(msg);
            _getAccountsRequests.Send(batch);

            Task<AccountRangeMessage> task = batch.CompletionSource.Task;

            using CancellationTokenSource delayCancellation = new();
            using CancellationTokenSource compositeCancellation
                = CancellationTokenSource.CreateLinkedTokenSource(token, delayCancellation.Token);
            Task firstTask = await Task.WhenAny(task, Task.Delay(Timeouts.Eth, compositeCancellation.Token));
            if (firstTask.IsCanceled)
            {
                token.ThrowIfCancellationRequested();
            }

            if (firstTask == task)
            {
                delayCancellation.Cancel();
                long elapsed = batch.FinishMeasuringTime();
                long bytesPerMillisecond = (long) ((decimal)batch.ResponseSize / Math.Max(1, elapsed));
                if (Logger.IsTrace)
                    Logger.Trace($"{this} speed is {batch.ResponseSize}/{elapsed} = {bytesPerMillisecond}");
                StatsManager.ReportTransferSpeedEvent(Session.Node, TransferSpeedType.NodeData, bytesPerMillisecond);

                return task.Result;
            }
            
            StatsManager.ReportTransferSpeedEvent(Session.Node, TransferSpeedType.NodeData, 0L);
            throw new TimeoutException($"{Session} Request timeout in {nameof(GetAccountRangeMessage)}");
        }
    }
}
