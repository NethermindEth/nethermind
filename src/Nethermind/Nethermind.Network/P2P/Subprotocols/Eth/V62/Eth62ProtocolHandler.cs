// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.TxPool;
using ShouldGossip = Nethermind.Consensus.ShouldGossip;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62
{
    public class Eth62ProtocolHandler : SyncPeerProtocolHandlerBase, IZeroProtocolHandler
    {
        private bool _statusReceived;
        private readonly TxFloodController _floodController;
        protected readonly ITxPool _txPool;
        private readonly IGossipPolicy _gossipPolicy;
        private readonly ITxGossipPolicy _txGossipPolicy;
        private readonly LruKeyCache<Keccak> _lastBlockNotificationCache = new(10, "LastBlockNotificationCache");

        public Eth62ProtocolHandler(ISession session,
            IMessageSerializationService serializer,
            INodeStatsManager statsManager,
            ISyncServer syncServer,
            ITxPool txPool,
            IGossipPolicy gossipPolicy,
            INetworkConfig networkConfig,
            ILogManager logManager,
            ITxGossipPolicy? transactionsGossipPolicy = null)
            : base(session, serializer, statsManager, syncServer, networkConfig, logManager)
        {
            _floodController = new TxFloodController(this, Timestamper.Default, Logger);
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _gossipPolicy = gossipPolicy ?? throw new ArgumentNullException(nameof(gossipPolicy));
            _txGossipPolicy = transactionsGossipPolicy ?? TxPool.ShouldGossip.Instance;

            EnsureGossipPolicy();
        }

        public void DisableTxFiltering()
        {
            _floodController.IsEnabled = false;
        }

        public override byte ProtocolVersion => EthVersions.Eth62;
        public override string ProtocolCode => Protocol.Eth;
        public override int MessageIdSpaceSize => 8;
        public override string Name => "eth62";
        protected override TimeSpan InitTimeout => Timeouts.Eth62Status;
        protected bool CanReceiveTransactions => _txGossipPolicy.ShouldListenToGossippedTransactions;

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

            if (SyncServer.Head is null)
            {
                throw new InvalidOperationException($"Cannot initialize {Name} without the head block set");
            }

            BlockHeader head = SyncServer.Head;
            StatusMessage statusMessage = new()
            {
                NetworkId = SyncServer.NetworkId,
                ProtocolVersion = ProtocolVersion,
                TotalDifficulty = head.TotalDifficulty ?? head.Difficulty,
                BestHash = head.Hash!,
                GenesisHash = SyncServer.Genesis.Hash!
            };

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
            int size = message.Content.ReadableBytes;

            bool CanAcceptBlockGossip()
            {
                if (_gossipPolicy.ShouldDisconnectGossipingNodes)
                {
                    const string postFinalized = $"NewBlock message received after FIRST_FINALIZED_BLOCK PoS block. Disconnecting Peer.";
                    ReportIn(postFinalized, size);
                    Disconnect(DisconnectReason.GossipingInPoS, postFinalized);
                    return false;
                }

                if (_gossipPolicy.ShouldDiscardBlocks)
                {
                    const string postTransition = $"NewBlock message received after TERMINAL_TOTAL_DIFFICULTY PoS block. Ignoring Message.";
                    ReportIn(postTransition, size);
                    return false;
                }

                return true;
            }

            int packetType = message.PacketType;
            if (!_statusReceived && packetType != Eth62MessageCode.Status)
            {
                throw new SubprotocolException($"No {nameof(StatusMessage)} received prior to communication with {Node:c}.");
            }

            if (Logger.IsTrace) Logger.Trace($"{Counter:D5} {Eth62MessageCode.GetDescription(packetType)} from {Node:c}");

            switch (packetType)
            {
                case Eth62MessageCode.Status:
                    StatusMessage statusMsg = Deserialize<StatusMessage>(message.Content);
                    ReportIn(statusMsg, size);
                    Handle(statusMsg);
                    break;
                case Eth62MessageCode.NewBlockHashes:
                    Metrics.Eth62NewBlockHashesReceived++;
                    if (CanAcceptBlockGossip())
                    {
                        NewBlockHashesMessage newBlockHashesMessage =
                            Deserialize<NewBlockHashesMessage>(message.Content);
                        ReportIn(newBlockHashesMessage, size);
                        Handle(newBlockHashesMessage);
                    }
                    break;
                case Eth62MessageCode.Transactions:
                    Metrics.Eth62TransactionsReceived++;
                    if (CanReceiveTransactions)
                    {
                        if (_floodController.IsAllowed())
                        {
                            TransactionsMessage txMsg = Deserialize<TransactionsMessage>(message.Content);
                            ReportIn(txMsg, size);
                            Handle(txMsg);
                        }
                        else
                        {
                            const string txFlooding = $"Ignoring {nameof(TransactionsMessage)} because of message flooding.";
                            ReportIn(txFlooding, size);
                        }
                    }
                    else
                    {
                        const string ignored = $"{nameof(TransactionsMessage)} ignored, syncing";
                        ReportIn(ignored, size);
                    }

                    break;
                case Eth62MessageCode.GetBlockHeaders:
                    GetBlockHeadersMessage getBlockHeadersMessage
                        = Deserialize<GetBlockHeadersMessage>(message.Content);
                    ReportIn(getBlockHeadersMessage, size);
                    Handle(getBlockHeadersMessage);
                    break;
                case Eth62MessageCode.BlockHeaders:
                    BlockHeadersMessage headersMsg = Deserialize<BlockHeadersMessage>(message.Content);
                    ReportIn(headersMsg, size);
                    Handle(headersMsg, size);
                    break;
                case Eth62MessageCode.GetBlockBodies:
                    GetBlockBodiesMessage getBodiesMsg = Deserialize<GetBlockBodiesMessage>(message.Content);
                    ReportIn(getBodiesMsg, size);
                    Handle(getBodiesMsg);
                    break;
                case Eth62MessageCode.BlockBodies:
                    BlockBodiesMessage bodiesMsg = Deserialize<BlockBodiesMessage>(message.Content);
                    ReportIn(bodiesMsg, size);
                    HandleBodies(bodiesMsg, size);
                    break;
                case Eth62MessageCode.NewBlock:
                    Metrics.Eth62NewBlockReceived++;
                    if (CanAcceptBlockGossip())
                    {
                        NewBlockMessage newBlockMsg = Deserialize<NewBlockMessage>(message.Content);
                        ReportIn(newBlockMsg, size);
                        Handle(newBlockMsg);
                    }
                    break;
            }
        }

        private bool CanGossip => EnsureGossipPolicy();

        private bool EnsureGossipPolicy()
        {
            if (!_gossipPolicy.CanGossipBlocks)
            {
                SyncServer.StopNotifyingPeersAboutNewBlocks();
                return false;
            }

            return true;
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
                NetworkId = (ulong)status.NetworkId,
                BestHash = status.BestHash,
                GenesisHash = status.GenesisHash,
                Protocol = status.Protocol,
                ProtocolVersion = status.ProtocolVersion,
                ForkId = status.ForkId,
                TotalDifficulty = status.TotalDifficulty
            };

            Session.IsNetworkIdMatched = SyncServer.NetworkId == (ulong)status.NetworkId;
            HeadHash = status.BestHash;
            TotalDifficulty = status.TotalDifficulty;
            ProtocolInitialized?.Invoke(this, eventArgs);
        }

        protected void Handle(TransactionsMessage msg)
        {
            IList<Transaction> iList = msg.Transactions;

            if (iList is ArrayPoolList<Transaction> apl)
            {
                HandleFast(apl.AsSpan());
            }
            else if (iList is Transaction[] array)
            {
                HandleFast(array.AsSpan());
            }
            else if (iList is List<Transaction> list)
            {
                HandleFast(CollectionsMarshal.AsSpan(list));
            }
            else
            {
                HandleSlow(iList);
            }
        }

        private void HandleFast(Span<Transaction> transactions)
        {
            bool isTrace = Logger.IsTrace;
            for (int i = 0; i < transactions.Length; i++)
            {
                PrepareAndSubmitTransaction(transactions[i], isTrace);
            }
        }

        private void HandleSlow(IList<Transaction> transactions)
        {
            bool isTrace = Logger.IsTrace;
            int count = transactions.Count;
            for (int i = 0; i < count; i++)
            {
                PrepareAndSubmitTransaction(transactions[i], isTrace);
            }
        }

        private void PrepareAndSubmitTransaction(Transaction tx, bool isTrace)
        {
            tx.Timestamp = _timestamper.UnixTime.Seconds;
            if (tx.Hash is not null)
            {
                NotifiedTransactions.Set(tx.Hash);
            }

            AcceptTxResult accepted = _txPool.SubmitTx(tx, TxHandlingOptions.None);
            _floodController.Report(accepted);

            if (isTrace) Log(tx, accepted);

            void Log(Transaction tx, in AcceptTxResult accepted)
            {
                Logger.Trace($"{Node:c} sent {tx.Hash} tx and it was {accepted} (chain ID = {tx.Signature?.ChainId})");
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

        public override void NotifyOfNewBlock(Block block, SendBlockMode mode)
        {
            if (!CanGossip || !_gossipPolicy.ShouldGossipBlock(block.Header))
            {
                return;
            }

            if (_lastBlockNotificationCache.Set(block.Hash))
            {
                switch (mode)
                {
                    case SendBlockMode.FullBlock:
                        SendNewBlock(block);
                        break;
                    case SendBlockMode.HashOnly:
                        HintNewBlock(block.Hash, block.Number);
                        break;
                    default:
                        if (Logger.IsError) Logger.Error($"Unknown mode ({mode}) passed to {nameof(NotifyOfNewBlock)} - handling as {nameof(SendBlockMode.HashOnly)} mode");
                        HintNewBlock(block.Hash, block.Number);
                        break;
                }
            }
        }

        private void SendNewBlock(Block block)
        {
            if (!block.TotalDifficulty.HasValue)
            {
                throw new InvalidOperationException($"Trying to send a block {block.Hash} with null total difficulty");
            }

            if (Logger.IsTrace) Logger.Trace($"OUT {Counter:D5} NewBlock to {Node:c}");

            NewBlockMessage msg = new() { Block = block, TotalDifficulty = block.TotalDifficulty.Value };

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
