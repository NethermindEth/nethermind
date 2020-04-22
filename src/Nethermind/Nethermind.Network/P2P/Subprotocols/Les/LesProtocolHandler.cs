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
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Les
{
    public class LesProtocolHandler : SyncPeerProtocolHandlerBase, IZeroProtocolHandler
    {
        public override string Name => "les3";

        public LesProtocolHandler(
            ISession session,
            IMessageSerializationService serializer,
            INodeStatsManager statsManager,
            ISyncServer syncServer,
            ILogManager logManager,
            ITxPool txPool): base(session, serializer, statsManager, syncServer, txPool, logManager)
        {

        }

        public override void Init()
        {
            if (Logger.IsTrace) Logger.Trace($"{ProtocolCode} v{ProtocolVersion} subprotocol initializing with {Session.Node:c}");
            if (SyncServer.Head == null)
            {
                throw new InvalidOperationException($"Cannot initialize {ProtocolCode} v{ProtocolVersion} protocol without the head block set");
            }

            BlockHeader head = SyncServer.Head;
            StatusMessage statusMessage = new StatusMessage
            {
                ProtocolVersion = ProtocolVersion,
                ChainId = (UInt256)SyncServer.ChainId,
                TotalDifficulty = head.TotalDifficulty ?? head.Difficulty,
                BestHash = head.Hash,
                HeadBlockNo = head.Number,
                GenesisHash = SyncServer.Genesis.Hash,

                // TODO - implement config option for these
                ServeHeaders = true,
                ServeChainSince = 0x00,
                //if (config.recentchain != null)
                //    ServeRecentChain = Config.recentchain
                ServeStateSince = 0x00,
                //if (Config.serverecentstate != null)
                //    ServeRecentState = Config.RecentState
                TxRelay = true,
                // TODO - should allow setting to infinite
                BufferLimit = int.MaxValue,
                MaximumRechargeRate = int.MaxValue
            };
            Send(statusMessage);

            if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportOutgoingMessage(Session.Node.Host, Name, statusMessage.ToString());
            Metrics.LesStatusesSent++;

            CheckProtocolInitTimeout().ContinueWith(x =>
            {
                if (x.IsFaulted && Logger.IsError)
                {
                    Logger.Error("Error during lesProtocol handler timeout logic", x.Exception);
                }
            });
        }


        public override byte ProtocolVersion { get; protected set; } = 3;

        public override string ProtocolCode => Protocol.Les;

        public override int MessageIdSpaceSize => 8;

        protected override TimeSpan InitTimeout => Timeouts.Les3Status;

        public byte RequestedAnnounceType = 0;

        public override event EventHandler<ProtocolInitializedEventArgs> ProtocolInitialized;
        public override event EventHandler<ProtocolEventArgs> SubprotocolRequested
         {
            add { }
            remove { }
        }

        public override void AddSupportedCapability(Capability capability) { }

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
                    StatusMessage statusMessage = Deserialize<StatusMessage>(message.Content);
                    if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(Session.Node.Host, Name, statusMessage.ToString());
                    Handle(statusMessage);
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
                             Environment.NewLine + $" network ID\t{status.ChainId}," +
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
            LesProtocolInitializedEventArgs eventArgs = new LesProtocolInitializedEventArgs(this)
            {
                Protocol = status.Protocol,
                ProtocolVersion = status.ProtocolVersion,
                ChainId = (long) status.ChainId, // todo should these really be different data types?
                TotalDifficulty = status.TotalDifficulty,
                BestHash = status.BestHash,
                HeadBlockNo = status.HeadBlockNo,
                GenesisHash = status.GenesisHash,
                AnnounceType = status.AnnounceType.Value,
                ServeHeaders = status.ServeHeaders,
                ServeChainSince = status.ServeChainSince,
                ServeRecentChain = status.ServeRecentChain,
                ServeStateSince = status.ServeStateSince,
                ServeRecentState = status.ServeRecentState,
                TxRelay = status.TxRelay,
                BufferLimit = status.BufferLimit,
                MaximumRechargeRate = status.MaximumRechargeRate,
                MaximumRequestCosts = status.MaximumRequestCosts
            };

            if (status.BestHash == new Keccak("0x828f6e9967f75742364c7ab5efd6e64428e60ad38e218789aaf108fbd0232973"))
            {
                InitiateDisconnect(DisconnectReason.UselessPeer, "One of the Rinkeby nodes stuck at Constantinople transition");
            }

            TotalDifficulty = status.TotalDifficulty;
            RequestedAnnounceType = status.AnnounceType.Value;

            ProtocolInitialized?.Invoke(this, eventArgs);
        }

        public override bool HasAgreedCapability(Capability capability) => false;

        public override bool HasAvailableCapability(Capability capability) => false;

        protected override void OnDisposed() { }
    }
}
