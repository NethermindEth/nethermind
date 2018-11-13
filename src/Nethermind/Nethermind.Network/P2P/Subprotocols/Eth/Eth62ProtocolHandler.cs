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
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.TransactionPools;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public class Eth62ProtocolHandler : ProtocolHandlerBase, IProtocolHandler, ISynchronizationPeer
    {
        protected ISynchronizationManager SyncManager { get; }

        private readonly BlockingCollection<Request<GetBlockHeadersMessage, BlockHeader[]>> _headersRequests
            = new BlockingCollection<Request<GetBlockHeadersMessage, BlockHeader[]>>();

        private readonly BlockingCollection<Request<GetBlockBodiesMessage, Block[]>> _bodiesRequests
            = new BlockingCollection<Request<GetBlockBodiesMessage, Block[]>>();

        private bool _statusReceived;
        private Keccak _remoteHeadBlockHash;
        private BigInteger _remoteHeadBlockDifficulty;
        private IPerfService _perfService;
        private readonly IBlockTree _blockTree;
        private readonly ITransactionPool _transactionPool;
        private readonly ITimestamp _timestamp;

        public Eth62ProtocolHandler(
            IP2PSession p2PSession,
            IMessageSerializationService serializer,
            ISynchronizationManager syncManager,
            ILogManager logManager,
            IPerfService perfService,
            IBlockTree blockTree,
            ITransactionPool transactionPool,
            ITimestamp timestamp)
            : base(p2PSession, serializer, logManager)
        {
            SyncManager = syncManager;
            _perfService = perfService ?? throw new ArgumentNullException(nameof(perfService));
            _blockTree = blockTree;
            _transactionPool = transactionPool;
            _timestamp = timestamp;
        }

        public virtual byte ProtocolVersion => 62;
        public string ProtocolCode => "eth";
        public virtual int MessageIdSpaceSize => 8;
        public virtual bool IsFastSyncSupported => false;
        public NodeId NodeId => P2PSession.RemoteNodeId;
        public INodeStats NodeStats => P2PSession.NodeStats;
        public string ClientId { get; set; }
        public event EventHandler<ProtocolInitializedEventArgs> ProtocolInitialized;
        event EventHandler<ProtocolEventArgs> IProtocolHandler.SubprotocolRequested
        {
            add
            {
            }
            remove
            {
            }
        }

        public void Init()
        {
            Logger.Trace($"{P2PSession.RemoteNodeId} {ProtocolCode} v{ProtocolVersion} subprotocol initializing");
            if (SyncManager.Head == null)
            {
                throw new InvalidOperationException("Initializing sync protocol without the head block set");
            }

            BlockHeader head = SyncManager.Head;
            StatusMessage statusMessage = new StatusMessage();
            statusMessage.ChainId = SyncManager.ChainId;
            statusMessage.ProtocolVersion = ProtocolVersion;
            statusMessage.TotalDifficulty = head.Difficulty;
            statusMessage.BestHash = head.Hash;
            statusMessage.GenesisHash = SyncManager.Genesis.Hash;

            Send(statusMessage);

            //We are expecting receiving Status message anytime from the p2p completion, irrespectful from sedning Status from our side
            CheckProtocolInitTimeout().ContinueWith(x =>
            {
                if (x.IsFaulted && Logger.IsError)
                {
                    Logger.Error("Error during eth62Protocol handler timeout logic", x.Exception);
                }
            });
        }

        public virtual void HandleMessage(Packet message)
        {
            if (Logger.IsTrace)
            {
                Logger.Trace($"{P2PSession.RemoteNodeId} {nameof(Eth62ProtocolHandler)} handling a message with code {message.PacketType}.");
            }

            if (message.PacketType != Eth62MessageCode.Status && !_statusReceived)
            {
                throw new SubprotocolException($"{P2PSession.RemoteNodeId} No {nameof(StatusMessage)} received prior to communication.");
            }

            switch (message.PacketType)
            {
                case Eth62MessageCode.Status:
                    Handle(Deserialize<StatusMessage>(message.Data));
                    break;
                case Eth62MessageCode.NewBlockHashes:
                    Handle(Deserialize<NewBlockHashesMessage>(message.Data));
                    break;
                case Eth62MessageCode.Transactions:
                    Handle(Deserialize<TransactionsMessage>(message.Data));
                    break;
                case Eth62MessageCode.GetBlockHeaders:
                    Handle(Deserialize<GetBlockHeadersMessage>(message.Data));
                    break;
                case Eth62MessageCode.BlockHeaders:
                    Handle(Deserialize<BlockHeadersMessage>(message.Data));
                    break;
                case Eth62MessageCode.GetBlockBodies:
                    Handle(Deserialize<GetBlockBodiesMessage>(message.Data));
                    break;
                case Eth62MessageCode.BlockBodies:
                    Handle(Deserialize<BlockBodiesMessage>(message.Data));
                    break;
                case Eth62MessageCode.NewBlock:
                    Handle(Deserialize<NewBlockMessage>(message.Data));
                    break;
            }
        }

        public void Close()
        {
            _headersRequests.CompleteAdding();
            _bodiesRequests.CompleteAdding();
        }

        public void Disconnect(DisconnectReason disconnectReason)
        {
        }

        public void SendNewBlock(Block block)
        {
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
            if (_statusReceived)
            {
                throw new SubprotocolException($"{nameof(StatusMessage)} has already been received in the past");
            }

            _statusReceived = true;
            if (Logger.IsTrace)
                Logger.Trace($"{P2PSession.RemoteNodeId} ETH received status with" +
                             Environment.NewLine + $" prot version\t{status.ProtocolVersion}" +
                             Environment.NewLine + $" network ID\t{status.ChainId}," +
                             Environment.NewLine + $" genesis hash\t{status.GenesisHash}," +
                             Environment.NewLine + $" best hash\t{status.BestHash}," +
                             Environment.NewLine + $" difficulty\t{status.TotalDifficulty}");

            _remoteHeadBlockHash = status.BestHash;
            _remoteHeadBlockDifficulty = status.TotalDifficulty;

            //if (!_statusSent)
            //{
            //    throw new InvalidOperationException($"Received status from {P2PSession.RemoteNodeId} before calling Init");
            //}

            ReceivedProtocolInitMsg(status);

            var eventArgs = new EthProtocolInitializedEventArgs(this)
            {
                ChainId = status.ChainId,
                BestHash = status.BestHash,
                GenesisHash = status.GenesisHash,
                Protocol = status.Protocol,
                ProtocolVersion = status.ProtocolVersion,
                TotalDifficulty = status.TotalDifficulty
            };

            ProtocolInitialized?.Invoke(this, eventArgs);
        }

        private void Handle(TransactionsMessage msg)
        {
            for (int i = 0; i < msg.Transactions.Length; i++)
            {
                var transaction = msg.Transactions[i];
                transaction.DeliveredBy = NodeId.PublicKey;
                transaction.Timestamp = _timestamp.EpochSeconds;
                SyncManager.AddNewTransaction(transaction, NodeId);
                _transactionPool.AddTransaction(transaction, _blockTree.Head.Number);
            }
        }

        private void Handle(GetBlockBodiesMessage request)
        {
            Keccak[] hashes = request.BlockHashes;
            Block[] blocks = new Block[hashes.Length];

            for (int i = 0; i < hashes.Length; i++)
            {
                blocks[i] = SyncManager.Find(hashes[i]);
            }

            Send(new BlockBodiesMessage(blocks));
        }

        private void Handle(GetBlockHeadersMessage getBlockHeadersMessage)
        {
            if (Logger.IsTrace)
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
                startingHash = SyncManager.Find(getBlockHeadersMessage.StartingBlockNumber)?.Hash;
            }

            Block[] blocks =
                startingHash == null
                    ? new Block[0]
                    : SyncManager.Find(startingHash, (int) getBlockHeadersMessage.MaxHeaders, (int) getBlockHeadersMessage.Skip, getBlockHeadersMessage.Reverse == 1);

            BlockHeader[] headers = new BlockHeader[blocks.Length];
            for (int i = 0; i < blocks.Length; i++)
            {
                headers[i] = blocks[i]?.Header;
            }

            Send(new BlockHeadersMessage(headers));
        }

        [Todo(Improve.MissingFunctionality, "Need to compare response")]
        private bool IsRequestMatched(
            Request<GetBlockHeadersMessage, BlockHeader[]> request,
            BlockHeadersMessage response)
        {
            return response.PacketType == Eth62MessageCode.BlockHeaders; // TODO: more detailed
        }

        [Todo(Improve.MissingFunctionality, "Need to compare response")]
        private bool IsRequestMatched(
            Request<GetBlockBodiesMessage, Block[]> request,
            BlockBodiesMessage response)
        {
            return response.PacketType == Eth62MessageCode.BlockBodies; // TODO: more detailed
        }

        private void Handle(BlockBodiesMessage message)
        {
            List<Block> blocks = new List<Block>();
            foreach (BlockBody body in message.Bodies)
            {
                // TODO: match with headers
                Block block = new Block(null, body.Transactions, body.Ommers);
                blocks.Add(block);
            }

            var request = _bodiesRequests.Take();
            if (IsRequestMatched(request, message))
            {
                request.CompletionSource.SetResult(blocks.ToArray());
            }
        }

        private void Handle(BlockHeadersMessage message)
        {
            var request = _headersRequests.Take();
            if (IsRequestMatched(request, message))
            {
                request.CompletionSource.SetResult(message.BlockHeaders);
            }
        }

        private void Handle(NewBlockHashesMessage newBlockHashes)
        {
            foreach ((Keccak Hash, UInt256 Number) hint in newBlockHashes.BlockHashes)
            {
                SyncManager.HintBlock(hint.Hash, hint.Number, NodeId);
            }
        }

        private void Handle(NewBlockMessage newBlock)
        {
            SyncManager.AddNewBlock(newBlock.Block, P2PSession.RemoteNodeId);
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
                    P2PSession?.NodeStats.AddLatencyCaptureEvent(NodeLatencyStatType.BlockHeaders, latency.Value);
                }
                return task.Result;
            }

            throw new TimeoutException($"{P2PSession.RemoteNodeId} Request timeout in {nameof(GetBlockHeadersMessage)}");
        }

        [Todo(Improve.Refactor, "Generic approach to requests")]
        private async Task<Block[]> SendRequest(GetBlockBodiesMessage message, CancellationToken token)
        {
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
                    P2PSession?.NodeStats.AddLatencyCaptureEvent(NodeLatencyStatType.BlockBodies, latency.Value);
                }
                return task.Result;
            }

            throw new TimeoutException($"{P2PSession.RemoteNodeId} Request timeout in {nameof(GetBlockBodiesMessage)}");
        }

        async Task<BlockHeader[]> ISynchronizationPeer.GetBlockHeaders(Keccak blockHash, int maxBlocks, int skip, CancellationToken token)
        {
            var msg = new GetBlockHeadersMessage();
            msg.MaxHeaders = maxBlocks;
            msg.Reverse = 0;
            msg.Skip = skip;
            msg.StartingBlockHash = blockHash;

            BlockHeader[] headers = await SendRequest(msg, token);
            return headers;
        }

        async Task<BlockHeader[]> ISynchronizationPeer.GetBlockHeaders(UInt256 number, int maxBlocks, int skip, CancellationToken token)
        {
            var msg = new GetBlockHeadersMessage();
            msg.MaxHeaders = maxBlocks;
            msg.Reverse = 0;
            msg.Skip = skip;
            msg.StartingBlockNumber = number;

            BlockHeader[] headers = await SendRequest(msg, token);
            return headers;
        }

        async Task<Block[]> ISynchronizationPeer.GetBlocks(Keccak[] blockHashes, CancellationToken token)
        {
            var bodiesMsg = new GetBlockBodiesMessage(blockHashes.ToArray());

            Block[] blocks = await SendRequest(bodiesMsg, token);
            return blocks;
        }

        Task<Keccak> ISynchronizationPeer.GetHeadBlockHash(CancellationToken token)
        {
            return Task.FromResult(_remoteHeadBlockHash);
        }

        async Task<UInt256> ISynchronizationPeer.GetHeadBlockNumber(CancellationToken token)
        {
            var msg = new GetBlockHeadersMessage();
            msg.StartingBlockHash = _remoteHeadBlockHash;
            msg.MaxHeaders = 1;
            msg.Reverse = 0;
            msg.Skip = 0;

            BlockHeader[] headers = await SendRequest(msg, token);
            return headers[0]?.Number ?? 0;
        }
        
        async Task<UInt256> ISynchronizationPeer.GetHeadDifficulty(CancellationToken token)
        {
            var msg = new GetBlockHeadersMessage();
            msg.StartingBlockHash = _remoteHeadBlockHash;
            msg.MaxHeaders = 1;
            msg.Reverse = 0;
            msg.Skip = 0;

            BlockHeader[] headers = await SendRequest(msg, token);
            return headers[0]?.Difficulty ?? 0;
        }
    }
}