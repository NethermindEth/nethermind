//  Copyright (c) 2018 Demerzel Solutions Limited
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
using Nethermind.Blockchain.TxPools;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;

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
            ILogManager logManager,
            ITxPool txPool) : base(session, serializer, nodeStatsManager, syncServer, logManager, txPool)
        {
        }

        public override bool IsFastSyncSupported => true;

        public override byte ProtocolVersion => 63;

        public override int MessageIdSpaceSize => 17; // magic number here following Go

        public override void HandleMessage(ZeroPacket message)
        {
            base.HandleMessage(message);

            int size = message.Content.ReadableBytes;
            switch (message.PacketType)
            {
                case Eth63MessageCode.GetReceipts:
                    NetworkDiagTracer.ReportIncomingMessage(Session.SessionId, Name, nameof(GetReceiptsMessage));
                    Interlocked.Increment(ref Counter);
                    if(Logger.IsTrace) Logger.Trace($"{Counter:D5} GetReceipts from {Node:c}");
                    Metrics.Eth63GetReceiptsReceived++;
                    Handle(Deserialize<GetReceiptsMessage>(message.Content));
                    break;
                case Eth63MessageCode.Receipts:
                    NetworkDiagTracer.ReportIncomingMessage(Session.SessionId, Name, nameof(ReceiptsMessage));
                    Interlocked.Increment(ref Counter);
                    if(Logger.IsTrace) Logger.Trace($"{Counter:D5} Receipts from {Node:c}");
                    Metrics.Eth63ReceiptsReceived++;
                    Handle(Deserialize<ReceiptsMessage>(message.Content), size);
                    break;
                case Eth63MessageCode.GetNodeData:
                    NetworkDiagTracer.ReportIncomingMessage(Session.SessionId, Name, nameof(GetNodeDataMessage));
                    Interlocked.Increment(ref Counter);
                    if(Logger.IsTrace) Logger.Trace($"{Counter:D5} GetNodeData from {Node:c}");
                    Metrics.Eth63GetNodeDataReceived++;
                    Handle(Deserialize<GetNodeDataMessage>(message.Content));
                    break;
                case Eth63MessageCode.NodeData:
                    NetworkDiagTracer.ReportIncomingMessage(Session.SessionId, Name, nameof(NodeDataMessage));
                    Interlocked.Increment(ref Counter);
                    if(Logger.IsTrace) Logger.Trace($"{Counter:D5} NodeData from {Node:c}");
                    Metrics.Eth63NodeDataReceived++;
                    Handle(Deserialize<NodeDataMessage>(message.Content), size);
                    break;
            }
        }

        public override string Name => "eth63";
        
        private void Handle(GetReceiptsMessage msg)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            TxReceipt[][] txReceipts = SyncServer.GetReceipts(msg.BlockHashes);
            Interlocked.Increment(ref Counter);
            Send(new ReceiptsMessage(txReceipts));
            stopwatch.Stop();
            if(Logger.IsTrace) Logger.Trace($"OUT {Counter:D5} Receipts to {Node:c} in {stopwatch.Elapsed.TotalMilliseconds}ms");
        }

        private void Handle(ReceiptsMessage msg, long size)
        {
            var request = _receiptsRequests.Take();
            if (IsRequestMatched(request, msg))
            {
                request.ResponseSize = size;
                request.CompletionSource.SetResult(msg.TxReceipts);
            }
        }

        private void Handle(GetNodeDataMessage msg)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            byte[][] nodeData = SyncServer.GetNodeData(msg.Keys);
            Interlocked.Increment(ref Counter);
            Send(new NodeDataMessage(nodeData));
            stopwatch.Stop();
            if(Logger.IsTrace) Logger.Trace($"OUT {Counter:D5} NodeData to {Node:c} in {stopwatch.Elapsed.TotalMilliseconds}ms");
        }

        private void Handle(NodeDataMessage msg, int size)
        {
            var request = _nodeDataRequests.Take();
            request.ResponseSize = size;
            if (IsRequestMatched(request, msg))
            {
                request.CompletionSource.SetResult(msg.Data);
            }
        }

        public override async Task<byte[][]> GetNodeData(IList<Keccak> keys, CancellationToken token)
        {
            if (keys.Count == 0)
            {
                return Array.Empty<byte[]>();
            }
            
            var msg = new GetNodeDataMessage(keys);
            byte[][] receipts = await SendRequest(msg, token);
            return receipts;
        }
        
        public override async Task<TxReceipt[][]> GetReceipts(IList<Keccak> blockHashes, CancellationToken token)
        {
            if (blockHashes.Count == 0)
            {
                return Array.Empty<TxReceipt[]>();
            }
            
            // Logger.Info($"Sending receipts request ({blockHashes.Count}) to {this}");
            
            var msg = new GetReceiptsMessage(blockHashes);
            TxReceipt[][] txReceipts = await SendRequest(msg, token);
            
            // Logger.Info($"Sent receipts request ({blockHashes.Count}) to {this} - received {txReceipts.Length}");
            return txReceipts;
        }

        [Todo(Improve.Refactor, "Generic approach to requests")]
        private async Task<byte[][]> SendRequest(GetNodeDataMessage message, CancellationToken token)
        {
            if (Logger.IsTrace)
            {
                Logger.Trace("Sending node fata request:");
                Logger.Trace($"Keys count: {message.Keys.Count}");
            }

            var request = new Request<GetNodeDataMessage, byte[][]>(message);
            request.StartMeasuringTime();
            _nodeDataRequests.Add(request, token);

            Send(request.Message);
            Task<byte[][]> task = request.CompletionSource.Task;
            
            using CancellationTokenSource delayCancellation = new CancellationTokenSource();
            using CancellationTokenSource compositeCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, delayCancellation.Token);
            var firstTask = await Task.WhenAny(task, Task.Delay(Timeouts.Eth, compositeCancellation.Token));
            if (firstTask.IsCanceled)
            {
                token.ThrowIfCancellationRequested();
            }

            if (firstTask == task)
            {
                delayCancellation.Cancel();
                long elapsed = request.FinishMeasuringTime();
                long bytesPerMillisecond = (long) ((decimal) request.ResponseSize / Math.Max(1, elapsed));
                if(Logger.IsTrace) Logger.Trace($"{this} speed is {request.ResponseSize}/{elapsed} = {bytesPerMillisecond}");
                StatsManager.ReportTransferSpeedEvent(Session.Node, bytesPerMillisecond);

                return task.Result;
            }
            
            StatsManager.ReportTransferSpeedEvent(Session.Node, 0L);
            throw new TimeoutException($"{Session} Request timeout in {nameof(GetNodeDataMessage)}");
        }
        
        [Todo(Improve.Refactor, "Generic approach to requests")]
        private async Task<TxReceipt[][]> SendRequest(GetReceiptsMessage message, CancellationToken token)
        {
            if (Logger.IsTrace)
            {
                Logger.Trace("Sending node fata request:");
                Logger.Trace($"Hashes count: {message.BlockHashes.Count}");
            }

            var request = new Request<GetReceiptsMessage, TxReceipt[][]>(message);
            request.StartMeasuringTime();
            _receiptsRequests.Add(request, token);

            Send(request.Message);

            Task<TxReceipt[][]> task = request.CompletionSource.Task;
            using CancellationTokenSource delayCancellation = new CancellationTokenSource();
            using CancellationTokenSource compositeCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, delayCancellation.Token);
            var firstTask = await Task.WhenAny(task, Task.Delay(Timeouts.Eth, compositeCancellation.Token));
            if (firstTask.IsCanceled)
            {
                token.ThrowIfCancellationRequested();
            }

            if (firstTask == task)
            {
                delayCancellation.Cancel();
                long elapsed = request.FinishMeasuringTime();
                long bytesPerMillisecond = (long) ((decimal) request.ResponseSize / Math.Max(1, elapsed));
                if(Logger.IsTrace) Logger.Trace($"{this} speed is {request.ResponseSize}/{elapsed} = {bytesPerMillisecond}");
                StatsManager.ReportTransferSpeedEvent(Session.Node, bytesPerMillisecond);

                return task.Result;
            }
            
            StatsManager.ReportTransferSpeedEvent(Session.Node, 0L);

            throw new TimeoutException($"{Session} Request timeout in {nameof(GetReceiptsMessage)}");
        }
        
        [Todo(Improve.MissingFunctionality, "Need to compare response")]
        private bool IsRequestMatched(
            Request<GetNodeDataMessage, byte[][]> request,
            NodeDataMessage response)
        {
            return response.PacketType == Eth63MessageCode.NodeData; // TODO: more detailed
        }

        [Todo(Improve.MissingFunctionality, "Need to compare response")]
        private bool IsRequestMatched(
            Request<GetReceiptsMessage, TxReceipt[][]> request,
            ReceiptsMessage response)
        {
            return response.PacketType == Eth63MessageCode.Receipts; // TODO: more detailed
        }
    }
}