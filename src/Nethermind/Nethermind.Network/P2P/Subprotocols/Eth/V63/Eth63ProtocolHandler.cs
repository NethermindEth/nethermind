﻿//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V63
{
    public class Eth63ProtocolHandler : Eth62ProtocolHandler
    {
        private readonly BlockingCollection<Request<GetNodeDataMessage, byte[][]>> _nodeDataRequests
            = new BlockingCollection<Request<GetNodeDataMessage, byte[][]>>();

        private readonly BlockingCollection<Request<GetReceiptsMessage, TxReceipt[][]>> _receiptsRequests
            = new BlockingCollection<Request<GetReceiptsMessage, TxReceipt[][]>>();

        public Eth63ProtocolHandler(ISession session,
            IMessageSerializationService serializer,
            INodeStatsManager nodeStatsManager,
            ISyncServer syncServer,
            ITxPool txPool,
            ILogManager logManager) : base(session, serializer, nodeStatsManager, syncServer, txPool, logManager)
        {
        }

        public override byte ProtocolVersion => 63;

        public override int MessageIdSpaceSize => 17; // magic number here following Go

        public override void HandleMessage(ZeroPacket message)
        {
            base.HandleMessage(message);
            int size = message.Content.ReadableBytes;

            switch (message.PacketType)
            {
                case Eth63MessageCode.GetReceipts:
                    if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(Session.Node.Host, Name, nameof(GetReceiptsMessage));
                    Handle(Deserialize<GetReceiptsMessage>(message.Content));
                    break;
                case Eth63MessageCode.Receipts:
                    if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(Session.Node.Host, Name, nameof(ReceiptsMessage));
                    Handle(Deserialize<ReceiptsMessage>(message.Content), size);
                    break;
                case Eth63MessageCode.GetNodeData:
                    if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(Session.Node.Host, Name, nameof(GetNodeDataMessage));
                    Handle(Deserialize<GetNodeDataMessage>(message.Content));
                    break;
                case Eth63MessageCode.NodeData:
                    if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(Session.Node.Host, Name, nameof(NodeDataMessage));
                    Handle(Deserialize<NodeDataMessage>(message.Content), size);
                    break;
            }
        }

        public override string Name => "eth63";

        private void Handle(GetReceiptsMessage msg)
        {
            Metrics.Eth63GetReceiptsReceived++;
            if (msg.Hashes.Count > 512)
            {
                throw new EthSyncException("Incoming receipts request for more than 512 blocks");
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            TxReceipt[][] txReceipts = SyncServer.GetReceipts(msg.Hashes);
            Send(new ReceiptsMessage(txReceipts));
            stopwatch.Stop();
            if (Logger.IsTrace) Logger.Trace($"OUT {Counter:D5} Receipts to {Node:c} in {stopwatch.Elapsed.TotalMilliseconds}ms");
        }

        private void Handle(ReceiptsMessage msg, long size)
        {
            Metrics.Eth63ReceiptsReceived++;
            Request<GetReceiptsMessage, TxReceipt[][]> request = _receiptsRequests.Take();
            request.ResponseSize = size;
            request.CompletionSource.SetResult(msg.TxReceipts);
        }

        private void Handle(GetNodeDataMessage msg)
        {
            Metrics.Eth63GetNodeDataReceived++;
            if (msg.Hashes.Count > 4096)
            {
                throw new EthSyncException("Incoming node data request for more than 4096 nodes");
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            byte[][] nodeData = SyncServer.GetNodeData(msg.Hashes);
            Send(new NodeDataMessage(nodeData));
            stopwatch.Stop();
            if (Logger.IsTrace) Logger.Trace($"OUT {Counter:D5} NodeData to {Node:c} in {stopwatch.Elapsed.TotalMilliseconds}ms");
        }

        private void Handle(NodeDataMessage msg, int size)
        {
            Metrics.Eth63NodeDataReceived++;
            Request<GetNodeDataMessage, byte[][]> request = _nodeDataRequests.Take();
            request.ResponseSize = size;
            request.CompletionSource.SetResult(msg.Data);
        }

        public override async Task<byte[][]> GetNodeData(IList<Keccak> keys, CancellationToken token)
        {
            if (keys.Count == 0)
            {
                return Array.Empty<byte[]>();
            }

            GetNodeDataMessage msg = new GetNodeDataMessage(keys);
            byte[][] receipts = await SendRequest(msg, token);
            return receipts;
        }

        public override async Task<TxReceipt[][]> GetReceipts(IList<Keccak> blockHashes, CancellationToken token)
        {
            if (blockHashes.Count == 0)
            {
                return Array.Empty<TxReceipt[]>();
            }

            GetReceiptsMessage msg = new GetReceiptsMessage(blockHashes);
            TxReceipt[][] txReceipts = await SendRequest(msg, token);
            return txReceipts;
        }

        [Todo(Improve.Refactor, "Generic approach to requests")]
        private async Task<byte[][]> SendRequest(GetNodeDataMessage message, CancellationToken token)
        {
            if (Logger.IsTrace)
            {
                Logger.Trace("Sending node fata request:");
                Logger.Trace($"Keys count: {message.Hashes.Count}");
            }

            Request<GetNodeDataMessage, byte[][]> request = new Request<GetNodeDataMessage, byte[][]>(message);
            request.StartMeasuringTime();
            _nodeDataRequests.Add(request, token);

            Send(request.Message);
            Task<byte[][]> task = request.CompletionSource.Task;

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
                long elapsed = request.FinishMeasuringTime();
                long bytesPerMillisecond = (long) ((decimal) request.ResponseSize / Math.Max(1, elapsed));
                if (Logger.IsTrace) Logger.Trace($"{this} speed is {request.ResponseSize}/{elapsed} = {bytesPerMillisecond}");
                StatsManager.ReportTransferSpeedEvent(Session.Node, TransferSpeedType.NodeData, bytesPerMillisecond);

                return task.Result;
            }

            StatsManager.ReportTransferSpeedEvent(Session.Node, TransferSpeedType.NodeData, 0L);
            throw new TimeoutException($"{Session} Request timeout in {nameof(GetNodeDataMessage)}");
        }

        [Todo(Improve.Refactor, "Generic approach to requests")]
        private async Task<TxReceipt[][]> SendRequest(GetReceiptsMessage message, CancellationToken token)
        {
            if (Logger.IsTrace)
            {
                Logger.Trace("Sending node fata request:");
                Logger.Trace($"Hashes count: {message.Hashes.Count}");
            }

            Request<GetReceiptsMessage, TxReceipt[][]> request = new Request<GetReceiptsMessage, TxReceipt[][]>(message);
            request.StartMeasuringTime();
            _receiptsRequests.Add(request, token);

            Send(request.Message);

            Task<TxReceipt[][]> task = request.CompletionSource.Task;
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
                long elapsed = request.FinishMeasuringTime();
                long bytesPerMillisecond = (long) ((decimal) request.ResponseSize / Math.Max(1, elapsed));
                if (Logger.IsTrace) Logger.Trace($"{this} speed is {request.ResponseSize}/{elapsed} = {bytesPerMillisecond}");
                StatsManager.ReportTransferSpeedEvent(Session.Node, TransferSpeedType.Receipts, bytesPerMillisecond);

                return task.Result;
            }

            StatsManager.ReportTransferSpeedEvent(Session.Node, TransferSpeedType.Receipts,0L);

            throw new TimeoutException($"{Session} Request timeout in {nameof(GetReceiptsMessage)}");
        }
    }
}