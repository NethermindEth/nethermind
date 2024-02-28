// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Les.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.Synchronization.FastSync;
using CancellationToken = System.Threading.CancellationToken;

namespace Nethermind.Network.P2P.Subprotocols.Les
{
    public class LesProtocolHandler : SyncPeerProtocolHandlerBase, ISyncPeer
    {
        public override string Name => "les3";
        public override bool IncludeInTxPool => false;

        public LesProtocolHandler(
            ISession session,
            IMessageSerializationService serializer,
            INodeStatsManager statsManager,
            ISyncServer syncServer,
            IBackgroundTaskScheduler backgroundTaskScheduler,
            ILogManager logManager) : base(session, serializer, statsManager, syncServer, backgroundTaskScheduler, logManager)
        {
            _lastSentBlock = SyncServer.Head;
        }

        public override void Init()
        {
            if (Logger.IsTrace) Logger.Trace($"{ProtocolCode} v{ProtocolVersion} subprotocol initializing with {Session.Node:c}");
            if (SyncServer.Head is null)
            {
                throw new InvalidOperationException($"Cannot initialize {ProtocolCode} v{ProtocolVersion} protocol without the head block set");
            }

            BlockHeader head = SyncServer.Head;
            StatusMessage statusMessage = new()
            {
                ProtocolVersion = ProtocolVersion,
                NetworkId = (UInt256)SyncServer.NetworkId,
                TotalDifficulty = head.TotalDifficulty ?? head.Difficulty,
                BestHash = head.Hash,
                HeadBlockNo = head.Number,
                GenesisHash = SyncServer.Genesis.Hash,

                // TODO - implement config option for these
                ServeHeaders = true,
                ServeChainSince = 0x00,
                //if (config.recentchain is not null)
                //    ServeRecentChain = Config.recentchain
                ServeStateSince = 0x00,
                //if (Config.serverecentstate is not null)
                //    ServeRecentState = Config.RecentState
                TxRelay = true,
                // TODO - should allow setting to infinite
                BufferLimit = int.MaxValue,
                MaximumRechargeRate = int.MaxValue
            };
            Send(statusMessage);

            Metrics.LesStatusesSent++;

            CheckProtocolInitTimeout().ContinueWith(x =>
            {
                if (x.IsFaulted && Logger.IsError)
                {
                    Logger.Error("Error during lesProtocol handler timeout logic", x.Exception);
                }
            });
        }


        public override byte ProtocolVersion => 3;

        public override string ProtocolCode => Protocol.Les;

        public override int MessageIdSpaceSize => 23;

        protected override TimeSpan InitTimeout => Timeouts.Les3Status;

        public LesAnnounceType RequestedAnnounceType;

        public override event EventHandler<ProtocolInitializedEventArgs> ProtocolInitialized;
        public override event EventHandler<ProtocolEventArgs> SubprotocolRequested
        {
            add { }
            remove { }
        }

        bool _statusReceived;
        public override void HandleMessage(ZeroPacket message)
        {
            if (message.PacketType != LesMessageCode.Status && !_statusReceived)
            {
                throw new SubprotocolException($"No {nameof(StatusMessage)} received prior to communication with {Session.Node:c}.");
            }

            int size = message.Content.ReadableBytes;

            switch (message.PacketType)
            {
                case LesMessageCode.Status:
                    {
                        using StatusMessage statusMessage = Deserialize<StatusMessage>(message.Content);
                        if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(Session.Node.Address, Name, statusMessage.ToString(), size);
                        Handle(statusMessage);
                        break;
                    }
                case LesMessageCode.GetBlockHeaders:
                    GetBlockHeadersMessage getBlockHeadersMessage = Deserialize<GetBlockHeadersMessage>(message.Content);
                    if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(Session.Node.Address, Name, getBlockHeadersMessage.ToString(), size);
                    BackgroundTaskScheduler.ScheduleSyncServe(getBlockHeadersMessage, Handle);
                    break;
                case LesMessageCode.GetBlockBodies:
                    GetBlockBodiesMessage getBlockBodiesMessage = Deserialize<GetBlockBodiesMessage>(message.Content);
                    if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(Session.Node.Address, Name, getBlockBodiesMessage.ToString(), size);
                    BackgroundTaskScheduler.ScheduleSyncServe(getBlockBodiesMessage, Handle);
                    break;
                case LesMessageCode.GetReceipts:
                    GetReceiptsMessage getReceiptsMessage = Deserialize<GetReceiptsMessage>(message.Content);
                    if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(Session.Node.Address, Name, getReceiptsMessage.ToString(), size);
                    BackgroundTaskScheduler.ScheduleSyncServe(getReceiptsMessage, Handle);
                    break;
                case LesMessageCode.GetContractCodes:
                    GetContractCodesMessage getContractCodesMessage = Deserialize<GetContractCodesMessage>(message.Content);
                    if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(Session.Node.Address, Name, getContractCodesMessage.ToString(), size);
                    BackgroundTaskScheduler.ScheduleSyncServe(getContractCodesMessage, Handle);
                    break;
                case LesMessageCode.GetHelperTrieProofs:
                    GetHelperTrieProofsMessage getHelperTrieProofsMessage = Deserialize<GetHelperTrieProofsMessage>(message.Content);
                    if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(Session.Node.Address, Name, getHelperTrieProofsMessage.ToString(), size);
                    BackgroundTaskScheduler.ScheduleSyncServe(getHelperTrieProofsMessage, Handle);
                    break;
            }
        }

