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
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.Rlpx;

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public class Eth62ProtocolHandler : ProtocolHandlerBase, IProtocolHandler, ISynchronizationPeer
    {
        private readonly ISynchronizationManager _sync;

        public Eth62ProtocolHandler(
            IP2PSession p2PSession,
            IMessageSerializationService serializer,
            ISynchronizationManager sync,
            ILogger logger)
            : base(p2PSession, serializer, logger)
        {
            _sync = sync;
        }

        private bool _statusSent;

        private bool _statusReceived;

        public virtual byte ProtocolVersion => 62;

        public string ProtocolCode => "eth";

        public virtual int MessageIdSpaceSize => 8;

        public void Init()
        {
            Logger.Info($"{P2PSession.RemoteNodeId} {ProtocolCode} v{ProtocolVersion} subprotocol initializing");
            if (_sync.Head == null)
            {
                throw new InvalidOperationException("Initializing sync protocol without the head block set");
            }

            BlockHeader head = _sync.Head;
            StatusMessage statusMessage = new StatusMessage();
            statusMessage.ChainId = _sync.BlockTree.ChainId;
            statusMessage.ProtocolVersion = ProtocolVersion;
            statusMessage.TotalDifficulty = head.Difficulty;
            statusMessage.BestHash = head.Hash;
            statusMessage.GenesisHash = _sync.Genesis.Hash;

            _statusSent = true;
            Send(statusMessage);
        }

        private static readonly Dictionary<int, Type> MessageTypes = new Dictionary<int, Type>
        {
            {Eth62MessageCode.Status, typeof(StatusMessage)},
            {Eth62MessageCode.NewBlockHashes, typeof(NewBlockHashesMessage)},
            {Eth62MessageCode.Transactions, typeof(TransactionsMessage)},
            {Eth62MessageCode.GetBlockHeaders, typeof(GetBlockHeadersMessage)},
            {Eth62MessageCode.BlockHeaders, typeof(BlockHeadersMessage)},
            {Eth62MessageCode.GetBlockBodies, typeof(GetBlockBodiesMessage)},
            {Eth62MessageCode.BlockBodies, typeof(BlockBodiesMessage)},
            {Eth62MessageCode.NewBlock, typeof(NewBlockMessage)},
        };

        public virtual Type ResolveMessageType(int messageCode)
        {
            return MessageTypes[messageCode];
        }

        public virtual void HandleMessage(Packet message)
        {
            try
            {
                if (Logger.IsDebugEnabled)
                {
                    Logger.Debug($"{P2PSession.RemoteNodeId} {nameof(Eth62ProtocolHandler)} handling a message with code {message.PacketType}.");
                }

                if (message.PacketType != Eth62MessageCode.Status && !_statusReceived)
                {
                    Diagnostics.TestExceptionHere("HandleMessage no status", Logger);
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
            catch (Exception e)
            {
                Logger.Error($"{P2PSession.RemoteNodeId} TEMP Investigating exception propagation", e);
                throw;
            }
        }

        private void Handle(StatusMessage status)
        {
            if (_statusReceived)
            {
                throw new SubprotocolException($"{nameof(StatusMessage)} has already been received in the past");
            }

            _statusReceived = true;
            Logger.Info($"{P2PSession.RemoteNodeId} ETH received status with" +
                        Environment.NewLine + $" prot version\t{status.ProtocolVersion}" +
                        Environment.NewLine + $" network ID\t{status.ChainId}," +
                        Environment.NewLine + $" genesis hash\t{status.GenesisHash}," +
                        Environment.NewLine + $" best hash\t{status.BestHash}," +
                        Environment.NewLine + $" difficulty\t{status.TotalDifficulty}");

            _remoteHeadBlockHash = status.BestHash;
            _remoteHeadBlockDifficulty = status.TotalDifficulty;

            if (status.GenesisHash != _sync.Genesis.Hash)
            {
                Logger.Warn($"{P2PSession.RemoteNodeId} Connected peer's genesis hash {status.GenesisHash} differes from {_sync.Genesis.Hash}");
                throw new InvalidOperationException("genesis hash mismatch");
            }

            if (!_statusSent)
            {
                throw new InvalidOperationException($"Received status from {P2PSession.RemoteNodeId} before calling Init");
            }
            
            ProtocolInitialized?.Invoke(this, EventArgs.Empty);
        }

        private void Handle(TransactionsMessage msg)
        {
            for (int i = 0; i < msg.Transactions.Length; i++)
            {
                _sync.AddNewTransaction(msg.Transactions[i], NodeId);
            }
        }

        private void Handle(GetBlockBodiesMessage request)
        {
            Keccak[] hashes = request.BlockHashes;
            Block[] blocks = new Block[hashes.Length];

            for (int i = 0; i < hashes.Length; i++)
            {
                blocks[i] = _sync.Find(hashes[i]);
            }

            Send(new BlockBodiesMessage(blocks));
        }

        private void Handle(GetBlockHeadersMessage getBlockHeadersMessage)
        {
            if (Logger.IsTraceEnabled)
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
                startingHash = _sync.Find(getBlockHeadersMessage.StartingBlockNumber).Hash;
            }

            Block[] blocks = _sync.Find(startingHash, (int)getBlockHeadersMessage.MaxHeaders, (int)getBlockHeadersMessage.Skip, getBlockHeadersMessage.Reverse == 1);
//            HeaderValidator validator = new HeaderValidator(new DifficultyCalculator(RopstenSpecProvider.Instance), _sync.BlockTree, new EthashSealEngine(new Ethash(), NullLogger.Instance), RopstenSpecProvider.Instance, NullLogger.Instance);
//            foreach (Block block in blocks)
//            {
//                bool validated = validator.Validate(block.Header);
//                if (!validated)
//                {
//                    throw new Exception("Sending invalid");
//                }
//            }
            
            BlockHeader[] headers = new BlockHeader[blocks.Length];
            for (int i = 0; i < blocks.Length; i++)
            {
                headers[i] = blocks[i].Header;
            }

            Send(new BlockHeadersMessage(headers));
        }

        private bool IsRequestMatched(
            Request<GetBlockHeadersMessage, BlockHeader[]> request,
            BlockHeadersMessage response)
        {
            return response.PacketType == Eth62MessageCode.BlockHeaders; // TODO: more detailed
        }

        private bool IsRequestMatched(
            Request<GetBlockBodiesMessage, Block[]> request,
            BlockBodiesMessage response)
        {
            return response.PacketType == Eth62MessageCode.BlockBodies; // TODO: more detailed
        }

        private void Handle(BlockBodiesMessage message)
        {
            List<Block> blocks = new List<Block>();
            foreach ((Transaction[] Transactions, BlockHeader[] Ommers) body in message.Bodies)
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
            foreach ((Keccak Hash, BigInteger Number) hint in newBlockHashes.BlockHashes)
            {
                _sync.HintBlock(hint.Hash, hint.Number, NodeId);
            }
        }

        private void Handle(NewBlockMessage newBlock)
        {
            _sync.AddNewBlock(newBlock.Block, P2PSession.RemoteNodeId);
        }

        public void Close()
        {
            _headersRequests.CompleteAdding();
            _bodiesRequests.CompleteAdding();
        }

        public void Disconnect(DisconnectReason disconnectReason)
        {
        }

        public event EventHandler ProtocolInitialized;
        public event EventHandler<ProtocolEventArgs> SubprotocolRequested;

        private readonly BlockingCollection<Request<GetBlockHeadersMessage, BlockHeader[]>> _headersRequests
            = new BlockingCollection<Request<GetBlockHeadersMessage, BlockHeader[]>>();

        private readonly BlockingCollection<Request<GetBlockBodiesMessage, Block[]>> _bodiesRequests
            = new BlockingCollection<Request<GetBlockBodiesMessage, Block[]>>();

        private class Request<TMsg, TResult>
        {
            public Request(TMsg message)
            {
                CompletionSource = new TaskCompletionSource<TResult>();
                Message = message;
            }

            public TMsg Message { get; set; }
            public TaskCompletionSource<TResult> CompletionSource { get; }
        }

        private async Task<BlockHeader[]> SendRequest(GetBlockHeadersMessage message)
        {
            if (Logger.IsTraceEnabled)
            {
                Logger.Trace("Sending headers request:");
                Logger.Trace($"Starting blockhash: {message.StartingBlockHash}");
                Logger.Trace($"Starting number: {message.StartingBlockNumber}");
                Logger.Trace($"Skip: {message.Skip}");
                Logger.Trace($"Reverse: {message.Reverse}");
                Logger.Trace($"Max headers: {message.MaxHeaders}");
            }

            var request = new Request<GetBlockHeadersMessage, BlockHeader[]>(message);
            _headersRequests.Add(request);
            Send(request.Message);
            Task<BlockHeader[]> task = request.CompletionSource.Task;
            if (await Task.WhenAny(task, Task.Delay(Timeouts.Eth62)) == task)
            {
                return task.Result;
            }

            // TODO: work in progress
            throw new TimeoutException($"{P2PSession.RemoteNodeId} Request timeout in {nameof(GetBlockHeadersMessage)}");
        }

        private async Task<Block[]> SendRequest(GetBlockBodiesMessage message)
        {
            if (Logger.IsTraceEnabled)
            {
                Logger.Trace("Sending bodies request:");
                Logger.Trace($"Blockhashes count: {message.BlockHashes.Length}");
            }

            var request = new Request<GetBlockBodiesMessage, Block[]>(message);
            _bodiesRequests.Add(request);
            Send(request.Message);

            Task<Block[]> task = request.CompletionSource.Task;
            if (await Task.WhenAny(task, Task.Delay(Timeouts.Eth62)) == task)
            {
                return task.Result;
            }

            // TODO: work in progress
            throw new TimeoutException($"{P2PSession.RemoteNodeId} Request timeout in {nameof(GetBlockBodiesMessage)}");
        }

        async Task<BlockHeader[]> ISynchronizationPeer.GetBlockHeaders(Keccak blockHash, int maxBlocks, int skip)
        {
            var msg = new GetBlockHeadersMessage();
            msg.MaxHeaders = maxBlocks;
            msg.Reverse = 0;
            msg.Skip = skip;
            msg.StartingBlockHash = blockHash;

            BlockHeader[] headers = await SendRequest(msg);
            return headers;
        }

        async Task<BlockHeader[]> ISynchronizationPeer.GetBlockHeaders(BigInteger number, int maxBlocks, int skip)
        {
            var msg = new GetBlockHeadersMessage();
            msg.MaxHeaders = maxBlocks;
            msg.Reverse = 0;
            msg.Skip = skip;
            msg.StartingBlockNumber = number;

            BlockHeader[] headers = await SendRequest(msg);
            return headers;
        }

        public PublicKey NodeId => P2PSession.RemoteNodeId;

        async Task<Block[]> ISynchronizationPeer.GetBlocks(Keccak[] blockHashes)
        {
            var bodiesMsg = new GetBlockBodiesMessage(blockHashes.ToArray());

            Block[] blocks = await SendRequest(bodiesMsg);
            return blocks;
        }

        Task<Keccak> ISynchronizationPeer.GetHeadBlockHash()
        {
            return Task.FromResult(_remoteHeadBlockHash);
        }

        async Task<BigInteger> ISynchronizationPeer.GetHeadBlockNumber()
        {
            var msg = new GetBlockHeadersMessage();
            msg.StartingBlockHash = _remoteHeadBlockHash;
            msg.MaxHeaders = 1;
            msg.Reverse = 0;
            msg.Skip = 0;

            BlockHeader[] headers = await SendRequest(msg);
            return headers[0]?.Number ?? 0;
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

        public Task Disconnect()
        {
            throw new NotImplementedException();
        }

        private Keccak _remoteHeadBlockHash;
        private BigInteger _remoteHeadBlockDifficulty;
    }
}