// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Synchronization;
using Nethermind.Logging;
using Nethermind.Stats;
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
            report.WriteShortReport();
            report.WriteFullReport();
        }

        [Test]
        public void Can_write_one_uninitialized()
        {
            ISyncPeerPool syncPeerPool = Substitute.For<ISyncPeerPool>();

            var syncPeer = BuildPeer(false);

            var peers = new[] { syncPeer };
            syncPeerPool.PeerCount.Returns(peers.Length);

            SyncPeersReport report = new(syncPeerPool, Substitute.For<INodeStatsManager>(), NoErrorLimboLogs.Instance);
            report.WriteShortReport();
            report.WriteFullReport();
        }

        [Test]
        public void Can_write_one_uninitialized_one_initialized()
        {
            ISyncPeerPool syncPeerPool = Substitute.For<ISyncPeerPool>();
            var syncPeer = BuildPeer(false);
            var syncPeer2 = BuildPeer(true);

            var peers = new[] { syncPeer, syncPeer2 };

            syncPeerPool.PeerCount.Returns(peers.Length);

            SyncPeersReport report = new(syncPeerPool, Substitute.For<INodeStatsManager>(), NoErrorLimboLogs.Instance);
            report.WriteShortReport();
            report.WriteFullReport();
        }

        [Test]
        public void Can_write_report_update()
        {
            ISyncPeerPool syncPeerPool = Substitute.For<ISyncPeerPool>();

            var syncPeer = BuildPeer(false);
            var syncPeer2 = BuildPeer(true);

            var peers = new[] { syncPeer, syncPeer2 };

            syncPeerPool.PeerCount.Returns(peers.Length);

            syncPeerPool.AllPeers.Returns(peers);

            SyncPeersReport report = new(syncPeerPool, Substitute.For<INodeStatsManager>(), NoErrorLimboLogs.Instance);
            report.WriteShortReport();
            report.WriteFullReport();

            syncPeer.IsInitialized.Returns(true);
            report.WriteShortReport();
            report.WriteFullReport();
        }

        private static PeerInfo BuildPeer(bool initialized)
        {
            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            PeerInfo peer = new(syncPeer);
            syncPeer.IsInitialized.Returns(initialized);
            return peer;
        }

        [Test]
        public void Can_write_report_update_with_allocations()
        {
            ISyncPeerPool syncPeerPool = Substitute.For<ISyncPeerPool>();
            var syncPeer = BuildPeer(false);
            var syncPeer2 = BuildPeer(true);

            var peers = new[] { syncPeer, syncPeer2 };
            syncPeerPool.PeerCount.Returns(peers.Length);

            syncPeerPool.AllPeers.Returns(peers);

            SyncPeersReport report = new(syncPeerPool, Substitute.For<INodeStatsManager>(), NoErrorLimboLogs.Instance);
            report.WriteShortReport();
            report.WriteFullReport();

            syncPeer.IsInitialized.Returns(true);
            report.WriteShortReport();
            report.WriteFullReport();
        }
    }
}
