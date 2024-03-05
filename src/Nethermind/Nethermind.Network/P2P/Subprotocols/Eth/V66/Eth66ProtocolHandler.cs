// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V65;
using Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using GetPooledTransactionsMessage = Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.GetPooledTransactionsMessage;
using PooledTransactionsMessage = Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.PooledTransactionsMessage;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66
{
    /// <summary>
    /// https://github.com/ethereum/EIPs/blob/master/EIPS/eip-2481.md
    /// </summary>
    public class Eth66ProtocolHandler : Eth65ProtocolHandler
    {
        private readonly MessageDictionary<GetBlockHeadersMessage, IOwnedReadOnlyList<BlockHeader>> _headersRequests66;
        private readonly MessageDictionary<GetBlockBodiesMessage, (OwnedBlockBodies, long)> _bodiesRequests66;
        private readonly MessageDictionary<GetNodeDataMessage, IOwnedReadOnlyList<byte[]>> _nodeDataRequests66;
        private readonly MessageDictionary<GetReceiptsMessage, (IOwnedReadOnlyList<TxReceipt[]>, long)> _receiptsRequests66;
        private readonly IPooledTxsRequestor _pooledTxsRequestor;
        private readonly Action<GetPooledTransactionsMessage> _sendAction;

        public Eth66ProtocolHandler(ISession session,
            IMessageSerializationService serializer,
            INodeStatsManager nodeStatsManager,
            ISyncServer syncServer,
            IBackgroundTaskScheduler backgroundTaskScheduler,
            ITxPool txPool,
            IPooledTxsRequestor pooledTxsRequestor,
            IGossipPolicy gossipPolicy,
            ForkInfo forkInfo,
            ILogManager logManager,
            ITxGossipPolicy? transactionsGossipPolicy = null)
            : base(session, serializer, nodeStatsManager, syncServer, backgroundTaskScheduler, txPool, pooledTxsRequestor, gossipPolicy, forkInfo, logManager, transactionsGossipPolicy)
        {
            _headersRequests66 = new MessageDictionary<GetBlockHeadersMessage, IOwnedReadOnlyList<BlockHeader>>(Send);
            _bodiesRequests66 = new MessageDictionary<GetBlockBodiesMessage, (OwnedBlockBodies, long)>(Send);
            _nodeDataRequests66 = new MessageDictionary<GetNodeDataMessage, IOwnedReadOnlyList<byte[]>>(Send);
            _receiptsRequests66 = new MessageDictionary<GetReceiptsMessage, (IOwnedReadOnlyList<TxReceipt[]>, long)>(Send);
            _pooledTxsRequestor = pooledTxsRequestor;
            // Capture Action once rather than per call
            _sendAction = Send;
        }

        public override string Name => "eth66";

        public override byte ProtocolVersion => EthVersions.Eth66;

        public override void HandleMessage(ZeroPacket message)
        {
            int size = message.Content.ReadableBytes;

            switch (message.PacketType)
            {
                case Eth66MessageCode.GetBlockHeaders:
                    GetBlockHeadersMessage getBlockHeadersMessage = Deserialize<GetBlockHeadersMessage>(message.Content);
                    Metrics.Eth66GetBlockHeadersReceived++;
                    ReportIn(getBlockHeadersMessage, size);
                    BackgroundTaskScheduler.ScheduleSyncServe(getBlockHeadersMessage, Handle);
                    break;
                case Eth66MessageCode.BlockHeaders:
                    BlockHeadersMessage headersMsg = Deserialize<BlockHeadersMessage>(message.Content);
                    Metrics.Eth66BlockHeadersReceived++;
                    ReportIn(headersMsg, size);
                    Handle(headersMsg, size);
                    break;
                case Eth66MessageCode.GetBlockBodies:
                    GetBlockBodiesMessage getBodiesMsg = Deserialize<GetBlockBodiesMessage>(message.Content);
                    Metrics.Eth66GetBlockBodiesReceived++;
                    ReportIn(getBodiesMsg, size);
                    BackgroundTaskScheduler.ScheduleSyncServe(getBodiesMsg, Handle);
                    break;
                case Eth66MessageCode.BlockBodies:
                    BlockBodiesMessage bodiesMsg = Deserialize<BlockBodiesMessage>(message.Content);
                    Metrics.Eth66BlockBodiesReceived++;
                    ReportIn(bodiesMsg, size);
                    HandleBodies(bodiesMsg, size);
                    break;
                case Eth66MessageCode.GetPooledTransactions:
                    GetPooledTransactionsMessage getPooledTxMsg
                        = Deserialize<GetPooledTransactionsMessage>(message.Content);
                    Metrics.Eth66GetPooledTransactionsReceived++;
                    ReportIn(getPooledTxMsg, size);
                    BackgroundTaskScheduler.ScheduleSyncServe(getPooledTxMsg, Handle);
                    break;
                case Eth66MessageCode.PooledTransactions:
                    if (CanReceiveTransactions)
                    {
                        PooledTransactionsMessage pooledTxMsg
                            = Deserialize<PooledTransactionsMessage>(message.Content);
                        Metrics.Eth66PooledTransactionsReceived++;
                        ReportIn(pooledTxMsg, size);
                        Handle(pooledTxMsg.EthMessage);
                    }
                    else
                    {
                        const string ignored = $"{nameof(PooledTransactionsMessage)} ignored, syncing";
                        ReportIn(ignored, size);
                    }

                    break;
                case Eth66MessageCode.GetReceipts:
                    GetReceiptsMessage getReceiptsMessage = Deserialize<GetReceiptsMessage>(message.Content);
                    Metrics.Eth66GetReceiptsReceived++;
                    ReportIn(getReceiptsMessage, size);
                    BackgroundTaskScheduler.ScheduleSyncServe(getReceiptsMessage, Handle);
                    break;
                case Eth66MessageCode.Receipts:
                    ReceiptsMessage receiptsMessage = Deserialize<ReceiptsMessage>(message.Content);
                    Metrics.Eth66ReceiptsReceived++;
                    ReportIn(receiptsMessage, size);
                    Handle(receiptsMessage, size);
                    break;
                case Eth66MessageCode.GetNodeData:
                    GetNodeDataMessage getNodeDataMessage = Deserialize<GetNodeDataMessage>(message.Content);
                    Metrics.Eth66GetNodeDataReceived++;
                    ReportIn(getNodeDataMessage, size);
                    BackgroundTaskScheduler.ScheduleSyncServe(getNodeDataMessage, Handle);
                    break;
                case Eth66MessageCode.NodeData:
                    NodeDataMessage nodeDataMessage = Deserialize<NodeDataMessage>(message.Content);
                    Metrics.Eth66NodeDataReceived++;
                    ReportIn(nodeDataMessage, size);
                    Handle(nodeDataMessage, size);
                    break;
                default:
                    base.HandleMessage(message);
                    break;
            }
        }

        private async Task<BlockHeadersMessage> Handle(GetBlockHeadersMessage getBlockHeaders, CancellationToken cancellationToken)
        {
            using var message = getBlockHeaders;
            V62.Messages.BlockHeadersMessage ethBlockHeadersMessage = await FulfillBlockHeadersRequest(message.EthMessage, cancellationToken);
            return new BlockHeadersMessage(message.RequestId, ethBlockHeadersMessage);
        }

        private async Task<BlockBodiesMessage> Handle(GetBlockBodiesMessage getBlockBodies, CancellationToken cancellationToken)
        {
            using var message = getBlockBodies;
            V62.Messages.BlockBodiesMessage ethBlockBodiesMessage = await FulfillBlockBodiesRequest(message.EthMessage, cancellationToken);
            return new BlockBodiesMessage(message.RequestId, ethBlockBodiesMessage);
        }

        private async Task<PooledTransactionsMessage> Handle(GetPooledTransactionsMessage getPooledTransactions, CancellationToken cancellationToken)
        {
            using var message = getPooledTransactions;
            return new PooledTransactionsMessage(message.RequestId,
                await FulfillPooledTransactionsRequest(message.EthMessage, cancellationToken));
        }

        private async Task<ReceiptsMessage> Handle(GetReceiptsMessage getReceiptsMessage, CancellationToken cancellationToken)
        {
            using var message = getReceiptsMessage;
            V63.Messages.ReceiptsMessage receiptsMessage = await FulfillReceiptsRequest(message.EthMessage, cancellationToken);
            return new ReceiptsMessage(message.RequestId, receiptsMessage);
        }

        private async Task<NodeDataMessage> Handle(GetNodeDataMessage getNodeDataMessage, CancellationToken cancellationToken)
        {
            using var message = getNodeDataMessage;
            V63.Messages.NodeDataMessage nodeDataMessage = await FulfillNodeDataRequest(message.EthMessage, cancellationToken);
            return new NodeDataMessage(message.RequestId, nodeDataMessage);
        }

        private void Handle(BlockHeadersMessage message, long size)
        {
            _headersRequests66.Handle(message.RequestId, message.EthMessage.BlockHeaders, size);
        }

        private void HandleBodies(BlockBodiesMessage blockBodiesMessage, long size)
        {
            _bodiesRequests66.Handle(blockBodiesMessage.RequestId, (blockBodiesMessage.EthMessage.Bodies, size), size);
        }

        private void Handle(NodeDataMessage msg, int size)
        {
            _nodeDataRequests66.Handle(msg.RequestId, msg.EthMessage.Data, size);
        }

        private void Handle(ReceiptsMessage msg, long size)
        {
            _receiptsRequests66.Handle(msg.RequestId, (msg.EthMessage.TxReceipts, size), size);
        }

        protected override void Handle(NewPooledTransactionHashesMessage msg)
        {
            using var message = msg;
            bool isTrace = Logger.IsTrace;
            Stopwatch? stopwatch = isTrace ? Stopwatch.StartNew() : null;

            TxPool.Metrics.PendingTransactionsHashesReceived += message.Hashes.Count;
            _pooledTxsRequestor.RequestTransactionsEth66(_sendAction, message.Hashes);

            stopwatch?.Stop();
            if (isTrace)
                Logger.Trace($"OUT {Counter:D5} {nameof(NewPooledTransactionHashesMessage)} to {Node:c} " +
                             $"in {stopwatch.Elapsed.TotalMilliseconds}ms");
        }

        protected override async Task<IOwnedReadOnlyList<BlockHeader>> SendRequest(V62.Messages.GetBlockHeadersMessage message, CancellationToken token)
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

            using GetBlockHeadersMessage msg66 = new();
            msg66.EthMessage = message;

            return await SendRequestGenericEth66(
                _headersRequests66,
                msg66,
                TransferSpeedType.Headers,
                static (message) => $"{nameof(GetBlockHeadersMessage)} with {message.EthMessage.MaxHeaders} max headers",
                token);
        }

        protected override async Task<(OwnedBlockBodies, long)> SendRequest(V62.Messages.GetBlockBodiesMessage message, CancellationToken token)
        {
            if (Logger.IsTrace)
            {
                Logger.Trace("Sending bodies request:");
                Logger.Trace($"Blockhashes count: {message.BlockHashes.Count}");
            }

            using GetBlockBodiesMessage msg66 = new();
            msg66.EthMessage = message;
            return await SendRequestGenericEth66(
                _bodiesRequests66,
                msg66,
                TransferSpeedType.Bodies,
                static (message) => $"{nameof(GetBlockBodiesMessage)} with {message.EthMessage.BlockHashes.Count} block hashes",
                token);
        }

        protected override async Task<IOwnedReadOnlyList<byte[]>> SendRequest(V63.Messages.GetNodeDataMessage message, CancellationToken token)
        {
            if (Logger.IsTrace)
            {
                Logger.Trace("Sending node fata request:");
                Logger.Trace($"Keys count: {message.Hashes.Count}");
            }

            using GetNodeDataMessage msg66 = new();
            msg66.EthMessage = message;
            return await SendRequestGenericEth66(
                _nodeDataRequests66,
                msg66,
                TransferSpeedType.NodeData,
                static (_) => $"{nameof(GetNodeDataMessage)}",
                token);
        }

        protected override async Task<(IOwnedReadOnlyList<TxReceipt[]>, long)> SendRequest(V63.Messages.GetReceiptsMessage message, CancellationToken token)
        {
            if (Logger.IsTrace)
            {
                Logger.Trace("Sending node fata request:");
                Logger.Trace($"Hashes count: {message.Hashes.Count}");
            }

            using GetReceiptsMessage msg66 = new();
            msg66.EthMessage = message;
            return await SendRequestGenericEth66(
                _receiptsRequests66,
                msg66,
                TransferSpeedType.Receipts,
                static (_) => $"{nameof(GetReceiptsMessage)}",
                token);
        }

        private async Task<TResponse> SendRequestGenericEth66<T66, TResponse>(
            MessageDictionary<T66, TResponse> messageQueue,
            T66 message,
            TransferSpeedType speedType,
            Func<T66, string> describeRequestFunc,
            CancellationToken token
        )
            where T66 : IEth66Message
        {
            Request<T66, TResponse> request = new(message);
            messageQueue.Send(request);

            return await HandleResponse(request, speedType, describeRequestFunc, token);
        }
    }
}
