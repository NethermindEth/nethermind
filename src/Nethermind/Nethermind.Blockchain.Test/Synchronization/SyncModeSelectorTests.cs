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
using Nethermind.Core.Specs;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Synchronization
{
    [TestFixture]
    public class SyncModeSelectorTests
    {
        [Test]
        public void Starts_with_not_started_in_fast_sync_enabled()
        {
            SyncModeSelector selector = BuildSelector(true);
            Assert.AreEqual(SyncMode.NotStarted, selector.Current);
        }

        [Test]
        public void Starts_with_not_started()
        {
            SyncModeSelector selector = BuildSelector(false);
            Assert.AreEqual(SyncMode.NotStarted, selector.Current);
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
            syncConfig.PivotNumber = null;
            syncConfig.PivotHash = null;
            
            ISyncProgressResolver syncProgressResolver = Substitute.For<ISyncProgressResolver>();
            
            SyncModeSelector selector = new SyncModeSelector(syncProgressResolver, syncPeerPool, syncConfig, LimboLogs.Instance);
            Assert.AreEqual(SyncMode.NotStarted, selector.Current);

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
                var testCase = states[i];
                syncProgressResolver.FindBestFullState().Returns(testCase.BestLocalState);
                syncProgressResolver.FindBestHeader().Returns(testCase.BestLocalHeader);
                syncProgressResolver.FindBestFullBlock().Returns(testCase.BestLocalFullBlock);
                syncProgressResolver.IsFastBlocksFinished().Returns(true);
                
                Assert.GreaterOrEqual(testCase.BestLocalHeader, testCase.BestLocalState, "checking if the test case is correct - local state always less then local header");
                Assert.GreaterOrEqual(testCase.BestLocalHeader, testCase.BestLocalFullBlock, "checking if the test case is correct - local full block always less then local header");
                peerInfo1.HeadNumber = testCase.BestRemote;
                selector.Update();
                Assert.AreEqual(testCase.ExpectedState, selector.Current, testCase.Description);    
            }
        }

        [TestCase(true, 1032, 999, 0, 0, SyncMode.Headers)]
        [TestCase(false, 1032, 1000, 0, 0, SyncMode.Full)]
        [TestCase(true, 1032, 1000, 0, 0, SyncMode.StateNodes)]
        [TestCase(true, 1032, 1000, 0, 1000, SyncMode.Full)]
        [TestCase(true, 0, 1032, 0, 1032, SyncMode.Full)]
        [TestCase(false, 0, 1032, 0, 1032, SyncMode.Full)]
        [TestCase(true, 4506571, 4506571, 4506571, 4506452, SyncMode.Full)]
        public void Selects_correctly(bool useFastSync, long bestRemote, long bestHeader, long bestBlock, long bestLocalState, SyncMode expected)
        {
            bool changedInvoked = false;

            SyncModeSelector selector = BuildSelector(useFastSync, bestRemote, bestHeader, bestBlock, bestLocalState);
            selector.Changed += (s, e) => changedInvoked = true;

            SyncMode beforeUpdate = selector.Current;

            selector.Update();
            Assert.AreEqual(expected, selector.Current, "as expected");
            if (expected != beforeUpdate)
            {
                Assert.True(changedInvoked, "changed");
            }
        }

        [TestCase(true, 1032, 0, 0, 0, SyncMode.NotStarted)]
        [TestCase(false, 1032, 0, 0, 0, SyncMode.NotStarted)]
        [TestCase(true, 1032, 1000, 0, 0, SyncMode.NotStarted)]
        [TestCase(false, 1032, 1000, 0, 0, SyncMode.NotStarted)]
        [TestCase(true, 1032, 1000, 0, 1000, SyncMode.NotStarted)]
        [TestCase(false, 1032, 1000, 0, 1000, SyncMode.NotStarted)]
        public void Does_not_change_when_no_peers(bool useFastSync, long bestRemote, long bestLocalHeader, long bestLocalFullBLock, long bestLocalState, SyncMode expected)
        {
            SyncModeSelector selector = BuildSelectorNoPeers(useFastSync, bestRemote,  bestLocalHeader, bestLocalFullBLock, bestLocalState);
            selector.Update();
            Assert.AreEqual(expected, selector.Current);
        }

        private static SyncModeSelector BuildSelector(bool fastSyncEnabled, long bestRemote = 0L, long bestHeader = 0L, long bestBlock = 0L, long bestLocalState = 0L)
        {
            IEthSyncPeerPool syncPeerPool = Substitute.For<IEthSyncPeerPool>();
            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            syncPeer.TotalDifficultyOnSessionStart.Returns((UInt256) (1024 * 1024));

            PeerInfo peerInfo1 = new PeerInfo(syncPeer) {HeadNumber = bestRemote, IsInitialized = true};
            PeerInfo peerInfo2 = new PeerInfo(syncPeer) {HeadNumber = bestRemote, IsInitialized = true};
            PeerInfo peerInfo3 = new PeerInfo(syncPeer) {HeadNumber = 0, IsInitialized = true};
            PeerInfo peerInfo4 = new PeerInfo(syncPeer) {HeadNumber = bestRemote * 2, IsInitialized = false};
            syncPeerPool.AllPeers.Returns(new[] {peerInfo1, peerInfo2, peerInfo3, peerInfo4});
            syncPeerPool.UsefulPeers.Returns(new[] {peerInfo1, peerInfo2});
            syncPeerPool.PeerCount.Returns(3);

            SyncConfig syncConfig = new SyncConfig();
            syncConfig.FastSync = fastSyncEnabled;

            ISyncProgressResolver syncProgressResolver = Substitute.For<ISyncProgressResolver>();
            syncProgressResolver.FindBestHeader().Returns(bestHeader);
            syncProgressResolver.FindBestFullBlock().Returns(bestBlock);
            syncProgressResolver.FindBestFullState().Returns(bestLocalState);
            syncProgressResolver.IsFastBlocksFinished().Returns(true);
            
            SyncModeSelector selector = new SyncModeSelector(syncProgressResolver, syncPeerPool, syncConfig, LimboLogs.Instance);
            return selector;
        }

        private static SyncModeSelector BuildSelectorNoPeers(bool useFastSync, long bestRemote = 0L, long bestHeader = 0L, long bestBlock = 0L, long bestLocalState = 0L)
        {
            IEthSyncPeerPool syncPeerPool = Substitute.For<IEthSyncPeerPool>();
            syncPeerPool.AllPeers.Returns(new PeerInfo[] { });
            syncPeerPool.PeerCount.Returns(0);

            SyncConfig syncConfig = new SyncConfig();
            syncConfig.FastSync = !useFastSync;

            ISyncProgressResolver syncProgressResolver = Substitute.For<ISyncProgressResolver>();
            syncProgressResolver.FindBestHeader().Returns(bestHeader);
            syncProgressResolver.FindBestFullBlock().Returns(bestBlock);
            syncProgressResolver.FindBestFullState().Returns(bestLocalState);
            syncProgressResolver.IsFastBlocksFinished().Returns(true);

            SyncModeSelector selector = new SyncModeSelector(syncProgressResolver, syncPeerPool, syncConfig, LimboLogs.Instance);
            return selector;
        }
    }
}