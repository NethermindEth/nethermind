/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.TxPools;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public class Eth62ProtocolHandler : ProtocolHandlerBase, IProtocolHandler, ISyncPeer
    {
        private System.Timers.Timer _txFloodCheckTimer;
        protected ISyncServer SyncServer { get; }

        private readonly BlockingCollection<Request<GetBlockHeadersMessage, BlockHeader[]>> _headersRequests
            = new BlockingCollection<Request<GetBlockHeadersMessage, BlockHeader[]>>();

        private readonly BlockingCollection<Request<GetBlockBodiesMessage, Block[]>> _bodiesRequests
            = new BlockingCollection<Request<GetBlockBodiesMessage, Block[]>>();

        private bool _statusReceived;
        private Keccak _remoteHeadBlockHash;
        protected IPerfService _perfService;
        private readonly ITxPool _txPool;
        private readonly ITimestamp _timestamp;

        public Eth62ProtocolHandler(
            ISession session,
            IMessageSerializationService serializer,
            INodeStatsManager statsManager,
            ISyncServer syncServer,
            ILogManager logManager,
            IPerfService perfService,
            ITxPool txPool)
            : base(session, statsManager, serializer, logManager)
        {
            SyncServer = syncServer;
            _perfService = perfService ?? throw new ArgumentNullException(nameof(perfService));
            _txPool = txPool;
            _timestamp = new Timestamp();

            _txFloodCheckTimer = new System.Timers.Timer(_txFloodCheckInterval.TotalMilliseconds);
            _txFloodCheckTimer.Elapsed += CheckTxFlooding;
            _txFloodCheckTimer.Start();
        }

        private TimeSpan _txFloodCheckInterval = TimeSpan.FromSeconds(60);

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
        }

        public virtual byte ProtocolVersion => 62;
        public string ProtocolCode => "eth";
        public virtual int MessageIdSpaceSize => 8;

        public Guid SessionId => Session.SessionId;
        public virtual bool IsFastSyncSupported => false;
        public Node Node => Session.Node;
        public string ClientId { get; set; }
        public UInt256 TotalDifficultyOnSessionStart { get; private set; }

        public event EventHandler<ProtocolInitializedEventArgs> ProtocolInitialized;

        event EventHandler<ProtocolEventArgs> IProtocolHandler.SubprotocolRequested
        {
            add { }
            remove { }
        }

        public void Init()
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} {ProtocolCode} v{ProtocolVersion} subprotocol initializing");
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

        private Random _random = new Random();

        protected long _counter = 0;
        
        public virtual void HandleMessage(Packet message)
        {
            if (Logger.IsTrace)
            {
                Logger.Trace($"{Session.RemoteNodeId} {nameof(Eth62ProtocolHandler)} handling a message with code {message.PacketType}.");
            }

            if (message.PacketType != Eth62MessageCode.Status && !_statusReceived)
            {
                throw new SubprotocolException($"{Session.RemoteNodeId} No {nameof(StatusMessage)} received prior to communication.");
            }

            switch (message.PacketType)
            {
                case Eth62MessageCode.Status:
                    StatusMessage statusMessage = Deserialize<StatusMessage>(message.Data);
                    if (statusMessage.StrangePrefix != null)
                    {
//                        Node.ClientId = $"STRANGE_PREFIX({statusMessage.StrangePrefix}) " + Node.ClientId;
                    }

                    Handle(statusMessage);
                    break;
                case Eth62MessageCode.NewBlockHashes:
                    Interlocked.Increment(ref _counter);
                    Logger.Warn($"{_counter:D5} NewBlockHashes from {Node:s}");
                    Metrics.Eth62NewBlockHashesReceived++;
                    Handle(Deserialize<NewBlockHashesMessage>(message.Data));
                    break;
                case Eth62MessageCode.Transactions:
                    Interlocked.Increment(ref _counter);
                    Metrics.Eth62TransactionsReceived++;
                    if (!_isDowngradedDueToTxFlooding || 10 > _random.Next(0, 99)) // TODO: disable that when IsMining is set to true
                    {
                        Handle(Deserialize<TransactionsMessage>(message.Data));
                    }

                    break;
                case Eth62MessageCode.GetBlockHeaders:
                    Interlocked.Increment(ref _counter);
                    Logger.Warn($"{_counter:D5} GetBlockHeaders from {Node:s}");
                    Metrics.Eth62GetBlockHeadersReceived++;
                    Handle(Deserialize<GetBlockHeadersMessage>(message.Data));
                    break;
                case Eth62MessageCode.BlockHeaders:
                    Interlocked.Increment(ref _counter);
                    Logger.Warn($"{_counter:D5} BlockHeaders from {Node:s}");
                    Metrics.Eth62BlockHeadersReceived++;
                    Handle(Deserialize<BlockHeadersMessage>(message.Data));
                    break;
                case Eth62MessageCode.GetBlockBodies:
                    Interlocked.Increment(ref _counter);
                    Logger.Warn($"{_counter:D5} GetBlockBodies from {Node:s}");
                    Metrics.Eth62GetBlockBodiesReceived++;
                    Handle(Deserialize<GetBlockBodiesMessage>(message.Data));
                    break;
                case Eth62MessageCode.BlockBodies:
                    Interlocked.Increment(ref _counter);
                    Logger.Warn($"{_counter:D5} BlockBodies from {Node:s}");
                    Metrics.Eth62BlockBodiesReceived++;
                    Handle(Deserialize<BlockBodiesMessage>(message.Data));
                    break;
                case Eth62MessageCode.NewBlock:
                    Interlocked.Increment(ref _counter);
                    Logger.Warn($"{_counter:D5} NewBlock from {Node:s}");
                    Metrics.Eth62NewBlockReceived++;
                    Handle(Deserialize<NewBlockMessage>(message.Data));
                    break;
            }
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

            Session.Disconnect(disconnectReason, DisconnectType.Local, details);
        }

        public void SendNewBlock(Block block)
        {
            Logger.Warn($"OUT {_counter:D5} NewBlock to {Node:s}");
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
            Interlocked.Increment(ref _counter);
            if (transaction.Hash == null)
            {
                throw new InvalidOperationException($"Trying to send a transaction with null hash");
            }

            TransactionsMessage msg = new TransactionsMessage(transaction);
            Send(msg);
        }

        public virtual async Task<TransactionReceipt[][]> GetReceipts(Keccak[] blockHash, CancellationToken token)
        {
            await Task.CompletedTask;
            throw new NotSupportedException("Fast sync not supported by eth62 protocol");
        }

        public virtual async Task<byte[][]> GetNodeData(Keccak[] hashes, CancellationToken token)
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
                Logger.Trace($"{Session.RemoteNodeId} ETH received status with" +
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
                transaction.Timestamp = _timestamp.EpochSeconds;
                AddTransactionResult result = _txPool.AddTransaction(transaction, SyncServer.Head.Number);
                if (result == AddTransactionResult.AlreadyKnown)
                {
                    _notAcceptedTxsSinceLastCheck++;
                }

                if (Logger.IsTrace) Logger.Trace($"{Node.Id} sent {transaction.Hash} and it was {result} (chain ID = {transaction.Signature.GetChainId})");
            }
        }

        private void Handle(GetBlockBodiesMessage request)
        {
            Keccak[] hashes = request.BlockHashes;
            Block[] blocks = new Block[hashes.Length];

            for (int i = 0; i < hashes.Length; i++)
            {
                blocks[i] = SyncServer.Find(hashes[i]);
            }

            Interlocked.Increment(ref _counter);
            Logger.Warn($"OUT {_counter:D5} BlockBodies to {Node:s}");
            Send(new BlockBodiesMessage(blocks));
        }

        private void Handle(GetBlockHeadersMessage getBlockHeadersMessage)
        {
            if (Logger.IsTrace || _counter < 6)
            {
                Logger.Trace($"GetBlockHeaders.MaxHeaders: {getBlockHeadersMessage.MaxHeaders}");
                Logger.Trace($"GetBlockHeaders.Reverse: {getBlockHeadersMessage.Reverse}");
                Logger.Trace($"GetBlockHeaders.Skip: {getBlockHeadersMessage.Skip}");
                Logger.Trace($"GetBlockHeaders.StartingBlockhash: {getBlockHeadersMessage.StartingBlockHash}");
                Logger.Trace($"GetBlockHeaders.StartingBlockNumber: {getBlockHeadersMessage.StartingBlockNumber}");
            }

            Keccak startingHash = getBlockHeadersMessage.StartingBlockHash;
            if (startingHash == null)
            {
                startingHash = SyncServer.FindHeader(getBlockHeadersMessage.StartingBlockNumber)?.Hash;
            }

            BlockHeader[] headers =
                startingHash == null
                    ? new BlockHeader[0]
                    : SyncServer.FindHeaders(startingHash, (int) getBlockHeadersMessage.MaxHeaders, (int) getBlockHeadersMessage.Skip, getBlockHeadersMessage.Reverse == 1);

            if (_counter < 6 && headers.Length < 10)
            {
                foreach (BlockHeader blockHeader in headers)
                {
                    Logger.Info("Sending header: " + blockHeader?.ToString(BlockHeader.Format.Short));
                }
            }
            
            Interlocked.Increment(ref _counter);
            Logger.Warn($"OUT {_counter:D5} BlockHeaders to {Node:s}");
            Send(new BlockHeadersMessage(headers));
        }

        private void Handle(BlockBodiesMessage message)
        {
            Block[] blocks = new Block[message.Bodies.Length];
            for (int i = 0; i < message.Bodies.Length; i++)
            {
                Block block = new Block(null, message.Bodies[i].Transactions, message.Bodies[i].Ommers);
                blocks[i] = block;
            }

            var request = _bodiesRequests.Take();
            if (message.PacketType == Eth62MessageCode.BlockBodies)
            {
                request.CompletionSource.SetResult(blocks);
            }
        }

        private void Handle(BlockHeadersMessage message)
        {
            var request = _headersRequests.Take();
            if (message.PacketType == Eth62MessageCode.BlockHeaders)
            {
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
            newBlockMessage.Block.TotalDifficulty = (UInt256) newBlockMessage.TotalDifficulty;
            SyncServer.AddNewBlock(newBlockMessage.Block, Session.Node);
        }

        protected class Request<TMsg, TResult>
        {
            public Request(TMsg message)
            {
                CompletionSource = new TaskCompletionSource<TResult>();
                Message = message;
            }

            public TMsg Message { get; }
            public TaskCompletionSource<TResult> CompletionSource { get; }
        }

        [Todo(Improve.Refactor, "Generic approach to requests")]
        private async Task<BlockHeader[]> SendRequest(GetBlockHeadersMessage message, CancellationToken token)
        {
            if (_headersRequests.IsAddingCompleted || _isDisposed)
            {
                throw new EthSynchronizationException("Session disposed");
            }
            
            if (Logger.IsTrace)
            {
                Logger.Trace("Sending headers request:");
                Logger.Trace($"Starting blockhash: {message.StartingBlockHash}");
                Logger.Trace($"Starting number: {message.StartingBlockNumber}");
                Logger.Trace($"Skip: {message.Skip}");
                Logger.Trace($"Reverse: {message.Reverse}");
                Logger.Trace($"Max headers: {message.MaxHeaders}");
            }

            var request = new Request<GetBlockHeadersMessage, BlockHeader[]>(message);
            _headersRequests.Add(request, token);

            var perfCalcId = _perfService.StartPerfCalc();

            Send(request.Message);
            Task<BlockHeader[]> task = request.CompletionSource.Task;
            var firstTask = await Task.WhenAny(task, Task.Delay(Timeouts.Eth, token));
            if (firstTask.IsCanceled)
            {
                token.ThrowIfCancellationRequested();
            }

            if (firstTask == task)
            {
                var latency = _perfService.EndPerfCalc(perfCalcId);
                if (latency.HasValue)
                {
                    StatsManager.ReportLatencyCaptureEvent(Session.Node, NodeLatencyStatType.BlockHeaders, latency.Value);
                }

                return task.Result;
            }

            _perfService.EndPerfCalc(perfCalcId);
            throw new TimeoutException($"{Session} Request timeout in {nameof(GetBlockHeadersMessage)} with {message.MaxHeaders} max headers");
        }

        [Todo(Improve.Refactor, "Generic approach to requests")]
        private async Task<Block[]> SendRequest(GetBlockBodiesMessage message, CancellationToken token)
        {
            if (_headersRequests.IsAddingCompleted || _isDisposed)
            {
                throw new EthSynchronizationException("Session disposed");
            }
            
            if (Logger.IsTrace)
            {
                Logger.Trace("Sending bodies request:");
                Logger.Trace($"Blockhashes count: {message.BlockHashes.Length}");
            }

            var request = new Request<GetBlockBodiesMessage, Block[]>(message);
            _bodiesRequests.Add(request, token);
            var perfCalcId = _perfService.StartPerfCalc();

            Send(request.Message);

            Task<Block[]> task = request.CompletionSource.Task;
            var firstTask = await Task.WhenAny(task, Task.Delay(Timeouts.Eth, token));
            if (firstTask.IsCanceled)
            {
                token.ThrowIfCancellationRequested();
            }

            if (firstTask == task)
            {
                var latency = _perfService.EndPerfCalc(perfCalcId);
                if (latency.HasValue)
                {
                    StatsManager.ReportLatencyCaptureEvent(Session.Node, NodeLatencyStatType.BlockBodies, latency.Value);
                }

                return task.Result;
            }

            throw new TimeoutException($"{Session} Request timeout in {nameof(GetBlockBodiesMessage)} with {message.BlockHashes.Length} block hashes");
        }

        async Task<BlockHeader[]> ISyncPeer.GetBlockHeaders(Keccak blockHash, int maxBlocks, int skip, CancellationToken token)
        {
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
            var msg = new GetBlockHeadersMessage();
            msg.MaxHeaders = maxBlocks;
            msg.Reverse = 0;
            msg.Skip = skip;
            msg.StartingBlockNumber = number;

            BlockHeader[] headers = await SendRequest(msg, token);
            return headers;
        }

        public void Disconnect(DisconnectReason reason, string details)
        {
            Session.InitiateDisconnect(reason, details);
        }

        async Task<Block[]> ISyncPeer.GetBlocks(Keccak[] blockHashes, CancellationToken token)
        {
            var bodiesMsg = new GetBlockBodiesMessage(blockHashes);

            Block[] blocks = await SendRequest(bodiesMsg, token);
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
            if(_isDisposed)
            {
                return;
            }
            
            _headersRequests?.Dispose();
            _bodiesRequests?.Dispose();

            _txFloodCheckTimer.Elapsed -= CheckTxFlooding;
            _txFloodCheckTimer?.Dispose();
            _isDisposed = true;
        }
    }
}