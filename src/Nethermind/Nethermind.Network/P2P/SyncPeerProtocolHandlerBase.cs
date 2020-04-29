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
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.P2P.Subprotocols.Eth.V65;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P
{
    public abstract class SyncPeerProtocolHandlerBase : ProtocolHandlerBase, ISyncPeer
    {
        public Node Node => Session?.Node;
        public string ClientId => Session?.Node?.ClientId;
        public UInt256 TotalDifficulty { get; set; }
        public PublicKey Id => Node.Id;
        protected ISyncServer SyncServer { get; }
        
        public long HeadNumber { get; set; }
        public Keccak HeadHash { get; set; }
        
        // this mean that we know what the number, hash, and total diff of the head block is
        public bool IsInitialized { get; set; }
        
        public override string ToString() => $"[Peer|{Name}|{HeadNumber}|{ClientId}|{Node:s}]";

        protected Keccak _remoteHeadBlockHash;
        protected ITxPool _txPool;
        protected ITimestamper _timestamper;

        protected readonly BlockingCollection<Request<GetBlockHeadersMessage, BlockHeader[]>> _headersRequests
            = new BlockingCollection<Request<GetBlockHeadersMessage, BlockHeader[]>>();

        protected readonly BlockingCollection<Request<GetBlockBodiesMessage, BlockBody[]>> _bodiesRequests
            = new BlockingCollection<Request<GetBlockBodiesMessage, BlockBody[]>>();

        protected SyncPeerProtocolHandlerBase(ISession session,
            IMessageSerializationService serializer,
            INodeStatsManager statsManager,
            ISyncServer syncServer,
            ITxPool txPool,
            ILogManager logManager) : base(session, statsManager, serializer, logManager)
        {
            SyncServer = syncServer ?? throw new ArgumentNullException(nameof(syncServer));
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _timestamper = Timestamper.Default;
        }

        public void Disconnect(DisconnectReason reason, string details)
        {
            if (Logger.IsDebug) Logger.Debug($"Disconnecting {Node:c} bacause of the {details}");
            Session.InitiateDisconnect(reason, details);
        }

        async Task<BlockBody[]> ISyncPeer.GetBlockBodies(IList<Keccak> blockHashes, CancellationToken token)
        {
            if (blockHashes.Count == 0)
            {
                return new BlockBody[0];
            }

            GetBlockBodiesMessage bodiesMsg = new GetBlockBodiesMessage(blockHashes);

            BlockBody[] blocks = await SendRequest(bodiesMsg, token);

            return blocks;
        }

        [Todo(Improve.Refactor, "Generic approach to requests")]
        private async Task<BlockBody[]> SendRequest(GetBlockBodiesMessage message, CancellationToken token)
        {
            if (_headersRequests.IsAddingCompleted || _isDisposed)
            {
                throw new TimeoutException("Session disposed");
            }

            if (Logger.IsTrace)
            {
                Logger.Trace("Sending bodies request:");
                Logger.Trace($"Blockhashes count: {message.BlockHashes.Count}");
            }

            Request<GetBlockBodiesMessage, BlockBody[]> request = new Request<GetBlockBodiesMessage, BlockBody[]>(message);
            _bodiesRequests.Add(request, token);
            request.StartMeasuringTime();

            Send(request.Message);

            Task<BlockBody[]> task = request.CompletionSource.Task;
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
                return new BlockHeader[0];
            }

            GetBlockHeadersMessage msg = new GetBlockHeadersMessage();
            msg.MaxHeaders = maxBlocks;
            msg.Reverse = 0;
            msg.Skip = skip;
            msg.StartingBlockNumber = number;

            BlockHeader[] headers = await SendRequest(msg, token);
            return headers;
        }

        async Task<BlockHeader[]> ISyncPeer.GetBlockHeaders(Keccak blockHash, int maxBlocks, int skip, CancellationToken token)
        {
            if (maxBlocks == 0)
            {
                return new BlockHeader[0];
            }

            GetBlockHeadersMessage msg = new GetBlockHeadersMessage();
            msg.MaxHeaders = maxBlocks;
            msg.Reverse = 0;
            msg.Skip = skip;
            msg.StartingBlockHash = blockHash;

            BlockHeader[] headers = await SendRequest(msg, token);
            return headers;
        }

        [Todo(Improve.Refactor, "Generic approach to requests")]
        private async Task<BlockHeader[]> SendRequest(GetBlockHeadersMessage message, CancellationToken token)
        {
            if (_headersRequests.IsAddingCompleted || _isDisposed)
            {
                throw new TimeoutException("Session disposed");
            }

            if (Logger.IsTrace)
            {
                Logger.Trace($"Sending headers request to {Session.Node:c}:");
                Logger.Trace($"  Starting blockhash: {message.StartingBlockHash}");
                Logger.Trace($"  Starting number: {message.StartingBlockNumber}");
                Logger.Trace($"  Skip: {message.Skip}");
                Logger.Trace($"  Reverse: {message.Reverse}");
                Logger.Trace($"  Max headers: {message.MaxHeaders}");
            }

            Request<GetBlockHeadersMessage, BlockHeader[]> request = new Request<GetBlockHeadersMessage, BlockHeader[]>(message);
            _headersRequests.Add(request, token);
            request.StartMeasuringTime();

            Send(request.Message);
            Task<BlockHeader[]> task = request.CompletionSource.Task;
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

                StatsManager.ReportTransferSpeedEvent(Session.Node, TransferSpeedType.Headers, bytesPerMillisecond);
                return task.Result;
            }

            StatsManager.ReportTransferSpeedEvent(Session.Node, TransferSpeedType.Headers,0);
            throw new TimeoutException($"{Session} Request timeout in {nameof(GetBlockHeadersMessage)} with {message.MaxHeaders} max headers");
        }

        async Task<BlockHeader> ISyncPeer.GetHeadBlockHeader(Keccak hash, CancellationToken token)
        {
            GetBlockHeadersMessage msg = new GetBlockHeadersMessage();
            msg.StartingBlockHash = hash ?? _remoteHeadBlockHash;
            msg.MaxHeaders = 1;
            msg.Reverse = 0;
            msg.Skip = 0;

            BlockHeader[] headers = await SendRequest(msg, token);
            return headers.Length > 0 ? headers[0] : null;
        }

        public virtual async Task<TxReceipt[][]> GetReceipts(IList<Keccak> blockHash, CancellationToken token)
        {
            await Task.CompletedTask;
            throw new NotSupportedException("Fast sync not supported by eth62 protocol");
        }

        public void SendNewBlock(Block block)
        {
            if (Logger.IsTrace) Logger.Trace($"OUT {Counter:D5} NewBlock to {Node:c}");
            if (block.TotalDifficulty == null)
            {
                throw new InvalidOperationException($"Trying to send a block {block.Hash} with null total difficulty");
            }

            NewBlockMessage msg = new NewBlockMessage();
            msg.Block = block;
            msg.TotalDifficulty = block.TotalDifficulty ?? 0;

            Send(msg);
        }

        public void HintNewBlock(Keccak blockHash, long number)
        {
            if (Logger.IsTrace) Logger.Trace($"OUT {Counter:D5} HintBlock to {Node:c}");

            NewBlockHashesMessage msg = new NewBlockHashesMessage();
            msg.BlockHashes = new[] {(blockHash, number)};
            Send(msg);
        }

        public virtual async Task<byte[][]> GetNodeData(IList<Keccak> hashes, CancellationToken token)
        {
            await Task.CompletedTask;
            throw new NotSupportedException("Fast sync not supported by eth62 protocol");
        }

        public virtual void SendNewTransaction(Transaction transaction, bool isPriority)
        {
            Interlocked.Increment(ref Counter);
            if (transaction.Hash == null)
            {
                throw new InvalidOperationException("Trying to send a transaction with null hash");
            }

            TransactionsMessage msg = new TransactionsMessage(new[] {transaction});
            Send(msg);
        }

        public override void HandleMessage(Packet message)
        {
            ZeroPacket zeroPacket = new ZeroPacket(message);
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
                Logger.Trace($"  StartingBlockhash: {getBlockHeadersMessage.StartingBlockHash}");
                Logger.Trace($"  StartingBlockNumber: {getBlockHeadersMessage.StartingBlockNumber}");
            }

            Interlocked.Increment(ref Counter);

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

            if (getBlockHeadersMessage.MaxHeaders > 1024)
            {
                throw new EthSyncException("Incoming headers request for more than 1024 headers");
            }

            Keccak startingHash = getBlockHeadersMessage.StartingBlockHash;
            if (startingHash == null)
            {
                startingHash = SyncServer.FindHash(getBlockHeadersMessage.StartingBlockNumber);
            }

            BlockHeader[] headers =
                startingHash == null
                    ? Array.Empty<BlockHeader>()
                    : SyncServer.FindHeaders(startingHash, (int) getBlockHeadersMessage.MaxHeaders, (int) getBlockHeadersMessage.Skip, getBlockHeadersMessage.Reverse == 1);

            headers = FixHeadersForGeth(headers);

            Send(new BlockHeadersMessage(headers));
            stopwatch.Stop();
            if (Logger.IsTrace) Logger.Trace($"OUT {Counter:D5} BlockHeaders to {Node:c} in {stopwatch.Elapsed.TotalMilliseconds}ms");
        }

        protected void Handle(BlockHeadersMessage message, long size)
        {
            Metrics.Eth62BlockHeadersReceived++;
            Request<GetBlockHeadersMessage, BlockHeader[]> request = _headersRequests.Take();
            if (message.PacketType == Eth62MessageCode.BlockHeaders)
            {
                request.ResponseSize = size;
                request.CompletionSource.SetResult(message.BlockHeaders);
            }
        }

        protected void Handle(GetBlockBodiesMessage request)
        {
            Metrics.Eth62GetBlockBodiesReceived++;
            if (request.BlockHashes.Count > 512)
            {
                throw new EthSyncException("Incoming bodies request for more than 512 bodies");
            }

            if (Logger.IsTrace)
            {
                Logger.Trace($"Received bodies request of length {request.BlockHashes.Count} from {Session.Node:c}:");
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            IList<Keccak> hashes = request.BlockHashes;
            Block[] blocks = new Block[hashes.Count];

            for (int i = 0; i < hashes.Count; i++)
            {
                blocks[i] = SyncServer.Find(hashes[i]);
            }

            Interlocked.Increment(ref Counter);
            Send(new BlockBodiesMessage(blocks));
            stopwatch.Stop();
            if (Logger.IsTrace) Logger.Trace($"OUT {Counter:D5} BlockBodies to {Node:c} in {stopwatch.Elapsed.TotalMilliseconds}ms");
        }

        protected void Handle(BlockBodiesMessage message, long size)
        {
            Metrics.Eth62BlockBodiesReceived++;
            Request<GetBlockBodiesMessage, BlockBody[]> request = _bodiesRequests.Take();
            if (message.PacketType == Eth62MessageCode.BlockBodies)
            {
                request.ResponseSize = size;
                request.CompletionSource.SetResult(message.Bodies);
            }
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

        private bool _isDisposed;
        protected abstract void OnDisposed();

        // todo - why can't this just be Dispose()?
        public override void InitiateDisconnect(DisconnectReason disconnectReason, string details)
        {
            try
            {
                _headersRequests.CompleteAdding();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                _bodiesRequests.CompleteAdding();
            }
            catch (ObjectDisposedException)
            {
            }

            Session.MarkDisconnected(disconnectReason, DisconnectType.Local, details);
        }

        public override void Dispose()
        {
            // todo Interlocked.Exchange?
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;

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

        #endregion

        protected class Request<TMsg, TResult>
        {
            public Request(TMsg message)
            {
                CompletionSource = new TaskCompletionSource<TResult>();
                Message = message;
            }

            public void StartMeasuringTime()
            {
                Stopwatch = Stopwatch.StartNew();
            }

            public long FinishMeasuringTime()
            {
                Stopwatch.Stop();
                return Stopwatch.ElapsedMilliseconds;
            }

            private Stopwatch Stopwatch { get; set; }
            public long ResponseSize { get; set; }
            public TMsg Message { get; }
            public TaskCompletionSource<TResult> CompletionSource { get; }
        }
    }
}