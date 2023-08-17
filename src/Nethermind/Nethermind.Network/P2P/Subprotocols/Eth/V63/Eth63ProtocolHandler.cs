// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
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

        private readonly MessageQueue<GetReceiptsMessage, (TxReceipt[][], long)> _receiptsRequests;

        private readonly LatencyAndMessageSizeBasedRequestSizer _receiptsRequestSizer = new(
            minRequestLimit: 1,
            maxRequestLimit: 128,

            // In addition to the byte limit, we also try to keep the latency of the get receipts between these two
            // watermark. This reduce timeout rate, and subsequently disconnection rate.
            lowerLatencyWatermark: TimeSpan.FromMilliseconds(2000),
            upperLatencyWatermark: TimeSpan.FromMilliseconds(3000),

            // When the receipts message size exceed this, we try to reduce the maximum number of block for this peer.
            // This is for BeSU and Reth which does not seems to use the 2MB soft limit, causing them to send 20MB of bodies
            // or receipts. This is not great as large message size are harder for DotNetty to pool byte buffer, causing
            // higher memory usage. Reducing this even further does seems to help with memory, but may reduce throughput.
            maxResponseSize: 3_000_000,
            initialRequestSize: 8
        );

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
            _receiptsRequests = new MessageQueue<GetReceiptsMessage, (TxReceipt[][], long)>(Send);
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
            _receiptsRequests.Handle((msg.TxReceipts, size), size);
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

            TxReceipt[][] txReceipts = await _receiptsRequestSizer.Run(blockHashes, async clampedBlockHashes =>
                await SendRequest(new GetReceiptsMessage(clampedBlockHashes), token));

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

        protected virtual async Task<(TxReceipt[][], long)> SendRequest(GetReceiptsMessage message, CancellationToken token)
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
