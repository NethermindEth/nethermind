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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;
using Nethermind.Network.P2P.Subprotocols.Wit;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P
{
    public abstract class SyncPeerProtocolHandlerBase : ProtocolHandlerBase, ISyncPeer
    {
        public static readonly ulong SoftOutgoingMessageSizeLimit = (ulong) 2.MB();
        public Node Node => Session?.Node;
        public string ClientId => Node?.ClientId;
        public UInt256 TotalDifficulty { get; set; }
        public PublicKey Id => Node.Id;
        string ITxPoolPeer.Enode => Node?.ToString();

        public virtual bool IncludeInTxPool => true;
        protected ISyncServer SyncServer { get; }

        public long HeadNumber { get; set; }
        public Keccak HeadHash { get; set; }

        // this means that we know what the number, hash, and total diff of the head block is
        public bool IsInitialized { get; set; }

        public override string ToString() => $"[Peer|{Name}|{HeadNumber}|{ClientId}|{Node:s}]";

        protected Keccak _remoteHeadBlockHash;
        protected readonly ITimestamper _timestamper;

        protected readonly MessageQueue<GetBlockHeadersMessage, BlockHeader[]> _headersRequests;
        protected readonly MessageQueue<GetBlockBodiesMessage, BlockBody[]> _bodiesRequests;

        protected SyncPeerProtocolHandlerBase(ISession session,
            IMessageSerializationService serializer,
            INodeStatsManager statsManager,
            ISyncServer syncServer,
            ILogManager logManager) : base(session, statsManager, serializer, logManager)
        {
            SyncServer = syncServer ?? throw new ArgumentNullException(nameof(syncServer));
            _timestamper = Timestamper.Default;
            _headersRequests = new MessageQueue<GetBlockHeadersMessage, BlockHeader[]>(Send);
            _bodiesRequests = new MessageQueue<GetBlockBodiesMessage, BlockBody[]>(Send);
        }

        public void Disconnect(DisconnectReason reason, string details)
        {
            if (Logger.IsDebug) Logger.Debug($"Disconnecting {Node:c} because of the {details}");
            Session.InitiateDisconnect(reason, details);
        }

        async Task<BlockBody[]> ISyncPeer.GetBlockBodies(IList<Keccak> blockHashes, CancellationToken token)
        {
            if (blockHashes.Count == 0)
            {
                return Array.Empty<BlockBody>();
            }

            GetBlockBodiesMessage bodiesMsg = new(blockHashes);

            BlockBody[] blocks = await SendRequest(bodiesMsg, token);
            return blocks;
        }

        [Todo(Improve.Refactor, "Generic approach to requests")]
        private async Task<BlockBody[]> SendRequest(GetBlockBodiesMessage message, CancellationToken token)
        {
            if (Logger.IsTrace)
            {
                Logger.Trace("Sending bodies request:");
                Logger.Trace($"Blockhashes count: {message.BlockHashes.Count}");
            }

            Request<GetBlockBodiesMessage, BlockBody[]> request = new(message);
            _bodiesRequests.Send(request);

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


        async Task<BlockHeader[]> ISyncPeer.GetBlockHeaders(long number, int maxBlocks, int skip, CancellationToken token)
        {
            if (maxBlocks == 0)
            {
                return Array.Empty<BlockHeader>();
            }

            GetBlockHeadersMessage msg = new();
            msg.MaxHeaders = maxBlocks;
            msg.Reverse = 0;
            msg.Skip = skip;
            msg.StartBlockNumber = number;

            BlockHeader[] headers = await SendRequest(msg, token);
            return headers;
        }

        private async Task<BlockHeader[]> SendRequest(GetBlockHeadersMessage message, CancellationToken token)
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

            Request<GetBlockHeadersMessage, BlockHeader[]> request = new(message);
            _headersRequests.Send(request);

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

        async Task<BlockHeader> ISyncPeer.GetHeadBlockHeader(Keccak hash, CancellationToken token)
        {
            GetBlockHeadersMessage msg = new();
            msg.StartBlockHash = hash ?? _remoteHeadBlockHash;
            msg.MaxHeaders = 1;
            msg.Reverse = 0;
            msg.Skip = 0;

            BlockHeader[] headers = await SendRequest(msg, token);
            return headers.Length > 0 ? headers[0] : null;
        }

        public virtual Task<TxReceipt[][]> GetReceipts(IList<Keccak> blockHash, CancellationToken token)
        {
            throw new NotSupportedException("Fast sync not supported by eth62 protocol");
        }

        public virtual Task<byte[][]> GetNodeData(IList<Keccak> hashes, CancellationToken token)
        {
            throw new NotSupportedException("Fast sync not supported by eth62 protocol");
        }

        public abstract void NotifyOfNewBlock(Block block, SendBlockPriority priority);

        public virtual bool SendNewTransaction(Transaction transaction, bool isPriority)
        {
            if (transaction.Hash == null)
            {
                throw new InvalidOperationException("Trying to send a transaction with null hash");
            }

            TransactionsMessage msg = new(new[] {transaction});
            Send(msg);
            return true;
        }

        public override void HandleMessage(Packet message)
        {
            ZeroPacket zeroPacket = new(message);
            HandleMessage(zeroPacket);
            zeroPacket.Release();
        }

        public abstract void HandleMessage(ZeroPacket message);

        protected void Handle(GetBlockHeadersMessage getBlockHeadersMessage)
        {
            Metrics.Eth62GetBlockHeadersReceived++;
            Stopwatch stopwatch = Stopwatch.StartNew();
            if (Logger.IsTrace)
            {
                Logger.Trace($"Received headers request from {Session.Node:c}:");
                Logger.Trace($"  MaxHeaders: {getBlockHeadersMessage.MaxHeaders}");
                Logger.Trace($"  Reverse: {getBlockHeadersMessage.Reverse}");
                Logger.Trace($"  Skip: {getBlockHeadersMessage.Skip}");
                Logger.Trace($"  StartingBlockhash: {getBlockHeadersMessage.StartBlockHash}");
                Logger.Trace($"  StartingBlockNumber: {getBlockHeadersMessage.StartBlockNumber}");
            }

            // // to clearly state that this client is an ETH client and not ETC (and avoid disconnections on reversed sync)
            // // also to improve performance as this is the most common request
            // if (getBlockHeadersMessage.StartingBlockNumber == 1920000 && getBlockHeadersMessage.MaxHeaders == 1)
            // {
            //     // hardcoded response
            //     // Packet packet = new Packet(ProtocolCode, Eth62MessageCode.BlockHeaders, Bytes.FromHexString("f90210f9020da0a218e2c611f21232d857e3c8cecdcdf1f65f25a4477f98f6f47e4063807f2308a01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d4934794bcdfc35b86bedf72f0cda046a3c16829a2ef41d1a0c5e389416116e3696cce82ec4533cce33efccb24ce245ae9546a4b8f0d5e9a75a07701df8e07169452554d14aadd7bfa256d4a1d0355c1d174ab373e3e2d0a3743a026cf9d9422e9dd95aedc7914db690b92bab6902f5221d62694a2fa5d065f534bb90100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000008638c3bf2616aa831d4c008347e7c08301482084578f7aa88d64616f2d686172642d666f726ba05b5acbf4bf305f948bd7be176047b20623e1417f75597341a059729165b9239788bede87201de42426"));
            //     // Session.DeliverMessage(packet);
            //     LazyInitializer.EnsureInitialized(ref _eth1920000HeaderMessage, () => Deserialize<BlockHeadersMessage>(Bytes.FromHexString("f90210f9020da0a218e2c611f21232d857e3c8cecdcdf1f65f25a4477f98f6f47e4063807f2308a01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d4934794bcdfc35b86bedf72f0cda046a3c16829a2ef41d1a0c5e389416116e3696cce82ec4533cce33efccb24ce245ae9546a4b8f0d5e9a75a07701df8e07169452554d14aadd7bfa256d4a1d0355c1d174ab373e3e2d0a3743a026cf9d9422e9dd95aedc7914db690b92bab6902f5221d62694a2fa5d065f534bb90100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000008638c3bf2616aa831d4c008347e7c08301482084578f7aa88d64616f2d686172642d666f726ba05b5acbf4bf305f948bd7be176047b20623e1417f75597341a059729165b9239788bede87201de42426")));
            //     Session.DeliverMessage(_eth1920000HeaderMessage);
            //     
            //     if (Logger.IsTrace) Logger.Trace($"OUT hardcoded 1920000 BlockHeaders to {Node:c}");
            //     return;
            // }

            Send(FulfillBlockHeadersRequest(getBlockHeadersMessage));
            stopwatch.Stop();
            if (Logger.IsTrace) Logger.Trace($"OUT {Counter:D5} BlockHeaders to {Node:c} in {stopwatch.Elapsed.TotalMilliseconds}ms");
        }

        protected BlockHeadersMessage FulfillBlockHeadersRequest(GetBlockHeadersMessage msg)
        {
            if (msg.MaxHeaders > 1024)
            {
                throw new EthSyncException("Incoming headers request for more than 1024 headers");
            }

            Keccak startingHash = msg.StartBlockHash;
            if (startingHash == null)
            {
                startingHash = SyncServer.FindHash(msg.StartBlockNumber);
            }

            BlockHeader[] headers =
                startingHash == null
                    ? Array.Empty<BlockHeader>()
                    : SyncServer.FindHeaders(startingHash, (int) msg.MaxHeaders, (int) msg.Skip, msg.Reverse == 1);

            headers = FixHeadersForGeth(headers);

            return new BlockHeadersMessage(headers);
        }

        protected void Handle(GetBlockBodiesMessage request)
        {
            Metrics.Eth62GetBlockBodiesReceived++;
            if (Logger.IsTrace)
            {
                Logger.Trace($"Received bodies request of length {request.BlockHashes.Count} from {Session.Node:c}:");
            }

            Stopwatch stopwatch = Stopwatch.StartNew();

            Interlocked.Increment(ref Counter);
            Send(FulfillBlockBodiesRequest(request));
            stopwatch.Stop();
            if (Logger.IsTrace) Logger.Trace($"OUT {Counter:D5} BlockBodies to {Node:c} in {stopwatch.Elapsed.TotalMilliseconds}ms");
        }

        protected BlockBodiesMessage FulfillBlockBodiesRequest(GetBlockBodiesMessage getBlockBodiesMessage)
        {
            IList<Keccak> hashes = getBlockBodiesMessage.BlockHashes;
            Block[] blocks = new Block[hashes.Count];

            ulong sizeEstimate = 0;
            for (int i = 0; i < hashes.Count; i++)
            {
                blocks[i] = SyncServer.Find(hashes[i]);
                sizeEstimate += MessageSizeEstimator.EstimateSize(blocks[i]);

                if (sizeEstimate > SoftOutgoingMessageSizeLimit)
                {
                    break;
                }
            }

            return new BlockBodiesMessage(blocks);
        }
        
        protected void Handle(BlockHeadersMessage message, long size)
        {
            Metrics.Eth62BlockHeadersReceived++;
            _headersRequests.Handle(message.BlockHeaders, size);
        }

        protected void HandleBodies(BlockBodiesMessage blockBodiesMessage, long size)
        {
            Metrics.Eth62BlockBodiesReceived++;
            _bodiesRequests.Handle(blockBodiesMessage.Bodies, size);
        }

        protected void Handle(GetReceiptsMessage msg)
        {
            Metrics.Eth63GetReceiptsReceived++;
            if (msg.Hashes.Count > 512)
            {
                throw new EthSyncException("Incoming receipts request for more than 512 blocks");
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            Send(FulfillReceiptsRequest(msg));
            stopwatch.Stop();
            if (Logger.IsTrace) Logger.Trace($"OUT {Counter:D5} Receipts to {Node:c} in {stopwatch.Elapsed.TotalMilliseconds}ms");
        }

        protected ReceiptsMessage FulfillReceiptsRequest(GetReceiptsMessage getReceiptsMessage)
        {
            TxReceipt[][] txReceipts = new TxReceipt[getReceiptsMessage.Hashes.Count][];

            ulong sizeEstimate = 0;
            for (int i = 0; i < getReceiptsMessage.Hashes.Count; i++)
            {
                txReceipts[i] = SyncServer.GetReceipts(getReceiptsMessage.Hashes[i]);
                for (int j = 0; j < txReceipts[i].Length; j++)
                {
                    sizeEstimate += MessageSizeEstimator.EstimateSize(txReceipts[i][j]);
                }

                if (sizeEstimate > SoftOutgoingMessageSizeLimit)
                {
                    Array.Resize(ref txReceipts, i + 1);
                    break;
                }
            }

            return new ReceiptsMessage(txReceipts);
        }

        private static BlockHeader[] FixHeadersForGeth(BlockHeader[] headers)
        {
            int emptyBlocksAtTheEnd = 0;
            for (int i = 0; i < headers.Length; i++)
            {
                if (headers[headers.Length - 1 - i] == null)
                {
                    emptyBlocksAtTheEnd++;
                }
                else
                {
                    break;
                }
            }

            if (emptyBlocksAtTheEnd != 0)
            {
                BlockHeader[] gethFriendlyHeaders = headers.AsSpan(0, headers.Length - emptyBlocksAtTheEnd).ToArray();
                headers = gethFriendlyHeaders;
            }

            return headers;
        }

        #region Cleanup

        private int _isDisposed;
        
        protected abstract void OnDisposed();

        public override void DisconnectProtocol(DisconnectReason disconnectReason, string details)
        {
            Dispose();
        }

        public override void Dispose()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) == 0)
            {
                OnDisposed();

                try
                {
                    _headersRequests?.CompleteAdding();
                }
                catch (ObjectDisposedException)
                {
                }

                try
                {
                    _bodiesRequests?.CompleteAdding();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        #endregion

        #region IPeerWithSatelliteProtocol

        private IDictionary<string, object>? _protocolHandlers;
        private IDictionary<string, object> ProtocolHandlers => _protocolHandlers ??= new Dictionary<string, object>();

        public void RegisterSatelliteProtocol<T>(string protocol, T protocolHandler) where T : class
        {
            ProtocolHandlers[protocol] = protocolHandler;
        }

        public bool TryGetSatelliteProtocol<T>(string protocol, out T? protocolHandler) where T : class
        {
            if (ProtocolHandlers.TryGetValue(protocol, out object handler))
            {
                protocolHandler = handler as T;
                return protocolHandler != null;
            }

            protocolHandler = null;
            return false;
        }
        
        #endregion
    }
}
