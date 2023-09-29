// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Peers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class SyncPeersReportTest
    {
        [Test]
        public void Can_write_no_peers()
        {
            ISyncPeerPool syncPeerPool = Substitute.For<ISyncPeerPool>();
            SyncPeersReport report = new(syncPeerPool, Substitute.For<INodeStatsManager>(), NoErrorLimboLogs.Instance);
            report.WriteAllocatedReport();
            report.WriteFullReport();
        }

        [Test]
        public void Can_write_one_uninitialized()
        {
            ISyncPeerPool syncPeerPool = Substitute.For<ISyncPeerPool>();

            PeerInfo syncPeer = BuildPeer(false);

            PeerInfo[] peers = { syncPeer };
            syncPeerPool.PeerCount.Returns(peers.Length);

            SyncPeersReport report = new(syncPeerPool, Substitute.For<INodeStatsManager>(), NoErrorLimboLogs.Instance);
            report.WriteAllocatedReport();
            report.WriteFullReport();
        }

        [Test]
        public void Can_write_one_uninitialized_one_initialized()
        {
            ISyncPeerPool syncPeerPool = Substitute.For<ISyncPeerPool>();
            PeerInfo syncPeer = BuildPeer(false);
            PeerInfo syncPeer2 = BuildPeer(true);

            PeerInfo[] peers = { syncPeer, syncPeer2 };

            syncPeerPool.PeerCount.Returns(peers.Length);

            SyncPeersReport report = new(syncPeerPool, Substitute.For<INodeStatsManager>(), NoErrorLimboLogs.Instance);
            report.WriteAllocatedReport();
            report.WriteFullReport();
        }

        [Test]
        public void Can_write_report_update()
        {
            ISyncPeerPool syncPeerPool = Substitute.For<ISyncPeerPool>();

            (PeerInfo syncPeer, StubSyncPeer syncPeerSyncPeer) = BuildPeerWithStubSyncPeer(false);
            PeerInfo syncPeer2 = BuildPeer(true);

            PeerInfo[] peers = { syncPeer, syncPeer2 };

            syncPeerPool.PeerCount.Returns(peers.Length);

            syncPeerPool.AllPeers.Returns(peers);

            SyncPeersReport report = new(syncPeerPool, Substitute.For<INodeStatsManager>(), NoErrorLimboLogs.Instance);
            report.WriteAllocatedReport();
            report.WriteFullReport();

            syncPeerSyncPeer.IsInitialized = true;
            report.WriteAllocatedReport();
            report.WriteFullReport();
        }

        private static PeerInfo BuildPeer(
            bool initialized,
            string ip = "127.0.0.1",
            int port = 3030,
            ConnectionDirection direction = ConnectionDirection.Out,
            int head = 9999,
            string protocolVersion = "eth99"
        )
        {
            (PeerInfo peer, StubSyncPeer _) =
                BuildPeerWithStubSyncPeer(initialized, ip, port, direction, head, protocolVersion);
            return peer;
        }

        private static (PeerInfo, StubSyncPeer) BuildPeerWithStubSyncPeer(
            bool initialized,
            string ip = "127.0.0.1",
            int port = 3030,
            ConnectionDirection direction = ConnectionDirection.Out,
            int head = 9999,
            string protocolVersion = "eth99"
        )
        {
            ISession session = Substitute.For<ISession>();
            session.Node.Returns(new Node(TestItem.PublicKeyA, ip, port));
            session.Direction.Returns(direction);

            IMessageSerializationService serializer = Substitute.For<IMessageSerializationService>();
            INodeStatsManager nodeStatsManager = Substitute.For<INodeStatsManager>();
            ISyncServer syncServer = Substitute.For<ISyncServer>();
            StubSyncPeer syncPeer = new StubSyncPeer(initialized, protocolVersion, session, serializer, nodeStatsManager, syncServer);

            syncPeer.HeadNumber = head;

            PeerInfo peer = new(syncPeer);
            return (peer, syncPeer);
        }

        [Test]
        public void Can_write_report_update_with_allocations()
        {
            ISyncPeerPool syncPeerPool = Substitute.For<ISyncPeerPool>();
            (PeerInfo syncPeer, StubSyncPeer syncPeerSyncPeer) = BuildPeerWithStubSyncPeer(false);
            PeerInfo syncPeer2 = BuildPeer(true);

            PeerInfo[] peers = { syncPeer, syncPeer2 };
            syncPeerPool.PeerCount.Returns(peers.Length);
            syncPeerPool.AllPeers.Returns(peers);

            SyncPeersReport report = new(syncPeerPool, Substitute.For<INodeStatsManager>(), NoErrorLimboLogs.Instance);
            report.WriteAllocatedReport();
            report.WriteFullReport();

            syncPeerSyncPeer.IsInitialized = true;
            report.WriteAllocatedReport();
            report.WriteFullReport();
        }

        [Test]
        public void PeerFormatIsCorrect()
        {
            PeerInfo syncPeer = BuildPeer(false);
            syncPeer.TryAllocate(AllocationContexts.All);

            PeerInfo syncPeer2 = BuildPeer(true, direction: ConnectionDirection.In);
            syncPeer2.PutToSleep(AllocationContexts.All, DateTime.Now);

            PeerInfo[] peers = { syncPeer, syncPeer2 };

            ISyncPeerPool syncPeerPool = Substitute.For<ISyncPeerPool>();
            syncPeerPool.PeerCount.Returns(peers.Length);
            syncPeerPool.AllPeers.Returns(peers);

            string expectedResult =
                "== Header ==" + Environment.NewLine +
                "===[Active][Sleep ][Peer(ProtocolVersion/Head/Host:Port/Direction)][Transfer Speeds (L/H/B/R/N/S)      ][Client Info (Name/Version/Operating System/Language)     ]" + Environment.NewLine +
                "--------------------------------------------------------------------------------------------------------------------------------------------------------------" + Environment.NewLine +
                "   [HBRNSW][      ][Peer|eth99|    9999|      127.0.0.1: 3030| Out][     |     |     |     |     |     ][]" + Environment.NewLine +
                "   [      ][HBRNSW][Peer|eth99|    9999|      127.0.0.1: 3030|  In][     |     |     |     |     |     ][]";

            SyncPeersReport report = new(syncPeerPool, Substitute.For<INodeStatsManager>(), NoErrorLimboLogs.Instance);
            string reportStr = report.MakeReportForPeers(peers, "== Header ==");
            reportStr.Should().Be(expectedResult);
        }

        private class StubSyncPeer : SyncPeerProtocolHandlerBase
        {
            public StubSyncPeer(bool initialized, string protocolVersion, ISession session,
                IMessageSerializationService serializer, INodeStatsManager statsManager, ISyncServer syncServer) :
                base(
                    session,
                    serializer,
                    statsManager,
                    syncServer,
                    NoErrorLimboLogs.Instance)
            {
                IsInitialized = initialized;
                Name = protocolVersion;
            }

            public override string Name { get; }
            public override byte ProtocolVersion { get; } = default;
            public override string ProtocolCode { get; } = default!;
            public override int MessageIdSpaceSize { get; } = default;
            protected override TimeSpan InitTimeout { get; } = default;
            public override event EventHandler<ProtocolInitializedEventArgs> ProtocolInitialized = delegate { };
            public override event EventHandler<ProtocolEventArgs> SubprotocolRequested = delegate { };
            public override void Init()
            {
                throw new NotImplementedException();
            }
            public override void HandleMessage(ZeroPacket message)
            {
                throw new NotImplementedException();
            }
            public override void NotifyOfNewBlock(Block block, SendBlockMode mode)
            {
                throw new NotImplementedException();
            }
            protected override void OnDisposed()
            {
                throw new NotImplementedException();
            }
        }
    }
}
