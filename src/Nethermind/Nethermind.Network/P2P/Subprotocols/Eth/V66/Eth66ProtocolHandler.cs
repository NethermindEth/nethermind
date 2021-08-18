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
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Eth.V65;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66
{
    /// <summary>
    /// https://github.com/ethereum/EIPs/blob/master/EIPS/eip-2481.md
    /// </summary>
    public class Eth66ProtocolHandler : Eth65ProtocolHandler
    {
        protected readonly MessageQueue<GetBlockHeadersMessage, BlockHeader[]> _headersRequests66;
        protected readonly MessageQueue<GetBlockBodiesMessage, BlockBody[]> _bodiesRequests66;
        public Eth66ProtocolHandler(ISession session,
            IMessageSerializationService serializer,
            INodeStatsManager nodeStatsManager,
            ISyncServer syncServer,
            ITxPool txPool,
            IPooledTxsRequestor pooledTxsRequestor,
            ISpecProvider specProvider,
            ILogManager logManager)
            : base(session, serializer, nodeStatsManager, syncServer, txPool, pooledTxsRequestor, specProvider, logManager)
        {
            _headersRequests66 = new MessageQueue<GetBlockHeadersMessage, BlockHeader[]>(Send);
            _bodiesRequests66 = new MessageQueue<GetBlockBodiesMessage, BlockBody[]>(Send);
        }
        
        public override string Name => "eth66";

        public override byte ProtocolVersion => 66;
        
        public override void HandleMessage(ZeroPacket message)
        {
            int size = message.Content.ReadableBytes;

            switch (message.PacketType)
            {
                case Eth66MessageCode.GetBlockHeaders:
                    GetBlockHeadersMessage getBlockHeadersMessage
                        = Deserialize<GetBlockHeadersMessage>(message.Content);
                    ReportIn(getBlockHeadersMessage);
                    Handle(getBlockHeadersMessage);
                    break;
                case Eth66MessageCode.BlockHeaders:
                    BlockHeadersMessage headersMsg = Deserialize<BlockHeadersMessage>(message.Content);
                    ReportIn(headersMsg);
                    Handle(headersMsg.EthMessage, size);
                    break;
                case Eth66MessageCode.GetBlockBodies:
                    GetBlockBodiesMessage getBodiesMsg = Deserialize<GetBlockBodiesMessage>(message.Content);
                    ReportIn(getBodiesMsg);
                    Handle(getBodiesMsg);
                    break;
                case Eth66MessageCode.BlockBodies:
                    BlockBodiesMessage bodiesMsg = Deserialize<BlockBodiesMessage>(message.Content);
                    ReportIn(bodiesMsg);
                    HandleBodies(bodiesMsg.EthMessage, size);
                    break;
                case Eth66MessageCode.GetPooledTransactions:
                    GetPooledTransactionsMessage getPooledTxMsg
                        = Deserialize<GetPooledTransactionsMessage>(message.Content);
                    ReportIn(getPooledTxMsg);
                    Handle(getPooledTxMsg);
                    break;
                case Eth66MessageCode.PooledTransactions:
                    PooledTransactionsMessage pooledTxMsg
                        = Deserialize<PooledTransactionsMessage>(message.Content);
                    Metrics.Eth65PooledTransactionsReceived++;
                    ReportIn(pooledTxMsg);
                    Handle(pooledTxMsg.EthMessage);
                    break;
                case Eth66MessageCode.GetReceipts:
                    GetReceiptsMessage getReceiptsMessage = Deserialize<GetReceiptsMessage>(message.Content);
                    ReportIn(getReceiptsMessage);
                    Handle(getReceiptsMessage);
                    break;
                case Eth66MessageCode.Receipts:
                    ReceiptsMessage receiptsMessage = Deserialize<ReceiptsMessage>(message.Content);
                    ReportIn(receiptsMessage);
                    Handle(receiptsMessage.EthMessage, size);
                    break;
                case Eth66MessageCode.GetNodeData:
                    GetNodeDataMessage getNodeDataMessage = Deserialize<GetNodeDataMessage>(message.Content);
                    ReportIn(getNodeDataMessage);
                    Handle(getNodeDataMessage);
                    break;
                case Eth66MessageCode.NodeData:
                    NodeDataMessage nodeDataMessage = Deserialize<NodeDataMessage>(message.Content);
                    ReportIn(nodeDataMessage);
                    Handle(nodeDataMessage.EthMessage, size);
                    break;
                default:
                    base.HandleMessage(message);
                    break;
            }
        }

        private void Handle(GetBlockHeadersMessage getBlockHeaders)
        {
            Eth.V62.BlockHeadersMessage ethBlockHeadersMessage =
                FulfillBlockHeadersRequest(getBlockHeaders.EthMessage);
            Send(new BlockHeadersMessage(getBlockHeaders.RequestId, ethBlockHeadersMessage));
        }

        private void Handle(GetBlockBodiesMessage getBlockBodies)
        {
            Eth.V62.BlockBodiesMessage ethBlockBodiesMessage =
                FulfillBlockBodiesRequest(getBlockBodies.EthMessage);
            Send(new BlockBodiesMessage(getBlockBodies.RequestId, ethBlockBodiesMessage));
        }

        private void Handle(GetPooledTransactionsMessage getPooledTransactions)
        {
            Eth.V65.PooledTransactionsMessage pooledTransactionsMessage =
                FulfillPooledTransactionsRequest(getPooledTransactions.EthMessage);
            Send(new PooledTransactionsMessage(getPooledTransactions.RequestId, pooledTransactionsMessage));
        }

        private void Handle(GetReceiptsMessage getReceiptsMessage)
        {
            Eth.V63.ReceiptsMessage receiptsMessage =
                FulfillReceiptsRequest(getReceiptsMessage.EthMessage);
            Send(new ReceiptsMessage(getReceiptsMessage.RequestId, receiptsMessage));
        }

        private void Handle(GetNodeDataMessage getNodeDataMessage)
        {
            Eth.V63.NodeDataMessage nodeDataMessage =
                FulfillNodeDataRequest(getNodeDataMessage.EthMessage);
            Send(new NodeDataMessage(getNodeDataMessage.RequestId, nodeDataMessage));
        }

        protected override async Task<BlockHeader[]> SendRequest(Eth.V62.GetBlockHeadersMessage message, CancellationToken token)
        {
            if (Logger.IsTrace)
            {
                Logger.Trace($"Sending headers request to {Session.Node:c}:");
                Logger.Trace($"  Starting blockhash: {message.StartBlockHash}");
                Logger.Trace($"  Starting number: {message.StartBlockNumber}");
                Logger.Trace($"  Skip: {message.Skip}");
                Logger.Trace($"  Reverse: {message.Reverse}");
                Logger.Trace($"  Max headers: {message.MaxHeaders}");
            }
        
            GetBlockHeadersMessage msg66 = new() {EthMessage = message};
            Request<GetBlockHeadersMessage, BlockHeader[]> request = new(msg66);
            _headersRequests66.Send(request);
        
            Task<BlockHeader[]> task = request.CompletionSource.Task;
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
                long elapsed = request.FinishMeasuringTime();
                long bytesPerMillisecond = (long) ((decimal) request.ResponseSize / Math.Max(1, elapsed));
                if (Logger.IsTrace) Logger.Trace($"{this} speed is {request.ResponseSize}/{elapsed} = {bytesPerMillisecond}");
        
                StatsManager.ReportTransferSpeedEvent(Session.Node, TransferSpeedType.Headers, bytesPerMillisecond);
                return task.Result;
            }
        
            StatsManager.ReportTransferSpeedEvent(Session.Node, TransferSpeedType.Headers, 0);
            throw new TimeoutException($"{Session} Request timeout in {nameof(GetBlockHeadersMessage)} with {message.MaxHeaders} max headers");
        }
        
        protected override async Task<BlockBody[]> SendRequest(Eth.V62.GetBlockBodiesMessage message, CancellationToken token)
        {
            if (Logger.IsTrace)
            {
                Logger.Trace("Sending bodies request:");
                Logger.Trace($"Blockhashes count: {message.BlockHashes.Count}");
            }
        
            GetBlockBodiesMessage msg66 = new() {EthMessage = message};
            Request<GetBlockBodiesMessage, BlockBody[]> request = new(msg66);
            _bodiesRequests66.Send(request);
        
            // Logger.Warn($"Sending bodies request of length {request.Message.BlockHashes.Count} to {this}");
        
            Task<BlockBody[]> task = request.CompletionSource.Task;
            using CancellationTokenSource delayCancellation = new();
            using CancellationTokenSource compositeCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, delayCancellation.Token);
            Task firstTask = await Task.WhenAny(task, Task.Delay(Timeouts.Eth, compositeCancellation.Token));
            if (firstTask.IsCanceled)
            {
                // Logger.Warn($"Bodies request of length {request.Message.BlockHashes.Count} expired with {this}");
                token.ThrowIfCancellationRequested();
            }
        
            if (firstTask == task)
            {
                // Logger.Warn($"Bodies request of length {request.Message.BlockHashes.Count} received with size {request.ResponseSize} from {this}");
                delayCancellation.Cancel();
                long elapsed = request.FinishMeasuringTime();
                long bytesPerMillisecond = (long) ((decimal) request.ResponseSize / Math.Max(1, elapsed));
                if (Logger.IsTrace) Logger.Trace($"{this} speed is {request.ResponseSize}/{elapsed} = {bytesPerMillisecond}");
                StatsManager.ReportTransferSpeedEvent(Session.Node, TransferSpeedType.Bodies, bytesPerMillisecond);
        
                return task.Result;
            }
        
            StatsManager.ReportTransferSpeedEvent(Session.Node, TransferSpeedType.Bodies, 0L);
            throw new TimeoutException($"{Session} Request timeout in {nameof(GetBlockBodiesMessage)} with {message.BlockHashes.Count} block hashes");
        }
        
        protected override void Handle(Eth.V62.BlockHeadersMessage message, long size)
        {
            _headersRequests66.Handle(message.BlockHeaders, size);
        }

        protected override void HandleBodies(Eth.V62.BlockBodiesMessage blockBodiesMessage, long size)
        {
            _bodiesRequests66.Handle(blockBodiesMessage.Bodies, size);
        }
    }
}
