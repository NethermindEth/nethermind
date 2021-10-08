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
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62
{
    public class Eth62ProtocolHandler : SyncPeerProtocolHandlerBase, IZeroProtocolHandler
    {
        private bool _statusReceived;
        private readonly TxFloodController _floodController;
        protected readonly ITxPool _txPool;
        private readonly IGossipPolicy _gossipPolicy;

        public Eth62ProtocolHandler(
            ISession session,
            IMessageSerializationService serializer,
            INodeStatsManager statsManager,
            ISyncServer syncServer,
            ITxPool txPool,
            IGossipPolicy poSSwitcher,
            ILogManager logManager) : base(session, serializer, statsManager, syncServer, logManager)
        {
            _floodController = new TxFloodController(this, Timestamper.Default, Logger);
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _gossipPolicy = poSSwitcher ?? throw new ArgumentNullException(nameof(poSSwitcher));
        }

        public void DisableTxFiltering()
        {
            _floodController.IsEnabled = false;
        }

        public override byte ProtocolVersion => 62;
        public override string ProtocolCode => Protocol.Eth;
        public override int MessageIdSpaceSize => 8;
        public override string Name => "eth62";
        protected override TimeSpan InitTimeout => Timeouts.Eth62Status;
        private bool HasEverBeenInPos { get; set; }

        public override event EventHandler<ProtocolInitializedEventArgs>? ProtocolInitialized;

        public override event EventHandler<ProtocolEventArgs>? SubprotocolRequested
        {
            add { }
            remove { }
        }

        protected virtual void EnrichStatusMessage(StatusMessage statusMessage) { }

        public override void Init()
        {
            if (Logger.IsTrace) Logger.Trace($"{Name} subprotocol initializing with {Node:c}");

            if (SyncServer.Head == null)
            {
                throw new InvalidOperationException($"Cannot initialize {Name} without the head block set");
            }

            BlockHeader head = SyncServer.Head;
            StatusMessage statusMessage = new();
            statusMessage.ChainId = SyncServer.ChainId;
            statusMessage.ProtocolVersion = ProtocolVersion;
            statusMessage.TotalDifficulty = head.TotalDifficulty ?? head.Difficulty;
            statusMessage.BestHash = head.Hash!;
            statusMessage.GenesisHash = SyncServer.Genesis.Hash!;
            EnrichStatusMessage(statusMessage);

            Metrics.StatusesSent++;
            Send(statusMessage);

            CheckProtocolInitTimeout().ContinueWith(x =>
            {
                if (x.IsFaulted && Logger.IsError)
                {
                    Logger.Error($"Error during {Name} handler timeout logic", x.Exception);
                }
            });
        }

        public override void HandleMessage(ZeroPacket message)
        {
            int packetType = message.PacketType;
            if (!_statusReceived && packetType != Eth62MessageCode.Status)
            {
                throw new SubprotocolException(
                    $"No {nameof(StatusMessage)} received prior to communication with {Node:c}.");
            }

            int size = message.Content.ReadableBytes;
            if (Logger.IsTrace)
                Logger.Trace(
                    $"{Counter:D5} {Eth62MessageCode.GetDescription(packetType)} from {Node:c}");

            switch (packetType)
            {
                case Eth62MessageCode.Status:
                    StatusMessage statusMsg = Deserialize<StatusMessage>(message.Content);
                    ReportIn(statusMsg);
                    Handle(statusMsg);
                    break;
                case Eth62MessageCode.NewBlockHashes:
                    Metrics.Eth62NewBlockHashesReceived++;
                    if (ShouldNotGossip)
                    {
                        Disconnect(DisconnectReason.BreachOfProtocol,
                            "NewBlockHashes message received while PoS protocol activated.");
                    }
                    else
                    {
                        NewBlockHashesMessage newBlockHashesMessage =
                            Deserialize<NewBlockHashesMessage>(message.Content);
                        ReportIn(newBlockHashesMessage);
                        Handle(newBlockHashesMessage);
                    }

                    break;
                case Eth62MessageCode.Transactions:
                    Metrics.Eth62TransactionsReceived++;
                    if (_floodController.IsAllowed())
                    {
                        TransactionsMessage txMsg = Deserialize<TransactionsMessage>(message.Content);
                        ReportIn(txMsg);
                        Handle(txMsg);
                    }

                    break;
                case Eth62MessageCode.GetBlockHeaders:
                    GetBlockHeadersMessage getBlockHeadersMessage
                        = Deserialize<GetBlockHeadersMessage>(message.Content);
                    ReportIn(getBlockHeadersMessage);
                    Handle(getBlockHeadersMessage);
                    break;
                case Eth62MessageCode.BlockHeaders:
                    BlockHeadersMessage headersMsg = Deserialize<BlockHeadersMessage>(message.Content);
                    ReportIn(headersMsg);
                    Handle(headersMsg, size);
                    break;
                case Eth62MessageCode.GetBlockBodies:
                    GetBlockBodiesMessage getBodiesMsg = Deserialize<GetBlockBodiesMessage>(message.Content);
                    ReportIn(getBodiesMsg);
                    Handle(getBodiesMsg);
                    break;
                case Eth62MessageCode.BlockBodies:
                    BlockBodiesMessage bodiesMsg = Deserialize<BlockBodiesMessage>(message.Content);
                    ReportIn(bodiesMsg);
                    HandleBodies(bodiesMsg, size);
                    break;
                case Eth62MessageCode.NewBlock:
                    Metrics.Eth62NewBlockReceived++;
                    if (ShouldNotGossip)
                    {
                        Disconnect(DisconnectReason.BreachOfProtocol,
                            "NewBlock message received while PoS protocol activated.");
                    }
                    else
                    {
                        NewBlockMessage newBlockMsg = Deserialize<NewBlockMessage>(message.Content);
                        ReportIn(newBlockMsg);
                        Handle(newBlockMsg);
                    }

                    break;
            }
        }

        private bool ShouldNotGossip
        {
            get
            {
                if (_gossipPolicy.ShouldGossipBlocks)
                {
                    SyncServer.StopNotifyingPeersAboutNewBlocks();
                    return true;
                }

                return false;
            }
        }

        private void Handle(StatusMessage status)
        {
            Metrics.StatusesReceived++;
            if (_statusReceived)
            {
                throw new SubprotocolException($"{nameof(StatusMessage)} has already been received in the past");
            }

            _statusReceived = true;
            _remoteHeadBlockHash = status.BestHash;

            ReceivedProtocolInitMsg(status);

            SyncPeerProtocolInitializedEventArgs eventArgs = new(this)
            {
                ChainId = (ulong)status.ChainId,
                BestHash = status.BestHash,
                GenesisHash = status.GenesisHash,
                Protocol = status.Protocol,
                ProtocolVersion = status.ProtocolVersion,
                TotalDifficulty = status.TotalDifficulty
            };

            Session.IsNetworkIdMatched = SyncServer.ChainId == (ulong)status.ChainId;
            HeadHash = status.BestHash;
            TotalDifficulty = status.TotalDifficulty;
            ProtocolInitialized?.Invoke(this, eventArgs);
        }

        protected void Handle(TransactionsMessage msg)
        {
            IList<Transaction> transactions = msg.Transactions;
            for (int i = 0; i < transactions.Count; i++)
            {
                Transaction tx = transactions[i];
                tx.DeliveredBy = Node.Id;
                tx.Timestamp = _timestamper.UnixTime.Seconds;
                AddTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.None);
                _floodController.Report(result == AddTxResult.Added);

                if (Logger.IsTrace)
                    Logger.Trace(
                        $"{Node:c} sent {tx.Hash} tx and it was {result} (chain ID = {tx.Signature?.ChainId})");
            }
        }

        private void Handle(NewBlockHashesMessage newBlockHashes)
        {
            (Keccak, long)[] blockHashes = newBlockHashes.BlockHashes;
            for (int i = 0; i < blockHashes.Length; i++)
            {
                (Keccak hash, long number) = blockHashes[i];
                SyncServer.HintBlock(hash, number, this);
            }
        }

        private void Handle(NewBlockMessage msg)
        {
            try
            {
                msg.Block.Header.TotalDifficulty = msg.TotalDifficulty;
                SyncServer.AddNewBlock(msg.Block, this);
            }
            catch (Exception e)
            {
                if (Logger.IsDebug) Logger.Debug($"Handling {msg} from {Node:c} failed: " + e.Message);
                throw;
            }
        }

        public override void NotifyOfNewBlock(Block block, SendBlockPriority priority)
        {
            if (ShouldNotGossip)
            {
                return;
            }

            switch (priority)
            {
                case SendBlockPriority.High:
                    SendNewBlock(block);
                    break;
                case SendBlockPriority.Low:
                    HintNewBlock(block.Hash, block.Number);
                    break;
                default:
                    Logger.Error(
                        $"Unknown priority ({priority}) passed to {nameof(NotifyOfNewBlock)} - handling as low priority");
                    HintNewBlock(block.Hash, block.Number);
                    break;
            }
        }

        private void SendNewBlock(Block block)
        {
            if (!block.TotalDifficulty.HasValue)
            {
                throw new InvalidOperationException($"Trying to send a block {block.Hash} with null total difficulty");
            }

            if (Logger.IsTrace) Logger.Trace($"OUT {Counter:D5} NewBlock to {Node:c}");

            NewBlockMessage msg = new();
            msg.Block = block;
            msg.TotalDifficulty = block.TotalDifficulty.Value;

            Send(msg);
        }

        private void HintNewBlock(Keccak blockHash, long number)
        {
            if (Logger.IsTrace) Logger.Trace($"OUT {Counter:D5} HintBlock to {Node:c}");

            NewBlockHashesMessage msg = new((blockHash, number));
            Send(msg);
        }

        protected override void OnDisposed()
        {
        }
    }
}