        public void Handle(StatusMessage status)
        {
            Metrics.LesStatusesReceived++;

            // set defaults
            if (!status.AnnounceType.HasValue) status.AnnounceType = 1;

            if (_statusReceived)
            {
                throw new SubprotocolException($"{nameof(StatusMessage)} has already been received in the past");
            }

            _statusReceived = true;
            if (Logger.IsTrace)
                Logger.Trace($"LES received status from {Session.Node:c} with" +
                             Environment.NewLine + $" prot version\t{status.ProtocolVersion}" +
                             Environment.NewLine + $" network ID\t{status.NetworkId}," +
                             Environment.NewLine + $" genesis hash\t{status.GenesisHash}," +
                             Environment.NewLine + $" best hash\t{status.BestHash}," +
                             Environment.NewLine + $" head blockno\t{status.HeadBlockNo}," +
                             Environment.NewLine + $" difficulty\t{status.TotalDifficulty}" +
                             Environment.NewLine + $" announce type\t{status.AnnounceType}" +
                             Environment.NewLine + $" serve headers\t{status.ServeHeaders}" +
                             Environment.NewLine + $" serve chain since\t{status.ServeChainSince}" +
                             Environment.NewLine + $" serve recent chain\t{status.ServeRecentChain}" +
                             Environment.NewLine + $" serve state since\t{status.ServeStateSince}" +
                             Environment.NewLine + $" serve recent state\t{status.ServeRecentState}" +
                             Environment.NewLine + $" transaction relay\t{status.TxRelay}" +
                             Environment.NewLine + $" buffer limit\t{status.BufferLimit}" +
                             Environment.NewLine + $" max recharge\t{status.MaximumRechargeRate}");
            // todo - log max request costs table

            _remoteHeadBlockHash = status.BestHash;

            ReceivedProtocolInitMsg(status);
            SyncPeerProtocolInitializedEventArgs eventArgs = new(this)
            {
                NetworkId = (ulong)status.NetworkId,
                BestHash = status.BestHash,
                GenesisHash = status.GenesisHash,
                Protocol = status.Protocol,
                ProtocolVersion = status.ProtocolVersion,
                TotalDifficulty = status.TotalDifficulty
            };

            TotalDifficulty = status.TotalDifficulty;
            RequestedAnnounceType = (LesAnnounceType)status.AnnounceType.Value;
            if (RequestedAnnounceType == LesAnnounceType.Signed) throw new NotImplementedException("Signed announcements are not yet supported.");

            ProtocolInitialized?.Invoke(this, eventArgs);
        }

        public async Task<BlockHeadersMessage> Handle(GetBlockHeadersMessage getBlockHeaders, CancellationToken cancellationToken)
        {
            using var message = getBlockHeaders;
            Eth.V62.Messages.BlockHeadersMessage ethBlockHeadersMessage = await FulfillBlockHeadersRequest(message.EthMessage, cancellationToken);
            // todo - implement cost tracking
            return new BlockHeadersMessage(ethBlockHeadersMessage, message.RequestId, int.MaxValue);
        }

        public async Task<BlockBodiesMessage> Handle(GetBlockBodiesMessage getBlockBodies, CancellationToken cancellationToken)
        {
            using var message = getBlockBodies;
            Eth.V62.Messages.BlockBodiesMessage ethBlockBodiesMessage = await FulfillBlockBodiesRequest(message.EthMessage, cancellationToken);
            // todo - implement cost tracking
            return new BlockBodiesMessage(ethBlockBodiesMessage, message.RequestId, int.MaxValue);
        }

