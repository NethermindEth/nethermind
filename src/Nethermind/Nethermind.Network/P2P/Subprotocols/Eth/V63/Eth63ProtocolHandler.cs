// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V63
{
    public class Eth63ProtocolHandler : Eth62ProtocolHandler
    {
        private readonly MessageQueue<GetNodeDataMessage, byte[][]> _nodeDataRequests;

        private readonly MessageQueue<GetReceiptsMessage, TxReceipt[][]> _receiptsRequests;

        private AdaptiveRequestSizer _receiptsRequestSizer;

        public Eth63ProtocolHandler(ISession session,
            IMessageSerializationService serializer,
            INodeStatsManager nodeStatsManager,
            ISyncServer syncServer,
            ITxPool txPool,
            IGossipPolicy gossipPolicy,
            ILogManager logManager,
            ITxGossipPolicy? transactionsGossipPolicy = null)
            : base(session, serializer, nodeStatsManager, syncServer, txPool, gossipPolicy, logManager, transactionsGossipPolicy)
        {
            _nodeDataRequests = new MessageQueue<GetNodeDataMessage, byte[][]>(Send);
            _receiptsRequests = new MessageQueue<GetReceiptsMessage, TxReceipt[][]>(Send);

            _receiptsRequestSizer = new AdaptiveRequestSizer(
                1,
                128);
        }

        public override byte ProtocolVersion => EthVersions.Eth63;

        public override int MessageIdSpaceSize => 17; // magic number here following Go

        public override void HandleMessage(ZeroPacket message)
        {
            base.HandleMessage(message);
            int size = message.Content.ReadableBytes;

            switch (message.PacketType)
            {
                case Eth63MessageCode.GetReceipts:
                    GetReceiptsMessage getReceiptsMessage = Deserialize<GetReceiptsMessage>(message.Content);
                    ReportIn(getReceiptsMessage, size);
                    Handle(getReceiptsMessage);
                    break;
                case Eth63MessageCode.Receipts:
                    ReceiptsMessage receiptsMessage = Deserialize<ReceiptsMessage>(message.Content);
                    ReportIn(receiptsMessage, size);
                    Handle(receiptsMessage, size);
                    break;
                case Eth63MessageCode.GetNodeData:
                    GetNodeDataMessage getNodeDataMessage = Deserialize<GetNodeDataMessage>(message.Content);
                    ReportIn(getNodeDataMessage, size);
                    Handle(getNodeDataMessage);
                    break;
                case Eth63MessageCode.NodeData:
                    NodeDataMessage nodeDataMessage = Deserialize<NodeDataMessage>(message.Content);
                    ReportIn(nodeDataMessage, size);
                    Handle(nodeDataMessage, size);
                    break;
            }
        }

        public override string Name => "eth63";

        protected virtual void Handle(ReceiptsMessage msg, long size)
        {
            Metrics.Eth63ReceiptsReceived++;
            _receiptsRequests.Handle(msg.TxReceipts, size);
        }

        private void Handle(GetNodeDataMessage msg)
        {
            Metrics.Eth63GetNodeDataReceived++;

            Stopwatch stopwatch = Stopwatch.StartNew();
            Send(FulfillNodeDataRequest(msg));
            stopwatch.Stop();
            if (Logger.IsTrace)
                Logger.Trace($"OUT {Counter:D5} NodeData to {Node:c} in {stopwatch.Elapsed.TotalMilliseconds}ms");
        }

        protected NodeDataMessage FulfillNodeDataRequest(GetNodeDataMessage msg)
        {
            if (msg.Hashes.Count > 4096)
            {
                throw new EthSyncException("Incoming node data request for more than 4096 nodes");
            }

            byte[][] nodeData = SyncServer.GetNodeData(msg.Hashes);

            return new NodeDataMessage(nodeData);
        }

        protected virtual void Handle(NodeDataMessage msg, int size)
        {
            Metrics.Eth63NodeDataReceived++;
            _nodeDataRequests.Handle(msg.Data, size);
        }

        public override async Task<byte[][]> GetNodeData(IReadOnlyList<Keccak> keys, CancellationToken token)
        {
            if (keys.Count == 0)
            {
                return Array.Empty<byte[]>();
            }

            GetNodeDataMessage msg = new(keys);

            // if node data is a disposable pooled array wrapper here then we could save around 1.6% allocations
            // on a sample 3M blocks Goerli fast sync
            byte[][] nodeData = await SendRequest(msg, token);
            return nodeData;
        }
        public override async Task<TxReceipt[][]> GetReceipts(IReadOnlyList<Keccak> blockHashes, CancellationToken token)
        {
            if (blockHashes.Count == 0)
            {
                return Array.Empty<TxReceipt[]>();
            }

            TxReceipt[][] txReceipts = await _receiptsRequestSizer.Run(async (requestSize) =>
            {
                GetReceiptsMessage msg = new(blockHashes.Take(requestSize).ToArray());

                Stopwatch sw = new Stopwatch();
                TxReceipt[][] response = await SendRequest(msg, token);
                TimeSpan duration = sw.Elapsed;

                if (duration > TimeSpan.FromMilliseconds(3000))
                {
                    return (response, AdaptiveRequestSizer.Direction.Decrease);
                }

                int txCount = 0;
                if (response != null)
                {
                    foreach (TxReceipt[]? receipts in response)
                    {
                        txCount += receipts?.Length ?? 0;
                    }
                }

                if (txCount > 20000)
                {
                    return (response, AdaptiveRequestSizer.Direction.Decrease);
                }

                if (blockHashes.Count > requestSize && duration < TimeSpan.FromMilliseconds(2000) && txCount < 1000)
                {
                    return (response, AdaptiveRequestSizer.Direction.Increase);
                }

                return (response, AdaptiveRequestSizer.Direction.Stay);
            });

            return txReceipts;
        }

        protected virtual async Task<byte[][]> SendRequest(GetNodeDataMessage message, CancellationToken token)
        {
            if (Logger.IsTrace)
            {
                Logger.Trace("Sending node fata request:");
                Logger.Trace($"Keys count: {message.Hashes.Count}");
            }

            return await SendRequestGeneric(
                _nodeDataRequests,
                message,
                TransferSpeedType.NodeData,
                static (_) => $"{nameof(GetNodeDataMessage)}",
                token);
        }

        protected virtual async Task<TxReceipt[][]> SendRequest(GetReceiptsMessage message, CancellationToken token)
        {
            if (Logger.IsTrace)
            {
                Logger.Trace("Sending node fata request:");
                Logger.Trace($"Hashes count: {message.Hashes.Count}");
            }

            return await SendRequestGeneric(
                _receiptsRequests,
                message,
                TransferSpeedType.Receipts,
                static (_) => $"{nameof(GetReceiptsMessage)}",
                token);
        }
    }
}
