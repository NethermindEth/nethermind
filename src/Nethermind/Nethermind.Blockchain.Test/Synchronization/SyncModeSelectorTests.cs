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

using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Logging;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Synchronization
{
    [TestFixture]
    public class SyncModeSelectorTests
    {
        [Test]
        public void Starts_with_headers_when_fast_sync_is_enabled()
        {
            SyncModeSelector selector = BuildSelector(true);
            Assert.AreEqual(SyncMode.Headers, selector.Current);
        }

        [Test]
        public void Starts_with_full_when_fast_sync_is_disabled()
        {
            SyncModeSelector selector = BuildSelector(false);
            Assert.AreEqual(SyncMode.Full, selector.Current);
        }

        [Test]
        public void Can_keep_changing_in_fast_sync()
        {
            IEthSyncPeerPool syncPeerPool = Substitute.For<IEthSyncPeerPool>();
            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            syncPeer.TotalDifficultyOnSessionStart.Returns((UInt256) (1024 * 1024));

            PeerInfo peerInfo1 = new PeerInfo(syncPeer) {HeadNumber = 0, IsInitialized = true};
            syncPeerPool.AllPeers.Returns(new[] {peerInfo1});
            syncPeerPool.UsefulPeers.Returns(new[] {peerInfo1});
            syncPeerPool.PeerCount.Returns(1);

            SyncConfig syncConfig = new SyncConfig();
            syncConfig.FastSync = true;
            SyncModeSelector selector = new SyncModeSelector(syncPeerPool, syncConfig, LimboLogs.Instance);
            Assert.AreEqual(SyncMode.Headers, selector.Current);

            (long BestRemote, long BestLocalHeader, long BestLocalFullBlock, long BestLocalState, SyncMode ExpectedState, string Description)[] states =
            {
                (0, 0, 0, 0, SyncMode.Full, "start"),
                (1032, 0, 0, 0, SyncMode.Headers, "learn about remote"),
                (1032, 512, 0, 0, SyncMode.Headers, "start downloading headers"),
                (1032, 1000, 0, 0, SyncMode.StateNodes, "finish downloading headers"),
                (1048, 1000, 0, 1000, SyncMode.Headers, "download node states up to best header"),
                (1048, 1016, 0, 1000, SyncMode.StateNodes, "catch up headers"),
                (1048, 1032, 0, 1016, SyncMode.StateNodes, "headers went too far, catch up with the nodes"),
                (1048, 1032, 0, 1032, SyncMode.Full, "ready to full sync"),
                (1068, 1048, 1048, 1036, SyncMode.Full, "full sync - blocks ahead of processing"),
                (1093, 1060, 1060, 1056, SyncMode.WaitForProcessor, "found better peer, need to catch up"),
                (1093, 1060, 1060, 1060, SyncMode.Headers, "first take headers"),
                (1093, 1092, 1060, 1060, SyncMode.StateNodes, "then nodes again"), 
                (2096, 1092, 1060, 1092, SyncMode.Headers, "found even better peer - get all headers"),
            };

            for (int i = 0; i < states.Length; i++)
            {
                Assert.GreaterOrEqual(states[i].BestLocalHeader, states[i].BestLocalState, "checking if the test case is correct - local state always less then local header");
                Assert.GreaterOrEqual(states[i].BestLocalHeader, states[i].BestLocalFullBlock, "checking if the test case is correct - local full block always less then local header");
                peerInfo1.HeadNumber = states[i].BestRemote;
                selector.Update(states[i].BestLocalHeader, states[i].BestLocalFullBlock, states[i].BestLocalState);
                Assert.AreEqual(states[i].ExpectedState, selector.Current, states[i].Description);    
            }
        }

        [TestCase(true, 1032, 999, 0, 0, SyncMode.Headers)]
        [TestCase(false, 1032, 1000, 0, 0, SyncMode.Full)]
        [TestCase(true, 1032, 1000, 0, 0, SyncMode.StateNodes)]
        [TestCase(false, 1032, 1000, 0, 0, SyncMode.Full)]
        [TestCase(true, 1032, 1000, 0, 1000, SyncMode.Full)]
        [TestCase(false, 1032, 1000, 0, 1000, SyncMode.Full)]
        [TestCase(true, 0, 1032, 0, 1032, SyncMode.Full)]
        [TestCase(false, 0, 1032, 0, 1032, SyncMode.Full)]
        public void Selects_correctly(bool useFastSync, long bestRemote, long bestLocalHeader, long bestLocalBestBlock, long bestLocalState, SyncMode expected)
        {
            bool changedInvoked = false;

            SyncModeSelector selector = BuildSelector(useFastSync, bestRemote);
            selector.Changed += (s, e) => changedInvoked = true;

            SyncMode beforeUpdate = selector.Current;

            selector.Update(bestLocalHeader, bestLocalBestBlock, bestLocalState);
            Assert.AreEqual(expected, selector.Current, "as expected");
            if (expected != beforeUpdate)
            {
                Assert.True(changedInvoked, "changed");
            }
        }

        [TestCase(true, 0, 0, 0, SyncMode.Headers)]
        [TestCase(false, 0, 0, 0, SyncMode.Full)]
        [TestCase(true, 1000, 0, 0, SyncMode.Headers)]
        [TestCase(false, 1000, 0, 0, SyncMode.Full)]
        [TestCase(true, 1000, 0, 1000, SyncMode.Headers)]
        [TestCase(false, 1000, 0, 1000, SyncMode.Full)]
        public void Does_not_change_when_no_peers(bool useFastSync, long bestLocalHeader, long bestLocalFullBLock, long bestLocalState, SyncMode expected)
        {
            SyncModeSelector selector = BuildSelectorNoPeers(useFastSync);
            selector.Update(bestLocalHeader, bestLocalFullBLock, bestLocalState);
            Assert.AreEqual(expected, selector.Current);
        }

        private static SyncModeSelector BuildSelector(bool fastSyncEnabled, long bestPeerBlock = 0L)
        {
            IEthSyncPeerPool syncPeerPool = Substitute.For<IEthSyncPeerPool>();
            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            syncPeer.TotalDifficultyOnSessionStart.Returns((UInt256) (1024 * 1024));

            PeerInfo peerInfo1 = new PeerInfo(syncPeer) {HeadNumber = bestPeerBlock, IsInitialized = true};
            PeerInfo peerInfo2 = new PeerInfo(syncPeer) {HeadNumber = bestPeerBlock, IsInitialized = true};
            PeerInfo peerInfo3 = new PeerInfo(syncPeer) {HeadNumber = 0, IsInitialized = true};
            PeerInfo peerInfo4 = new PeerInfo(syncPeer) {HeadNumber = bestPeerBlock * 2, IsInitialized = false};
            syncPeerPool.AllPeers.Returns(new[] {peerInfo1, peerInfo2, peerInfo3, peerInfo4});
            syncPeerPool.UsefulPeers.Returns(new[] {peerInfo1, peerInfo2});
            syncPeerPool.PeerCount.Returns(3);

            SyncConfig syncConfig = new SyncConfig();
            syncConfig.FastSync = fastSyncEnabled;
            SyncModeSelector selector = new SyncModeSelector(syncPeerPool, syncConfig, LimboLogs.Instance);
            return selector;
        }

        private static SyncModeSelector BuildSelectorNoPeers(bool fastSyncEnabled)
        {
            IEthSyncPeerPool syncPeerPool = Substitute.For<IEthSyncPeerPool>();
            syncPeerPool.AllPeers.Returns(new PeerInfo[] { });
            syncPeerPool.PeerCount.Returns(0);

            SyncConfig syncConfig = new SyncConfig();
            syncConfig.FastSync = fastSyncEnabled;
            SyncModeSelector selector = new SyncModeSelector(syncPeerPool, syncConfig, LimboLogs.Instance);
            return selector;
        }
    }
}