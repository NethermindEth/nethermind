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
            SyncPeersReport report = new SyncPeersReport(syncPeerPool, Substitute.For<INodeStatsManager>(), NoErrorLimboLogs.Instance);
            report.WriteShortReport();
            report.WriteFullReport();
        }

        [Test]
        public void Can_write_one_uninitialized()
        {
            ISyncPeerPool syncPeerPool = Substitute.For<ISyncPeerPool>();

            var syncPeer = BuildPeer(false);

            var peers = new[] {syncPeer};
            syncPeerPool.PeerCount.Returns(peers.Length);

            SyncPeersReport report = new SyncPeersReport(syncPeerPool, Substitute.For<INodeStatsManager>(), NoErrorLimboLogs.Instance);
            report.WriteShortReport();
            report.WriteFullReport();
        }

        [Test]
        public void Can_write_one_uninitialized_one_initialized()
        {
            ISyncPeerPool syncPeerPool = Substitute.For<ISyncPeerPool>();
            var syncPeer = BuildPeer(false);
            var syncPeer2 = BuildPeer(true);

            var peers = new[] {syncPeer, syncPeer2};
            
            syncPeerPool.PeerCount.Returns(peers.Length);

            SyncPeersReport report = new SyncPeersReport(syncPeerPool, Substitute.For<INodeStatsManager>(), NoErrorLimboLogs.Instance);
            report.WriteShortReport();
            report.WriteFullReport();
        }

        [Test]
        public void Can_write_report_update()
        {
            ISyncPeerPool syncPeerPool = Substitute.For<ISyncPeerPool>();

            var syncPeer = BuildPeer(false);
            var syncPeer2 = BuildPeer(true);

            var peers = new[] {syncPeer, syncPeer2};
            
            syncPeerPool.PeerCount.Returns(peers.Length);

            syncPeerPool.AllPeers.Returns(peers);

            SyncPeersReport report = new SyncPeersReport(syncPeerPool, Substitute.For<INodeStatsManager>(), NoErrorLimboLogs.Instance);
            report.WriteShortReport();
            report.WriteFullReport();

            syncPeer.IsInitialized.Returns(true);
            report.WriteShortReport();
            report.WriteFullReport();
        }

        private static PeerInfo BuildPeer(bool initialized)
        {
            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            PeerInfo peer = new PeerInfo(syncPeer);
            syncPeer.IsInitialized.Returns(initialized);
            return peer;
        }

        [Test]
        public void Can_write_report_update_with_allocations()
        {
            ISyncPeerPool syncPeerPool = Substitute.For<ISyncPeerPool>();
            var syncPeer = BuildPeer(false);
            var syncPeer2 = BuildPeer(true);

            var peers = new[] {syncPeer, syncPeer2};
            syncPeerPool.PeerCount.Returns(peers.Length);

            syncPeerPool.AllPeers.Returns(peers);

            SyncPeersReport report = new SyncPeersReport(syncPeerPool, Substitute.For<INodeStatsManager>(), NoErrorLimboLogs.Instance);
            report.WriteShortReport();
            report.WriteFullReport();

            syncPeer.IsInitialized.Returns(true);
            report.WriteShortReport();
            report.WriteFullReport();
        }
    }
}