        public async Task<ReceiptsMessage> Handle(GetReceiptsMessage getReceipts, CancellationToken cancellationToken)
        {
            using var message = getReceipts;
            Eth.V63.Messages.ReceiptsMessage ethReceiptsMessage = await FulfillReceiptsRequest(message.EthMessage, cancellationToken);
            // todo - implement cost tracking
            return new ReceiptsMessage(ethReceiptsMessage, message.RequestId, int.MaxValue);
        }

        public Task<ContractCodesMessage> Handle(GetContractCodesMessage getContractCodes, CancellationToken cancellationToken)
        {
            using var message = getContractCodes;
            var codes = SyncServer.GetNodeData(message.RequestAddresses, cancellationToken, NodeDataType.Code);
            // todo - implement cost tracking
            return Task.FromResult(new ContractCodesMessage(codes, message.RequestId, int.MaxValue));
        }

        public Task<HelperTrieProofsMessage> Handle(GetHelperTrieProofsMessage getHelperTrieProofs, CancellationToken cancellationToken)
        {
            using var message = getHelperTrieProofs;
            List<byte[]> proofNodes = new();
            List<byte[]> auxData = new();

            for (int requestNo = 0; requestNo < message.Requests.Length; requestNo++)
            {
                var request = message.Requests[requestNo];
                switch (request.SubType)
                {
                    case HelperTrieType.CHT:
                        GetCHTData(request, proofNodes, auxData);
                        break;
                    case HelperTrieType.BloomBits:
                        throw new SubprotocolException("bloom bits trie not yet supported");
                }
            }

            return Task.FromResult(new HelperTrieProofsMessage(proofNodes.Distinct().ToArray(), auxData.ToArray(), message.RequestId, int.MaxValue));
        }

        public void GetCHTData(HelperTrieRequest request, List<byte[]> proofNodes, List<byte[]> auxData)
        {
            var cht = SyncServer.GetCHT();
            // todo - enum?
            if (request.AuxiliaryData == 1)
            {
                auxData.Add(cht.RootHash.BytesToArray());
                return;
            }
            else if (request.AuxiliaryData == 2)
            {
                (Hash256 hash, _) = cht.Get(request.Key);
                using IOwnedReadOnlyList<BlockHeader> headerResult = SyncServer.FindHeaders(hash, 1, 0, false);
                if (headerResult.Count != 1) throw new SubprotocolException($"Unable to find header for block {request.Key.WithoutLeadingZeros().ToArray().ToLongFromBigEndianByteArrayWithoutLeadingZeros()} for GetHelperProofs response.");
                auxData.Add(Rlp.Encode(headerResult[0]).Bytes);
            }
            proofNodes.AddRange(Synchronization.LesSync.CanonicalHashTrie.BuildProof(request.Key, request.SectionIndex, request.FromLevel));
        }

        private BlockHeader _lastSentBlock;

        public override void NotifyOfNewBlock(Block block, SendBlockMode mode)
        {
            if (RequestedAnnounceType == LesAnnounceType.None) return;
            if (!block.TotalDifficulty.HasValue)
            {
                throw new InvalidOperationException($"Trying to send a block {block.Hash} with null total difficulty");
            }

            if (block.TotalDifficulty <= _lastSentBlock.TotalDifficulty) return;

            AnnounceMessage announceMessage = new();
            announceMessage.HeadHash = block.Hash;
            announceMessage.HeadBlockNo = block.Number;
            announceMessage.TotalDifficulty = block.TotalDifficulty.Value;
            if (_lastSentBlock is null || block.ParentHash == _lastSentBlock.Hash)
                announceMessage.ReorgDepth = 0;
            else
            {
                BlockHeader firstCommonAncestor = SyncServer.FindLowestCommonAncestor(block.Header, _lastSentBlock);
                if (firstCommonAncestor is null)
                    throw new SubprotocolException($"Unable to send announcement to LES peer - No common ancestor found between {block.Header} and {_lastSentBlock}");
                announceMessage.ReorgDepth = _lastSentBlock.Number - firstCommonAncestor.Number;
            }

            _lastSentBlock = block.Header;

            Send(announceMessage);
        }

        Task<BlockHeader?> ISyncPeer.GetHeadBlockHeader(Hash256? hash, CancellationToken token)
        {
            return Task.FromResult(_lastSentBlock);
        }

        protected override void OnDisposed() { }
    }
}
