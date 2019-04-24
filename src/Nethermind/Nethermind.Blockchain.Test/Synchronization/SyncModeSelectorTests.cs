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

            (long BestRemote, long BestLocalHeader, long BestLocalState, SyncMode ExpectedState)[] states =
            {
                (0, 0, 0, SyncMode.Full),
                (1032, 0, 0, SyncMode.Headers),
                (1032, 512, 0, SyncMode.Headers),
                (1032, 1000, 0, SyncMode.StateNodes),
                (1048, 1000, 1000, SyncMode.Headers),
                (1048, 1016, 1000, SyncMode.StateNodes),
                (1048, 1016, 1016, SyncMode.Full),
                (1048, 1048, 1048, SyncMode.Full),
                (1048, 1049, 1049, SyncMode.Full),
                (2096, 1049, 1049, SyncMode.Headers),
            };

            for (int i = 0; i < states.Length; i++)
            {
                peerInfo1.HeadNumber = states[i].BestRemote;
                selector.Update(states[i].BestLocalHeader, states[i].BestLocalState);
                Assert.AreEqual(states[i].ExpectedState, selector.Current, i.ToString());    
            }
        }

        [TestCase(true, 1032, 999, 0, SyncMode.Headers)]
        [TestCase(false, 1032, 1000, 0, SyncMode.Full)]
        [TestCase(true, 1032, 1000, 0, SyncMode.StateNodes)]
        [TestCase(false, 1032, 1000, 0, SyncMode.Full)]
        [TestCase(true, 1032, 1000, 1000, SyncMode.Full)]
        [TestCase(false, 1032, 1000, 1000, SyncMode.Full)]
        [TestCase(true, 0, 1032, 1032, SyncMode.Full)]
        [TestCase(false, 0, 1032, 1032, SyncMode.Full)]
        public void Selects_correctly(bool useFastSync, long bestRemote, long bestLocalHeader, long bestLocalState, SyncMode expected)
        {
            bool changedInvoked = false;

            SyncModeSelector selector = BuildSelector(useFastSync, bestRemote);
            selector.Changed += (s, e) => changedInvoked = true;

            SyncMode beforeUpdate = selector.Current;

            selector.Update(bestLocalHeader, bestLocalState);
            Assert.AreEqual(expected, selector.Current, "as expected");
            if (expected != beforeUpdate)
            {
                Assert.True(changedInvoked, "changed");
            }
        }

        [TestCase(true, 0, 0, SyncMode.Headers)]
        [TestCase(false, 0, 0, SyncMode.Full)]
        [TestCase(true, 1000, 0, SyncMode.Headers)]
        [TestCase(false, 1000, 0, SyncMode.Full)]
        [TestCase(true, 1000, 1000, SyncMode.Headers)]
        [TestCase(false, 1000, 1000, SyncMode.Full)]
        public void Does_not_change_when_no_peers(bool useFastSync, long bestLocalHeader, long bestLocalState, SyncMode expected)
        {
            SyncModeSelector selector = BuildSelectorNoPeers(useFastSync);
            selector.Update(bestLocalHeader, bestLocalState);
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