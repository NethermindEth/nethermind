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
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using DotNetty.Common.Concurrency;
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

        public virtual int MessageIdSpaceSize => 7;

        public void Init()
        {
            Logger.Info($"{ProtocolCode} v{ProtocolVersion} subprotocol initializing");
            
            Block headBlock = _sync.BlockTree.HeadBlock;
            StatusMessage statusMessage = new StatusMessage();
            statusMessage.ChainId = _sync.BlockTree.ChainId;
            statusMessage.ProtocolVersion = ProtocolVersion;
            statusMessage.TotalDifficulty = headBlock.Difficulty;
            statusMessage.BestHash = headBlock.Hash;
            statusMessage.GenesisHash = _sync.BlockTree.GenesisHash;
            
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
            Logger.Info($"{nameof(Eth62ProtocolHandler)} handling a message with code {message.PacketType}.");

            if (message.PacketType != Eth62MessageCode.Status && !_statusReceived)
            {
                throw new SubprotocolException($"No {nameof(StatusMessage)} received prior to communication.");
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

        private void Handle(StatusMessage status)
        {
            if (_statusReceived)
            {
                throw new SubprotocolException($"{nameof(StatusMessage)} has already been received in the past");
            }

            _statusReceived = true;
            Logger.Info("ETH received status with" +
                       Environment.NewLine + $" prot version\t{status.ProtocolVersion}" +
                       Environment.NewLine + $" network ID\t{status.ChainId}," +
                       Environment.NewLine + $" genesis hash\t{status.GenesisHash}," +
                       Environment.NewLine + $" best hash\t{status.BestHash}," +
                       Environment.NewLine + $" difficulty\t{status.TotalDifficulty}");

            _remoteHeadBlockHash = status.BestHash;
            _remoteHeadBlockDifficulty = status.TotalDifficulty;

            Debug.Assert(_statusSent, "Expecting Init() to have been called by this point");
            ProtocolInitialized?.Invoke(this, EventArgs.Empty);
        }

        private void Handle(TransactionsMessage transactionsMessage)
        {
            throw new NotImplementedException();
//            for (int txIndex = 0; txIndex < transactionsMessage.Transactions.Length; txIndex++)
//            {
//                Transaction transaction = transactionsMessage.Transactions[txIndex];
//                TransactionInfo info = _sync.Add(transaction, P2PSession.RemoteNodeId);
//                if (info.Quality == Quality.Invalid) // TODO: processed invalid should not be penalized here
//                {
//                    Logger.Debug($"Received an invalid transaction from {P2PSession.RemoteNodeId}");
//                    throw new SubprotocolException($"Peer sent an invalid transaction {transaction.Hash}");
//                }
//            }
        }

        private void Handle(GetBlockBodiesMessage request)
        {
            Keccak[] hashes = request.BlockHashes;
            Block[] blocks = new Block[hashes.Length];
            
            for (int i = 0; i < hashes.Length; i++)
            {
                BlockInfo blockInfo = _sync.Find(hashes[i]);
                if (blockInfo != null)
                {
                    if (blockInfo.BodyLocation == BlockDataLocation.Store)
                    {
                        throw new NotImplementedException();
                    }

                    if (blockInfo.BodyLocation == BlockDataLocation.Memory)
                    {
                        blocks[i] = blockInfo.Block;
                    }
                    else
                    {
                        throw new NotImplementedException();
                    }
                }
            }

            Send(new BlockBodiesMessage(blocks));
        }

        private void Handle(GetBlockHeadersMessage getBlockHeadersMessage)
        {
            BlockInfo[] blockInfos = _sync.Find(getBlockHeadersMessage.StartingBlockHash, (int)getBlockHeadersMessage.MaxHeaders, (int)getBlockHeadersMessage.Skip, getBlockHeadersMessage.Reverse == 1);
            BlockHeader[] blockHeaders = new BlockHeader[blockInfos.Length];
            for (int i = 0; i < blockInfos.Length; i++)
            {
                if (blockInfos[i] != null)
                {
                    if (blockInfos[i].HeaderLocation == BlockDataLocation.Store)
                    {
                        throw new NotImplementedException();
                    }

                    if (blockInfos[i].HeaderLocation == BlockDataLocation.Memory)
                    {
                        blockHeaders[i] = blockInfos[i].BlockHeader;
                    }
                    else
                    {
                        throw new NotImplementedException();
                    }
                }
            }

            Send(new BlockHeadersMessage(blockHeaders));
        }

        private bool IsRequestMatched(
            Request<GetBlockHeadersMessage, BlockHeader[]> request,
            BlockHeadersMessage response)
        {
            return response.PacketType == Eth62MessageCode.GetBlockHeaders; // TODO: more detailed
        }
        
        private bool IsRequestMatched(
            Request<GetBlockBodiesMessage, Block[]> request,
            BlockBodiesMessage response)
        {
            return response.PacketType == Eth62MessageCode.GetBlockBodies; // TODO: more detailed
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
                _sync.HintBlock(hint.Hash, hint.Number);
            }
        }

        private void Handle(NewBlockMessage newBlock)
        {
            // TODO: use total difficulty here
            // TODO: can drop connection if processing of the block fails (not only validation?)
            BlockInfo blockInfo = _sync.AddBlock(newBlock.Block, P2PSession.RemoteNodeId);
            if (blockInfo.BlockQuality == Quality.Invalid)
            {
                throw new SubprotocolException($"Peer sent an invalid new block {newBlock.Block.Hash}");
            }
        }

        public void Close()
        {
            _headersRequests.CompleteAdding();
            _bodiesRequests.CompleteAdding();
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
            var request = new Request<GetBlockHeadersMessage, BlockHeader[]>(message);
            _headersRequests.Add(request);
            return await request.CompletionSource.Task;
        }
        
        private async Task<Block[]> SendRequest(GetBlockBodiesMessage message)
        {
            var request = new Request<GetBlockBodiesMessage, Block[]>(message);
            _bodiesRequests.Add(request);
            return await request.CompletionSource.Task;
        }

        async Task<Block[]> ISynchronizationPeer.GetBlocks(Keccak blockHash, BigInteger maxBlocks)
        {
            var msg = new GetBlockHeadersMessage();
            msg.MaxHeaders = maxBlocks;
            msg.Reverse = 0;
            msg.Skip = 0;
            msg.StartingBlockHash = blockHash;

            
            BlockHeader[] headers = await SendRequest(msg);
            List<Keccak> hashes = new List<Keccak>();
            for (int i = 0; i < headers.Length; i++)
            {
                BlockInfo info = _sync.AddBlockHeader(headers[i]);
                if (info.HeaderQuality == Quality.DataValid)
                {
                    hashes.Add(info.Hash);
                }
                else
                {
                    throw new NotImplementedException();
                }
            }
            
            var bodiesMsg = new GetBlockBodiesMessage(hashes.ToArray());
            Block[] blocks = await SendRequest(bodiesMsg);
            for (int i = 0; i < blocks.Length; i++)
            {
                _sync.AddBlock(blocks[i], P2PSession.RemoteNodeId);
            }

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

        public event EventHandler<BlockEventArgs> NewBlock;
        public event EventHandler<KeccakEventArgs> NewBlockHash;

        private Keccak _remoteHeadBlockHash;
        private BigInteger _remoteHeadBlockDifficulty;
    }
}