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

using System.Linq;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Logging;
using Nethermind.Stats;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Synchronization
{
    [TestFixture]
    public class SyncPeersReportTest
    {
        [Test]
        public void Can_write_no_peers()
        {
            IEthSyncPeerPool syncPeerPool = Substitute.For<IEthSyncPeerPool>();
            SyncPeersReport report = new SyncPeersReport(syncPeerPool, Substitute.For<INodeStatsManager>(), NoErrorLimboLogs.Instance);
            report.WriteShortReport();
            report.WriteFullReport();
        }

        [Test]
        public void Can_write_one_uninitialized()
        {
            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            IEthSyncPeerPool syncPeerPool = Substitute.For<IEthSyncPeerPool>();
            PeerInfo peer1 = new PeerInfo(syncPeer);
            peer1.IsInitialized = false;
            
            PeerInfo[] peers = new PeerInfo[] {peer1};
            syncPeerPool.PeerCount.Returns(peers.Length);

            SyncPeersReport report = new SyncPeersReport(syncPeerPool, Substitute.For<INodeStatsManager>(), NoErrorLimboLogs.Instance);
            report.WriteShortReport();
            report.WriteFullReport();
        }
        
        [Test]
        public void Can_write_one_uninitialized_one_initialized()
        {
            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            IEthSyncPeerPool syncPeerPool = Substitute.For<IEthSyncPeerPool>();
            PeerInfo peer1 = new PeerInfo(syncPeer);
            peer1.IsInitialized = false;
            
            PeerInfo peer2 = new PeerInfo(syncPeer);
            peer2.IsInitialized = true;
            
            PeerInfo[] peers = new PeerInfo[] {peer1, peer2};
            syncPeerPool.PeerCount.Returns(peers.Length);

            SyncPeersReport report = new SyncPeersReport(syncPeerPool, Substitute.For<INodeStatsManager>(), NoErrorLimboLogs.Instance);
            report.WriteShortReport();
            report.WriteFullReport();
        }
        
        [Test]
        public void Can_write_report_update()
        {
            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            IEthSyncPeerPool syncPeerPool = Substitute.For<IEthSyncPeerPool>();
            PeerInfo peer1 = new PeerInfo(syncPeer);
            peer1.IsInitialized = false;
            
            PeerInfo peer2 = new PeerInfo(syncPeer);
            peer2.IsInitialized = true;
            
            PeerInfo[] peers = new PeerInfo[] {peer1, peer2};
            syncPeerPool.PeerCount.Returns(peers.Length);

            syncPeerPool.AllPeers.Returns(peers);

            SyncPeersReport report = new SyncPeersReport(syncPeerPool, Substitute.For<INodeStatsManager>(), NoErrorLimboLogs.Instance);
            report.WriteShortReport();
            report.WriteFullReport();

            peer1.IsInitialized = true;
            report.WriteShortReport();
            report.WriteFullReport();
        }
        
        [Test]
        public void Can_write_report_update_with_allocations()
        {
            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            IEthSyncPeerPool syncPeerPool = Substitute.For<IEthSyncPeerPool>();
            PeerInfo peer1 = new PeerInfo(syncPeer);
            peer1.IsInitialized = false;
            
            PeerInfo peer2 = new PeerInfo(syncPeer);
            peer2.IsInitialized = true;
            
            PeerInfo[] peers = new PeerInfo[] {peer1, peer2};
            syncPeerPool.PeerCount.Returns(peers.Length);

            syncPeerPool.AllPeers.Returns(peers);
            syncPeerPool.Allocations.Returns(peers.Select(p => new SyncPeerAllocation(p, "desc")));

            SyncPeersReport report = new SyncPeersReport(syncPeerPool, Substitute.For<INodeStatsManager>(), NoErrorLimboLogs.Instance);
            report.WriteShortReport();
            report.WriteFullReport();

            peer1.IsInitialized = true;
            report.WriteShortReport();
            report.WriteFullReport();
        }
    }
}