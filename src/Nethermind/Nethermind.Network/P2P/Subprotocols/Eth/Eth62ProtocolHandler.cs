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
using System.Timers;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.TxPools;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public class Eth62ProtocolHandler : ProtocolHandlerBase, IZeroProtocolHandler, ISyncPeer
    {
        private System.Timers.Timer _txFloodCheckTimer;
        protected ISyncServer SyncServer { get; }

        private readonly BlockingCollection<Request<GetBlockHeadersMessage, BlockHeader[]>> _headersRequests
            = new BlockingCollection<Request<GetBlockHeadersMessage, BlockHeader[]>>();

        private readonly BlockingCollection<Request<GetBlockBodiesMessage, BlockBody[]>> _bodiesRequests
            = new BlockingCollection<Request<GetBlockBodiesMessage, BlockBody[]>>();

        private bool _statusReceived;
        private Keccak _remoteHeadBlockHash;
        private readonly ITxPool _txPool;
        private readonly ITimestamper _timestamper;

        private BlockHeadersMessage _eth1920000HeaderMessage;
        
        public Eth62ProtocolHandler(ISession session,
            IMessageSerializationService serializer,
            INodeStatsManager statsManager,
            ISyncServer syncServer,
            ILogManager logManager,
            ITxPool txPool)
            : base(session, statsManager, serializer, logManager)
        {
            SyncServer = syncServer ?? throw new ArgumentNullException(nameof(syncServer));
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _timestamper = Timestamper.Default;

            _txFloodCheckTimer = new System.Timers.Timer(_txFloodCheckInterval.TotalMilliseconds);
            _txFloodCheckTimer.Elapsed += CheckTxFlooding;
            _txFloodCheckTimer.Start();
        }

        private readonly TimeSpan _txFloodCheckInterval = TimeSpan.FromSeconds(60);

        public override string ToString() => $"[Peer|{Node:s}|{ClientId}]";

        private void CheckTxFlooding(object sender, ElapsedEventArgs e)
        {
            if (_notAcceptedTxsSinceLastCheck / _txFloodCheckInterval.TotalSeconds > 100)
            {
                if (Logger.IsDebug) Logger.Debug($"Disconnecting {Node.Id} due to tx flooding");
                InitiateDisconnect(DisconnectReason.UselessPeer, $"tx flooding {_notAcceptedTxsSinceLastCheck}/{_txFloodCheckTimer}");
            }

            if (_notAcceptedTxsSinceLastCheck / _txFloodCheckInterval.TotalSeconds > 10)
            {
                if (Logger.IsDebug) Logger.Debug($"Downgrading {Node.Id} due to tx flooding");
                _isDowngradedDueToTxFlooding = true;
            }

            _notAcceptedTxsSinceLastCheck = 0;

            if (Session.IsClosing)
            {
                DisposeTimer();
            }
        }

        public virtual byte ProtocolVersion => 62;
        public string ProtocolCode => "eth";
        public virtual int MessageIdSpaceSize => 8;

        public Guid SessionId => Session.SessionId;
        public virtual bool IsFastSyncSupported => false;
        public Node Node => Session.Node;
        public string ClientId { get; set; }
        public UInt256 TotalDifficultyOnSessionStart { get; private set; }

        public bool HasAvailableCapability(Capability capability) => false;
        public bool HasAgreedCapability(Capability capability) => false;

        public void AddSupportedCapability(Capability capability)
        {
        }

        public event EventHandler<ProtocolInitializedEventArgs> ProtocolInitialized;

        event EventHandler<ProtocolEventArgs> IProtocolHandler.SubprotocolRequested
        {
            add { }
            remove { }
        }

        public void Init()
        {
            if (Logger.IsTrace) Logger.Trace($"{ProtocolCode} v{ProtocolVersion} subprotocol initializing with {Session.Node:c}");
            if (SyncServer.Head == null)
            {
                throw new InvalidOperationException($"Cannot initialize {ProtocolCode} v{ProtocolVersion} protocol without the head block set");
            }


            BlockHeader head = SyncServer.Head;
            StatusMessage statusMessage = new StatusMessage();
            statusMessage.ChainId = (UInt256) SyncServer.ChainId;
            statusMessage.ProtocolVersion = ProtocolVersion;
            statusMessage.TotalDifficulty = head.TotalDifficulty ?? head.Difficulty;
            statusMessage.BestHash = head.Hash;
            statusMessage.GenesisHash = SyncServer.Genesis.Hash;

            Send(statusMessage);
            Metrics.StatusesSent++;

            //We are expecting receiving Status message anytime from the p2p completion, irrespective of sending Status from our side
            CheckProtocolInitTimeout().ContinueWith(x =>
            {
                if (x.IsFaulted && Logger.IsError)
                {
                    Logger.Error("Error during eth62Protocol handler timeout logic", x.Exception);
                }
            });
        }

        private bool _isDowngradedDueToTxFlooding = false;

        private readonly Random _random = new Random();

        protected long Counter = 0;

        public virtual void HandleMessage(ZeroPacket message)
        {
            if (Logger.IsTrace)
            {
                Logger.Trace($"Handling {message} message from {Session.Node:c}.");
            }

            if (message.PacketType != Eth62MessageCode.Status && !_statusReceived)
            {
                throw new SubprotocolException($"No {nameof(StatusMessage)} received prior to communication with {Session.Node:c}.");
            }

            int size = message.Content.ReadableBytes;
            switch (message.PacketType)
            {
                case Eth62MessageCode.Status:
                    StatusMessage statusMessage = Deserialize<StatusMessage>(message.Content);
                    Handle(statusMessage);
                    break;
                case Eth62MessageCode.NewBlockHashes:
                    Interlocked.Increment(ref Counter);
                    if (Logger.IsTrace) Logger.Trace($"{Counter:D5} NewBlockHashes from {Node:c}");
                    Metrics.Eth62NewBlockHashesReceived++;
                    Handle(Deserialize<NewBlockHashesMessage>(message.Content));
                    break;
                case Eth62MessageCode.Transactions:
                    Interlocked.Increment(ref Counter);
                    Metrics.Eth62TransactionsReceived++;
                    if (!_isDowngradedDueToTxFlooding || 10 > _random.Next(0, 99)) // TODO: disable that when IsMining is set to true
                    {
                        Handle(Deserialize<TransactionsMessage>(message.Content));
                    }

                    break;
                case Eth62MessageCode.GetBlockHeaders:
                    Interlocked.Increment(ref Counter);
                    if (Logger.IsTrace) Logger.Trace($"{Counter:D5} GetBlockHeaders from {Node:c}");
                    Metrics.Eth62GetBlockHeadersReceived++;
                    Handle(Deserialize<GetBlockHeadersMessage>(message.Content));
                    break;
                case Eth62MessageCode.BlockHeaders:
                    Interlocked.Increment(ref Counter);
                    if (Logger.IsTrace) Logger.Trace($"{Counter:D5} BlockHeaders from {Node:c}");
                    Metrics.Eth62BlockHeadersReceived++;
                    Handle(Deserialize<BlockHeadersMessage>(message.Content), size);
                    break;
                case Eth62MessageCode.GetBlockBodies:
                    Interlocked.Increment(ref Counter);
                    if (Logger.IsTrace) Logger.Trace($"{Counter:D5} GetBlockBodies from {Node:c}");
                    Metrics.Eth62GetBlockBodiesReceived++;
                    Handle(Deserialize<GetBlockBodiesMessage>(message.Content));
                    break;
                case Eth62MessageCode.BlockBodies:
                    Interlocked.Increment(ref Counter);
                    if (Logger.IsTrace) Logger.Trace($"{Counter:D5} BlockBodies from {Node:c}");
                    Metrics.Eth62BlockBodiesReceived++;
                    Handle(Deserialize<BlockBodiesMessage>(message.Content), size);
                    break;
                case Eth62MessageCode.NewBlock:
                    Interlocked.Increment(ref Counter);
                    if (Logger.IsTrace) Logger.Trace($"{Counter:D5} NewBlock from {Node:c}");
                    Metrics.Eth62NewBlockReceived++;
                    Handle(Deserialize<NewBlockMessage>(message.Content));
                    break;
            }
        }

        public virtual void HandleMessage(Packet message)
        {
            ZeroPacket zeroPacket = new ZeroPacket(message);
            HandleMessage(zeroPacket);
            zeroPacket.Release();
        }

        public void InitiateDisconnect(DisconnectReason disconnectReason, string details)
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

        public void SendNewTransaction(Transaction transaction)
        {
            Interlocked.Increment(ref Counter);
            if (transaction.Hash == null)
            {
                throw new InvalidOperationException($"Trying to send a transaction with null hash");
            }

            TransactionsMessage msg = new TransactionsMessage(transaction);
            Send(msg);
        }

        public virtual async Task<TxReceipt[][]> GetReceipts(IList<Keccak> blockHash, CancellationToken token)
        {
            await Task.CompletedTask;
            throw new NotSupportedException("Fast sync not supported by eth62 protocol");
        }

        public virtual async Task<byte[][]> GetNodeData(IList<Keccak> hashes, CancellationToken token)
        {
            await Task.CompletedTask;
            throw new NotSupportedException("Fast sync not supported by eth62 protocol");
        }

        protected override TimeSpan InitTimeout => Timeouts.Eth62Status;

        private void Handle(StatusMessage status)
        {
            Metrics.StatusesReceived++;

            if (_statusReceived)
            {
                throw new SubprotocolException($"{nameof(StatusMessage)} has already been received in the past");
            }

            _statusReceived = true;
            if (Logger.IsTrace)
                Logger.Trace($"ETH received status from {Session.Node:c} with" +
                             Environment.NewLine + $" prot version\t{status.ProtocolVersion}" +
                             Environment.NewLine + $" network ID\t{status.ChainId}," +
                             Environment.NewLine + $" genesis hash\t{status.GenesisHash}," +
                             Environment.NewLine + $" best hash\t{status.BestHash}," +
                             Environment.NewLine + $" difficulty\t{status.TotalDifficulty}");

            _remoteHeadBlockHash = status.BestHash;

            ReceivedProtocolInitMsg(status);

            var eventArgs = new EthProtocolInitializedEventArgs(this)
            {
                ChainId = (long) status.ChainId,
                BestHash = status.BestHash,
                GenesisHash = status.GenesisHash,
                Protocol = status.Protocol,
                ProtocolVersion = status.ProtocolVersion,
                TotalDifficulty = status.TotalDifficulty
            };

            if (status.BestHash == new Keccak("0x828f6e9967f75742364c7ab5efd6e64428e60ad38e218789aaf108fbd0232973"))
            {
                InitiateDisconnect(DisconnectReason.UselessPeer, "One of the Rinkeby nodes stuck at Constantinople transition");
            }

            TotalDifficultyOnSessionStart = status.TotalDifficulty;
            ProtocolInitialized?.Invoke(this, eventArgs);
        }

        private long _notAcceptedTxsSinceLastCheck;

        private void Handle(TransactionsMessage msg)
        {
            for (int i = 0; i < msg.Transactions.Length; i++)
            {
                var transaction = msg.Transactions[i];
                transaction.DeliveredBy = Node.Id;
                transaction.Timestamp = _timestamper.EpochSeconds;
                AddTxResult result = _txPool.AddTransaction(transaction, SyncServer.Head.Number);
                if (result == AddTxResult.AlreadyKnown)
                {
                    _notAcceptedTxsSinceLastCheck++;
                }

                if (Logger.IsTrace) Logger.Trace($"{Node:c} sent {transaction.Hash} tx and it was {result} (chain ID = {transaction.Signature.ChainId})");
            }
        }

        private void Handle(GetBlockBodiesMessage request)
        {
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

        private void Handle(GetBlockHeadersMessage getBlockHeadersMessage)
        {
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

            // to clearly state that this client is an ETH client and not ETC (and avoid disconnections on reversed sync)
            // also to improve performance as this is the most common request
            if (getBlockHeadersMessage.StartingBlockNumber == 1920000 && getBlockHeadersMessage.MaxHeaders == 1)
            {
                // hardcoded response
                // Packet packet = new Packet(ProtocolCode, Eth62MessageCode.BlockHeaders, Bytes.FromHexString("f90210f9020da0a218e2c611f21232d857e3c8cecdcdf1f65f25a4477f98f6f47e4063807f2308a01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d4934794bcdfc35b86bedf72f0cda046a3c16829a2ef41d1a0c5e389416116e3696cce82ec4533cce33efccb24ce245ae9546a4b8f0d5e9a75a07701df8e07169452554d14aadd7bfa256d4a1d0355c1d174ab373e3e2d0a3743a026cf9d9422e9dd95aedc7914db690b92bab6902f5221d62694a2fa5d065f534bb90100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000008638c3bf2616aa831d4c008347e7c08301482084578f7aa88d64616f2d686172642d666f726ba05b5acbf4bf305f948bd7be176047b20623e1417f75597341a059729165b9239788bede87201de42426"));
                // Session.DeliverMessage(packet);
                LazyInitializer.EnsureInitialized(ref _eth1920000HeaderMessage, () => Deserialize<BlockHeadersMessage>(Bytes.FromHexString("f90210f9020da0a218e2c611f21232d857e3c8cecdcdf1f65f25a4477f98f6f47e4063807f2308a01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d4934794bcdfc35b86bedf72f0cda046a3c16829a2ef41d1a0c5e389416116e3696cce82ec4533cce33efccb24ce245ae9546a4b8f0d5e9a75a07701df8e07169452554d14aadd7bfa256d4a1d0355c1d174ab373e3e2d0a3743a026cf9d9422e9dd95aedc7914db690b92bab6902f5221d62694a2fa5d065f534bb90100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000008638c3bf2616aa831d4c008347e7c08301482084578f7aa88d64616f2d686172642d666f726ba05b5acbf4bf305f948bd7be176047b20623e1417f75597341a059729165b9239788bede87201de42426")));
                Session.DeliverMessage(_eth1920000HeaderMessage);
                
                if (Logger.IsTrace) Logger.Trace($"OUT hardcoded 1920000 BlockHeaders to {Node:c}");
                return;
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

        private void Handle(BlockBodiesMessage message, long size)
        {
            Request<GetBlockBodiesMessage, BlockBody[]> request = _bodiesRequests.Take();
            if (message.PacketType == Eth62MessageCode.BlockBodies)
            {
                request.ResponseSize = size;
                request.CompletionSource.SetResult(message.Bodies);
            }
        }

        private void Handle(BlockHeadersMessage message, long size)
        {
            var request = _headersRequests.Take();
            if (message.PacketType == Eth62MessageCode.BlockHeaders)
            {
                request.ResponseSize = size;
                request.CompletionSource.SetResult(message.BlockHeaders);
            }
        }

        private void Handle(NewBlockHashesMessage newBlockHashes)
        {
            foreach ((Keccak Hash, long Number) hint in newBlockHashes.BlockHashes)
            {
                SyncServer.HintBlock(hint.Hash, hint.Number, Node);
            }
        }

        private void Handle(NewBlockMessage newBlockMessage)
        {
            newBlockMessage.Block.TotalDifficulty = newBlockMessage.TotalDifficulty;

            try
            {
                SyncServer.AddNewBlock(newBlockMessage.Block, Session.Node);
            }
            catch (Exception e)
            {
                Logger.Error($"Adding new block {newBlockMessage.Block?.ToString(Block.Format.Short)} from {Node:c} failed", e);
                throw;
            }
        }

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

            var request = new Request<GetBlockHeadersMessage, BlockHeader[]>(message);
            _headersRequests.Add(request, token);
            request.StartMeasuringTime();

            Send(request.Message);
            Task<BlockHeader[]> task = request.CompletionSource.Task;
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

            StatsManager.ReportTransferSpeedEvent(Session.Node, 0);
            throw new TimeoutException($"{Session} Request timeout in {nameof(GetBlockHeadersMessage)} with {message.MaxHeaders} max headers");
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

            var request = new Request<GetBlockBodiesMessage, BlockBody[]>(message);
            _bodiesRequests.Add(request, token);
            request.StartMeasuringTime();

            Send(request.Message);

            Task<BlockBody[]> task = request.CompletionSource.Task;
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
            throw new TimeoutException($"{Session} Request timeout in {nameof(GetBlockBodiesMessage)} with {message.BlockHashes.Count} block hashes");
        }

        async Task<BlockHeader[]> ISyncPeer.GetBlockHeaders(Keccak blockHash, int maxBlocks, int skip, CancellationToken token)
        {
            if (maxBlocks == 0)
            {
                return new BlockHeader[0];
            }

            var msg = new GetBlockHeadersMessage();
            msg.MaxHeaders = maxBlocks;
            msg.Reverse = 0;
            msg.Skip = skip;
            msg.StartingBlockHash = blockHash;

            BlockHeader[] headers = await SendRequest(msg, token);
            return headers;
        }

        async Task<BlockHeader[]> ISyncPeer.GetBlockHeaders(long number, int maxBlocks, int skip, CancellationToken token)
        {
            if (maxBlocks == 0)
            {
                return new BlockHeader[0];
            }

            var msg = new GetBlockHeadersMessage();
            msg.MaxHeaders = maxBlocks;
            msg.Reverse = 0;
            msg.Skip = skip;
            msg.StartingBlockNumber = number;

            // Logger.Info($"Sending headers request ({number}, {maxBlocks}, {skip}) to {this}");

            BlockHeader[] headers = await SendRequest(msg, token);
            // int nonNullCount = 0;
            // for (int i = 0; i < headers.Length; i++)
            // {
            //     if (headers[i] != null)
            //     {
            //         nonNullCount++;
            //     }
            // }

            // Logger.Info($"Sent headers request ({number}, {maxBlocks}, {skip}) to {this} - received {headers.Length}, out of which {nonNullCount} non null");
            return headers;
        }

        public void Disconnect(DisconnectReason reason, string details)
        {
            if (Logger.IsDebug) Logger.Debug($"Disconnecting {Node:c} bacause of the {details}");
            Session.InitiateDisconnect(reason, details);
        }

        async Task<BlockBody[]> ISyncPeer.GetBlockBodies(IList<Keccak> blockHashes, CancellationToken token)
        {
            // Logger.Info($"Sending bodies request ({blockHashes.Count}) to {this}");
            if (blockHashes.Count == 0)
            {
                return new BlockBody[0];
            }

            var bodiesMsg = new GetBlockBodiesMessage(blockHashes);

            BlockBody[] blocks = await SendRequest(bodiesMsg, token);

            // int nonNullCount = 0;
            // for (int i = 0; i < blocks.Length; i++)
            // {
            //     if (blocks[i] != null)
            //     {
            //         nonNullCount++;
            //     }
            // }

            // Logger.Info($"Sent bodies request ({blockHashes.Count}) to {this} - received {blocks.Length}, out of which {nonNullCount} non null");
            return blocks;
        }

        async Task<BlockHeader> ISyncPeer.GetHeadBlockHeader(Keccak hash, CancellationToken token)
        {
            var msg = new GetBlockHeadersMessage();
            msg.StartingBlockHash = hash ?? _remoteHeadBlockHash;
            msg.MaxHeaders = 1;
            msg.Reverse = 0;
            msg.Skip = 0;

            BlockHeader[] headers = await SendRequest(msg, token);
            return headers.Length > 0 ? headers[0] : null;
        }

        private bool _isDisposed;

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            DisposeTimer();

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

            _isDisposed = true;
        }

        private void DisposeTimer()
        {
            try
            {
                _txFloodCheckTimer.Elapsed -= CheckTxFlooding;
                _txFloodCheckTimer.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (NullReferenceException)
            {
            }
        }
    }
}