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
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public class Eth62ProtocolHandler : SyncPeerProtocolHandlerBase, IZeroProtocolHandler, ISyncPeer, ITxPoolPeer
    {
        private System.Timers.Timer _txFloodCheckTimer;

        private bool _statusReceived;

        // private BlockHeadersMessage _eth1920000HeaderMessage;
        
        public Eth62ProtocolHandler(
            ISession session,
            IMessageSerializationService serializer,
            INodeStatsManager statsManager,
            ISyncServer syncServer,
            ILogManager logManager,
            ITxPool txPool) : base(session, serializer, statsManager, syncServer, logManager, txPool)
        {
            _txFloodCheckTimer = new System.Timers.Timer(_txFloodCheckInterval.TotalMilliseconds);
            _txFloodCheckTimer.Elapsed += CheckTxFlooding;
            _txFloodCheckTimer.Start();
        }

        private readonly TimeSpan _txFloodCheckInterval = TimeSpan.FromSeconds(60);


        private void CheckTxFlooding(object sender, ElapsedEventArgs e)
        {
            if (!_isDowngradedDueToTxFlooding && _notAcceptedTxsSinceLastCheck / _txFloodCheckInterval.TotalSeconds > 10)
            {
                if (Logger.IsDebug) Logger.Debug($"Downgrading {Node.Id} due to tx flooding");
                _isDowngradedDueToTxFlooding = true;
            }
            else
            {
                if (_notAcceptedTxsSinceLastCheck / _txFloodCheckInterval.TotalSeconds > 100)
                {
                    if (Logger.IsDebug) Logger.Debug($"Disconnecting {Node.Id} due to tx flooding");
                    InitiateDisconnect(DisconnectReason.UselessPeer, $"tx flooding {_notAcceptedTxsSinceLastCheck}/{_txFloodCheckInterval.TotalSeconds > 100}");
                }
            }

            _notAcceptedTxsSinceLastCheck = 0;

            if (Session.IsClosing)
            {
                OnDisposed();
            }
        }

        public override byte ProtocolVersion { get; protected set; } = 62;

        public override string ProtocolCode => Protocol.Eth;
        public override int MessageIdSpaceSize => 8;

        public override bool HasAvailableCapability(Capability capability) => false;
        public override bool HasAgreedCapability(Capability capability) => false;
        public override void AddSupportedCapability(Capability capability) { }

        public override event EventHandler<ProtocolInitializedEventArgs> ProtocolInitialized;

        public override event EventHandler<ProtocolEventArgs> SubprotocolRequested
        {
            add { }
            remove { }
        }

        public override void Init()
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
            if(NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportOutgoingMessage(Session.Node.Host, Name, statusMessage.ToString());
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

        public override void HandleMessage(ZeroPacket message)
        {
            if (message.PacketType != Eth62MessageCode.Status && !_statusReceived)
            {
                throw new SubprotocolException($"No {nameof(StatusMessage)} received prior to communication with {Session.Node:c}.");
            }

            int size = message.Content.ReadableBytes;
            
            // Logger.Warn($"Received a message {message.Protocol}.{Enum.GetName(typeof(Eth62MessageCode), message.PacketType)} of size {size/1024}kb");
            
            switch (message.PacketType)
            {
                case Eth62MessageCode.Status:
                    StatusMessage statusMessage = Deserialize<StatusMessage>(message.Content);
                    if(NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(Session.Node.Host, Name, statusMessage.ToString());
                    Handle(statusMessage);
                    break;
                case Eth62MessageCode.NewBlockHashes:
                    if(NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(Session.Node.Host, Name, nameof(NewBlockHashesMessage));
                    Interlocked.Increment(ref Counter);
                    if (Logger.IsTrace) Logger.Trace($"{Counter:D5} NewBlockHashes from {Node:c}");
                    Metrics.Eth62NewBlockHashesReceived++;
                    Handle(Deserialize<NewBlockHashesMessage>(message.Content));
                    break;
                case Eth62MessageCode.Transactions:
                    Interlocked.Increment(ref Counter);
                    Metrics.Eth62TransactionsReceived++;
                    TransactionsMessage transactionsMessage = Deserialize<TransactionsMessage>(message.Content);
                    if(NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(Session.Node.Host, Name, $"{nameof(TransactionsMessage)}({transactionsMessage.Transactions.Length})");
                    if (!_isDowngradedDueToTxFlooding || 10 > _random.Next(0, 99)) // TODO: disable that when IsMining is set to true
                    {
                        Handle(transactionsMessage);
                    }

                    break;
                case Eth62MessageCode.GetBlockHeaders:
                    Interlocked.Increment(ref Counter);
                    if (Logger.IsTrace) Logger.Trace($"{Counter:D5} GetBlockHeaders from {Node:c}");
                    Metrics.Eth62GetBlockHeadersReceived++;
                    GetBlockHeadersMessage getBlockHeadersMessage = Deserialize<GetBlockHeadersMessage>(message.Content);
                    if(NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(Session.Node.Host, Name, $"{nameof(GetBlockHeadersMessage)}({getBlockHeadersMessage.StartingBlockNumber}|{getBlockHeadersMessage.StartingBlockHash}, {getBlockHeadersMessage.MaxHeaders})");
                    Handle(getBlockHeadersMessage);
                    break;
                case Eth62MessageCode.BlockHeaders:
                    Interlocked.Increment(ref Counter);
                    if (Logger.IsTrace) Logger.Trace($"{Counter:D5} BlockHeaders from {Node:c}");
                    Metrics.Eth62BlockHeadersReceived++;
                    BlockHeadersMessage blockHeadersMessage = Deserialize<BlockHeadersMessage>(message.Content);
                    if(NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(Session.Node.Host, Name, $"{nameof(BlockHeadersMessage)}({blockHeadersMessage.BlockHeaders.Length})");
                    Handle(blockHeadersMessage, size);
                    break;
                case Eth62MessageCode.GetBlockBodies:
                    Interlocked.Increment(ref Counter);
                    if (Logger.IsTrace) Logger.Trace($"{Counter:D5} GetBlockBodies from {Node:c}");
                    Metrics.Eth62GetBlockBodiesReceived++;
                    GetBlockBodiesMessage getBlockBodiesMessage = Deserialize<GetBlockBodiesMessage>(message.Content);
                    if(NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(Session.Node.Host, Name, $"{nameof(GetBlockBodiesMessage)}({getBlockBodiesMessage.BlockHashes.Count})");
                    Handle(getBlockBodiesMessage);
                    break;
                case Eth62MessageCode.BlockBodies:
                    Interlocked.Increment(ref Counter);
                    if (Logger.IsTrace) Logger.Trace($"{Counter:D5} BlockBodies from {Node:c}");
                    Metrics.Eth62BlockBodiesReceived++;
                    BlockBodiesMessage blockBodiesMessage = Deserialize<BlockBodiesMessage>(message.Content);
                    if(NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(Session.Node.Host, Name, $"{nameof(BlockBodiesMessage)}({blockBodiesMessage.Bodies.Length})");
                    Handle(blockBodiesMessage, size);
                    break;
                case Eth62MessageCode.NewBlock:
                    Interlocked.Increment(ref Counter);
                    if (Logger.IsTrace) Logger.Trace($"{Counter:D5} NewBlock from {Node:c}");
                    Metrics.Eth62NewBlockReceived++;
                    NewBlockMessage newBlockMessage = Deserialize<NewBlockMessage>(message.Content);
                    if(NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(Session.Node.Host, Name, $"{nameof(NewBlockMessage)}({newBlockMessage.Block.Number})");
                    Handle(newBlockMessage);
                    break;
            }
        }

        public override string Name => "eth62";
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

            EthProtocolInitializedEventArgs eventArgs = new EthProtocolInitializedEventArgs(this)
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
                Transaction transaction = msg.Transactions[i];
                transaction.DeliveredBy = Node.Id;
                transaction.Timestamp = _timestamper.EpochSeconds;
                AddTxResult result = _txPool.AddTransaction(transaction, SyncServer.Head.Number, TxHandlingOptions.None);
                if (result != AddTxResult.Added)
                {
                    _notAcceptedTxsSinceLastCheck++;
                }

                if (Logger.IsTrace) Logger.Trace($"{Node:c} sent {transaction.Hash} tx and it was {result} (chain ID = {transaction.Signature.ChainId})");
            }
        }

        private void Handle(NewBlockHashesMessage newBlockHashes)
        {
            foreach ((Keccak hash, long number) in newBlockHashes.BlockHashes)
            {
                SyncServer.HintBlock(hash, number, Node);
            }
        }

        private void Handle(NewBlockMessage newBlockMessage)
        {
            newBlockMessage.Block.Header.TotalDifficulty = newBlockMessage.TotalDifficulty;

            try
            {
                SyncServer.AddNewBlock(newBlockMessage.Block, Session.Node);
            }
            catch (Exception e)
            {
                Logger.Debug($"Adding new block {newBlockMessage.Block?.ToString(Block.Format.Short)} from {Node:c} failed: " + e.Message);
                throw;
            }
        }

        public override void NotifyOfNewBlock(Block block, SendBlockPriority priority)
        {
            if (priority == SendBlockPriority.High)
            {
                SendNewBlock(block);
            }
            else
            {
                HintNewBlock(block.Hash, block.Number);
            }
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
            msg.BlockHashes = new[] { (blockHash, number) };
            Send(msg);
        }


        protected override void OnDisposed()
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