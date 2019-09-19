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

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Synchronization.FastBlocks;
using Nethermind.Blockchain.TxPools;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Store;
using Nethermind.Store.Repositories;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Synchronization.FastBlocks
{
    [TestFixture]
    [SingleThreaded]
    public class FastBlocksFeedTests
    {
        private ISpecProvider _specProvider = MainNetSpecProvider.Instance;
        private InMemoryReceiptStorage _remoteReceiptStorage;
        private BlockTree _validTree2048;
        private BlockTree _validTree1024;
        private BlockTree _validTree8;
        private BlockTree _badTreeAfter1024;
        private List<LatencySyncPeerMock> _syncPeers;
        private Dictionary<LatencySyncPeerMock, int> _peerMaxResponseSizes;
        private Dictionary<LatencySyncPeerMock, HashSet<long>> _invalidBlocks;
        private Dictionary<LatencySyncPeerMock, IBlockTree> _peerTrees;
        private Dictionary<LatencySyncPeerMock, FastBlocksBatch> _pendingResponses;
        private Dictionary<long, Action> _scheduledActions;
        private HashSet<LatencySyncPeerMock> _maliciousByRepetition;
        private HashSet<LatencySyncPeerMock> _maliciousByInvalidReceipts;
        private HashSet<LatencySyncPeerMock> _maliciousByInvalidTxs;
        private HashSet<LatencySyncPeerMock> _maliciousByInvalidOmmers;
        private HashSet<LatencySyncPeerMock> _maliciousByShiftedOneForward;
        private HashSet<LatencySyncPeerMock> _maliciousByShiftedOneBack;
        private HashSet<LatencySyncPeerMock> _maliciousByShortAtStart;
        private HashSet<LatencySyncPeerMock> _incorrectByTooShortMessages;
        private HashSet<LatencySyncPeerMock> _incorrectByTooLongMessages;
        private HashSet<LatencySyncPeerMock> _timingOut;
        private InMemoryReceiptStorage _localReceiptStorage;
        private BlockTree _localBlockTree;
        private FastBlocksFeed _feed;
        private long _time;
        private int _timeoutTime = 5000;

        private IEthSyncPeerPool _syncPeerPool;
        private SyncConfig _syncConfig;

        public FastBlocksFeedTests()
        {
            // make trees lazy
            _remoteReceiptStorage = new InMemoryReceiptStorage();
            _validTree2048 = Build.A.BlockTree().WithTransactions(_remoteReceiptStorage, _specProvider).OfChainLength(2048).TestObject;
            _validTree1024 = Build.A.BlockTree().WithTransactions(_remoteReceiptStorage, _specProvider).OfChainLength(1024).TestObject;
            _validTree8 = Build.A.BlockTree().WithTransactions(_remoteReceiptStorage, _specProvider).OfChainLength(8).TestObject;
            _badTreeAfter1024 = Build.A.BlockTree().WithTransactions(_remoteReceiptStorage, _specProvider).OfChainLength(2048, 1, 1024).TestObject;
        }

        [SetUp]
        public void Setup()
        {
            _localReceiptStorage = new InMemoryReceiptStorage();
            _syncPeers = new List<LatencySyncPeerMock>();
            _peerTrees = new Dictionary<LatencySyncPeerMock, IBlockTree>();
            _peerMaxResponseSizes = new Dictionary<LatencySyncPeerMock, int>();
            _pendingResponses = new Dictionary<LatencySyncPeerMock, FastBlocksBatch>();
            _invalidBlocks = new Dictionary<LatencySyncPeerMock, HashSet<long>>();
            _maliciousByRepetition = new HashSet<LatencySyncPeerMock>();
            _maliciousByInvalidTxs = new HashSet<LatencySyncPeerMock>();
            _maliciousByInvalidOmmers = new HashSet<LatencySyncPeerMock>();
            _maliciousByShiftedOneForward = new HashSet<LatencySyncPeerMock>();
            _maliciousByShiftedOneBack = new HashSet<LatencySyncPeerMock>();
            _maliciousByShortAtStart = new HashSet<LatencySyncPeerMock>();
            _maliciousByInvalidReceipts = new HashSet<LatencySyncPeerMock>();
            _incorrectByTooShortMessages = new HashSet<LatencySyncPeerMock>();
            _incorrectByTooLongMessages = new HashSet<LatencySyncPeerMock>();
            _timingOut = new HashSet<LatencySyncPeerMock>();
            _scheduledActions = new Dictionary<long, Action>();

            LatencySyncPeerMock.RemoteIndex = 1;
            _time = 0;
            _syncPeerPool = Substitute.For<IEthSyncPeerPool>();
            _syncPeerPool.WhenForAnyArgs(p => p.ReportNoSyncProgress(Arg.Any<SyncPeerAllocation>()))
                .Do(ci =>
                {
                    LatencySyncPeerMock mock = ((LatencySyncPeerMock) ci.Arg<SyncPeerAllocation>().Current.SyncPeer);
                    mock.BusyUntil = _time + 5000;
                    mock.IsReported = true;
                });

            _syncPeerPool.WhenForAnyArgs(p => p.ReportNoSyncProgress(Arg.Any<PeerInfo>()))
                .Do(ci =>
                {
                    LatencySyncPeerMock mock = ((LatencySyncPeerMock) ci.Arg<PeerInfo>().SyncPeer);
                    mock.BusyUntil = _time + 5000;
                    mock.IsReported = true;
                });

            _syncPeerPool.WhenForAnyArgs(p => p.ReportInvalid(Arg.Any<SyncPeerAllocation>()))
                .Do(ci =>
                {
                    LatencySyncPeerMock mock = ((LatencySyncPeerMock) ci.Arg<SyncPeerAllocation>().Current.SyncPeer);
                    mock.BusyUntil = _time + 30000;
                    mock.IsReported = true;
                });

            _syncPeerPool.WhenForAnyArgs(p => p.ReportInvalid(Arg.Any<PeerInfo>()))
                .Do(ci =>
                {
                    LatencySyncPeerMock mock = ((LatencySyncPeerMock) ci.Arg<PeerInfo>().SyncPeer);
                    mock.BusyUntil = _time + 30000;
                    mock.IsReported = true;
                });

            _syncPeerPool.AllPeers.Returns((ci) => _syncPeers.Select(sp => new PeerInfo(sp) {HeadNumber = sp.Tree.Head.Number}));

            _syncConfig = new SyncConfig();
            _syncConfig.PivotHash = _validTree2048.Head.Hash.ToString();
            _syncConfig.PivotNumber = _validTree2048.Head.Number.ToString();
            _syncConfig.PivotTotalDifficulty = _validTree2048.Head.TotalDifficulty.ToString();

            SetupLocalTree();
            SetupFeed();
        }

        private void SetupLocalTree(int length = 1)
        {
            var blockInfoDb = new MemDb();
            _localBlockTree = new BlockTree(new MemDb(), new MemDb(), blockInfoDb, new ChainLevelInfoRepository(blockInfoDb), MainNetSpecProvider.Instance, NullTxPool.Instance, _syncConfig, LimboLogs.Instance);
            for (int i = 0; i < length; i++)
            {
                _localBlockTree.SuggestBlock(_validTree2048.FindBlock(i, BlockTreeLookupOptions.None));
            }
        }

        private void SetupFeed(bool syncBodies = false, bool syncReceipts = false)
        {
            _syncConfig.DownloadBodiesInFastSync = syncBodies;
            _syncConfig.DownloadReceiptsInFastSync = syncReceipts;

            _feed = new FastBlocksFeed(_specProvider, _localBlockTree, _localReceiptStorage, _syncPeerPool, _syncConfig, NullSyncReport.Instance, LimboLogs.Instance);
        }

        [Test]
        public void One_peer_with_valid_chain_bodies_restarting()
        {
            LatencySyncPeerMock syncPeer = new LatencySyncPeerMock(_validTree2048);
            SetupFeed(true);
            SetupSyncPeers(syncPeer);
            RunFeed(5000, 9);
            Assert.AreEqual(78, _time);

            AssertTreeSynced(_validTree2048, true);
        }

        [Test]
        public void One_peer_with_valid_chain_bodies()
        {
            LatencySyncPeerMock syncPeer = new LatencySyncPeerMock(_validTree2048);
            SetupFeed(true);
            SetupSyncPeers(syncPeer);
            RunFeed();
            Assert.AreEqual(48, _time);

            AssertTreeSynced(_validTree2048, true);
        }

        [Test]
        public void One_peer_with_valid_chain()
        {
            LatencySyncPeerMock syncPeer = new LatencySyncPeerMock(_validTree2048);
            SetupSyncPeers(syncPeer);
            RunFeed();
            Assert.AreEqual(24, _time);

            AssertTreeSynced(_validTree2048);
        }

        [Test]
        public void Two_peers_with_valid_chain_bodies()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048);
            SetupFeed(true);
            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(26, _time);

            AssertTreeSynced(_validTree2048, true);
        }

        [Test]
        public void Two_peers_with_valid_chain()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048);
            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(13, _time);

            AssertTreeSynced(_validTree2048);
        }

        [Test]
        public void Two_peers_with_valid_chain_and_various_max_response_sizes_bodies()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048);
            SetupFeed(true);
            _peerMaxResponseSizes[syncPeer1] = 100;
            _peerMaxResponseSizes[syncPeer2] = 75;

            SetupSyncPeers(syncPeer1, syncPeer2);

            RunFeed();
            Assert.AreEqual(170, _time);

            AssertTreeSynced(_validTree2048, true);
        }

        [Test]
        public void Two_peers_with_valid_chain_and_various_max_response_sizes()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048);
            _peerMaxResponseSizes[syncPeer1] = 100;
            _peerMaxResponseSizes[syncPeer2] = 75;

            SetupSyncPeers(syncPeer1, syncPeer2);

            RunFeed();
            Assert.AreEqual(85, _time);

            AssertTreeSynced(_validTree2048);
        }

        [Test]
        public void Two_peers_one_malicious_by_repetition()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048);
            _maliciousByRepetition.Add(syncPeer1);

            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(25, _time);

            AssertTreeSynced(_validTree2048);
        }

        [Test]
        public void Two_peers_one_malicious_by_invalid_txs()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048);
            _maliciousByInvalidTxs.Add(syncPeer1);
            SetupFeed(true);

            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(38, _time);

            AssertTreeSynced(_validTree2048, true);
        }

        [Test]
        public void Two_peers_one_malicious_by_invalid_ommers()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048);
            _maliciousByInvalidOmmers.Add(syncPeer1);
            SetupFeed(true);

            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(38, _time);

            AssertTreeSynced(_validTree2048, true);
        }

        [Test]
        public void Two_peers_one_malicious_by_short_at_start()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048);
            _maliciousByShortAtStart.Add(syncPeer1);

            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(25, _time);

            AssertTreeSynced(_validTree2048);
        }

        [Test]
        public void Two_peers_one_malicious_by_short_at_start_bodies()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048);
            _maliciousByShortAtStart.Add(syncPeer1);
            SetupFeed(true);

            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(49, _time);

            AssertTreeSynced(_validTree2048, true);
        }

        [Test]
        public void Two_peers_one_malicious_by_shift_forward()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048);
            _maliciousByShiftedOneForward.Add(syncPeer1);

            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(25, _time);

            AssertTreeSynced(_validTree2048);
        }

        [Test]
        public void Two_peers_one_malicious_by_shift_forward_bodies()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048);
            _maliciousByShiftedOneForward.Add(syncPeer1);
            SetupFeed(true);

            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(49, _time);

            AssertTreeSynced(_validTree2048, true);
        }

        [Test]
        public void Two_peers_one_malicious_by_shift_back()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048);
            _maliciousByShiftedOneBack.Add(syncPeer1);

            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(25, _time);

            AssertTreeSynced(_validTree2048);
        }

        [Test]
        public void Two_peers_one_malicious_by_shift_back_bodies()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048);
            _maliciousByShiftedOneBack.Add(syncPeer1);
            SetupFeed(true);

            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(49, _time);

            AssertTreeSynced(_validTree2048, true);
        }

        [Test]
        public void Two_peers_one_sending_too_short_messages()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048);
            _incorrectByTooShortMessages.Add(syncPeer1);

            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(60, _time);

            AssertTreeSynced(_validTree2048);
        }

        [Test]
        public void Two_peers_one_sending_too_short_messages_bodies()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048);
            _incorrectByTooShortMessages.Add(syncPeer1);
            SetupFeed(true);

            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(120, _time);

            AssertTreeSynced(_validTree2048, true);
        }

        [Test]
        public void Two_peers_one_sending_too_long_messages()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048);
            _incorrectByTooLongMessages.Add(syncPeer1);

            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(25, _time);

            AssertTreeSynced(_validTree2048);
        }

        [Test]
        public void Two_peers_one_sending_too_long_messages_bodies()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048);
            _incorrectByTooLongMessages.Add(syncPeer1);
            SetupFeed(true);

            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(49, _time);

            AssertTreeSynced(_validTree2048, true);
        }

        [Test]
        public void Two_peers_one_timing_out()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048);
            _timingOut.Add(syncPeer1);

            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed(20000);
            Assert.AreEqual(5007, _time);

            AssertTreeSynced(_validTree2048);
        }

        [Test]
        public void Two_peers_one_timing_out_bodies()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048);
            _timingOut.Add(syncPeer1);
            SetupFeed(true);

            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed(20000);
            Assert.AreEqual(5031, _time);

            AssertTreeSynced(_validTree2048, true);
        }

        [Test]
        public void One_peer_with_valid_one_with_invalid_A()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_badTreeAfter1024);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048, 300);

            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(1205, _time);

            AssertTreeSynced(_validTree2048);
        }

        [Test]
        public void One_peer_with_valid_one_with_invalid_A_bodies()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_badTreeAfter1024);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048, 300);
            SetupFeed(true);

            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(2409, _time);

            AssertTreeSynced(_validTree2048, true);
        }

        [Test]
        public void One_peer_with_valid_one_with_invalid_B()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048, 300);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_badTreeAfter1024);

            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(602, _time);

            AssertTreeSynced(_validTree2048);
        }

        [Test]
        public void One_peer_with_valid_one_with_invalid_B_bodies()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048, 300);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_badTreeAfter1024);
            SetupFeed(true);

            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(1806, _time);

            AssertTreeSynced(_validTree2048, true);
        }

        [Test(Description = "Test if bodies dependencies are handled correctly")]
        public void Two_valid_one_slower_with_bodies_and_one_restart()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048, 300);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048);
            SetupFeed(true);

            SetupSyncPeers(syncPeer1, syncPeer2);
            _scheduledActions[312] = ResetAndStartNewRound;

            RunFeed();
            Assert.AreEqual(613, _time);

            AssertTreeSynced(_validTree2048, true);
        }

        [Test(Description = "Test if receipt dependencies are handled correctly")]
        public void Two_valid_one_slower_with_receipts_and_one_restart()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048, 300);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048);
            SetupFeed(true, true);

            SetupSyncPeers(syncPeer1, syncPeer2);
            _scheduledActions[906] = ResetAndStartNewRound;

            RunFeed(5000);
//            Assert.AreEqual(2116, _time);

            AssertTreeSynced(_validTree2048, true, true);
        }

        [Test]
        public void One_valid_one_malicious_with_receipts_and_one_restart()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048, 300);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree2048);
            SetupFeed(true, true);

            _maliciousByInvalidReceipts.Add(syncPeer2);
            SetupSyncPeers(syncPeer1, syncPeer2);

            RunFeed(5000);
//            Assert.AreEqual(2116, _time);

            AssertTreeSynced(_validTree2048, true, true);
        }

        [Test]
        public void Two_peers_with_valid_chain_one_shorter()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree1024);
            SetupSyncPeers(syncPeer1, syncPeer2);
            RunFeed();
            Assert.AreEqual(18, _time);

            AssertTreeSynced(_validTree2048);
        }

        [Test]
        public void Two_peers_with_valid_chain_one_shorter_bodies()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree1024);
            SetupSyncPeers(syncPeer1, syncPeer2);
            SetupFeed(true);
            RunFeed();
            Assert.AreEqual(36, _time);

            AssertTreeSynced(_validTree2048, true);
        }

        [Test]
        public void Short_chain()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree1024);
            SetupSyncPeers(syncPeer1, syncPeer2);

            _syncConfig.PivotHash = _validTree8.Head.Hash.Bytes.ToHexString();
            _syncConfig.PivotNumber = _validTree8.Head.Number.ToString();
            _syncConfig.PivotTotalDifficulty = _validTree8.Head.Difficulty.ToString();

            RunFeed();
            Assert.AreEqual(6, _time);

            AssertTreeSynced(_validTree8);
        }

        [Test]
        public void Short_chain_bodies()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048);
            LatencySyncPeerMock syncPeer2 = new LatencySyncPeerMock(_validTree1024);
            SetupSyncPeers(syncPeer1, syncPeer2);
            SetupFeed(true);

            _syncConfig.PivotHash = _validTree8.Head.Hash.Bytes.ToHexString();
            _syncConfig.PivotNumber = _validTree8.Head.Number.ToString();
            _syncConfig.PivotTotalDifficulty = _validTree8.Head.Difficulty.ToString();
            RunFeed();
            Assert.AreEqual(12, _time);

            AssertTreeSynced(_validTree8, true);
        }

        [Test]
        public void Shorter_responses()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048);
            SetupSyncPeers(syncPeer1);

            _incorrectByTooShortMessages.Add(syncPeer1);

            RunFeed();

            Assert.AreEqual(240, _time);

            AssertTreeSynced(_validTree2048);
        }

        [Test]
        public void Shorter_responses_bodies()
        {
            LatencySyncPeerMock syncPeer1 = new LatencySyncPeerMock(_validTree2048);
            SetupSyncPeers(syncPeer1);
            SetupFeed(true);

            _incorrectByTooShortMessages.Add(syncPeer1);

            RunFeed();

            Assert.AreEqual(480, _time);

            AssertTreeSynced(_validTree2048, true);
        }

        private void AssertTreeSynced(IBlockTree tree, bool bodiesSync = false, bool receiptSync = false)
        {
            Keccak nextHash = tree.Head.Hash;
            for (int i = 0; i < tree.Head.Number; i++)
            {
                BlockHeader header = _localBlockTree.FindHeader(nextHash, BlockTreeLookupOptions.None);
                Assert.NotNull(header, $"header {tree.Head.Number - i}");
                if (bodiesSync)
                {
                    Block expectedBlock = _localBlockTree.FindBlock(nextHash, BlockTreeLookupOptions.None);
                    Assert.AreEqual(nextHash, expectedBlock?.Hash, $"hash difference {tree.Head.Number - i}");
                    if (expectedBlock != null)
                    {
                        Block actualBlock = tree.FindBlock(expectedBlock.Hash, BlockTreeLookupOptions.None);
                        Rlp saved = Rlp.Encode(actualBlock);
                        Rlp expected = Rlp.Encode(expectedBlock);
                        Assert.AreEqual(expected, saved, $"body {tree.Head.Number - i}");

                        if (receiptSync)
                        {
                            int txIndex = 0;
                            foreach (Transaction transaction in expectedBlock.Transactions)
                            {
                                Assert.NotNull(_localReceiptStorage.Find(transaction.Hash), $"receipt {expectedBlock.Number}.{txIndex}");
                                txIndex++;
                            }
                        }
                    }
                }

                nextHash = header.ParentHash;
            }

            Assert.True(_feed.IsFinished);
        }

        private void RunFeed(int timeLimit = 5000, int restartEvery = 100000)
        {
            _feed.StartNewRound();
            while (true)
            {
                if (_scheduledActions.ContainsKey(_time))
                {
                    _scheduledActions[_time].Invoke();
                }

                if (_time % restartEvery == 0)
                {
                    ResetAndStartNewRound();
                }

                if (_time > timeLimit)
                {
                    TestContext.WriteLine($"TIMEOUT AT {_time}");
                    break;
                }

                if (_pendingResponses.Count < _syncPeers.Count(p => !p.IsReported))
                {
                    FastBlocksBatch batch = _feed.PrepareRequest();
                    if (batch == null && _pendingResponses.Count == 0)
                    {
                        TestContext.WriteLine($"STOP - NULL BATCH AND NO PENDING");
                        break;
                    }

                    if (batch != null)
                    {
                        bool wasAssigned = false;
                        foreach (LatencySyncPeerMock syncPeer in _syncPeers)
                        {
                            if (syncPeer.BusyUntil == null
                                && _peerTrees[syncPeer].Head.Number >= (batch.MinNumber ?? 0))
                            {
                                syncPeer.BusyUntil = _time + syncPeer.Latency;
                                if (_timingOut.Contains(syncPeer))
                                {
                                    syncPeer.BusyUntil = _time + _timeoutTime;
                                }

                                batch.Allocation = new SyncPeerAllocation(new PeerInfo(syncPeer), "test");
                                _pendingResponses.Add(syncPeer, batch);
                                TestContext.WriteLine($"{_time,6} |SENDING {batch} REQUEST TO {syncPeer.Node:s}");
                                wasAssigned = true;
                                break;
                            }
                        }

                        if (!wasAssigned)
                        {
//                            TestContext.WriteLine($"{_time,6} | {batch} WAS NOT ASSIGNED");
                            _feed.HandleResponse(batch);
                        }
                    }
                }

                foreach (LatencySyncPeerMock intSyncPeerMock in _syncPeers)
                {
                    if (intSyncPeerMock.BusyUntil == _time)
                    {
                        intSyncPeerMock.BusyUntil = null;
                        FastBlocksBatch responseBatch = CreateResponse(intSyncPeerMock);
                        if (responseBatch != null)
                        {
                            _feed.HandleResponse(responseBatch);
                        }
                    }
                }

                _time++;
            }
        }

        private void ResetAndStartNewRound()
        {
            TestContext.WriteLine("RESET AND START NEW ROUND");
            _pendingResponses.Clear();
            _syncPeers.ForEach(p => p.BusyUntil = null);
            _feed.StartNewRound();
        }

        private void SetupSyncPeers(params LatencySyncPeerMock[] syncPeers)
        {
            foreach (LatencySyncPeerMock latencySyncPeerMock in syncPeers)
            {
                _syncPeers.Add(latencySyncPeerMock);
                _peerTrees[latencySyncPeerMock] = latencySyncPeerMock.Tree;
            }
        }

        private FastBlocksBatch CreateResponse(LatencySyncPeerMock syncPeer)
        {
            if (!_pendingResponses.ContainsKey(syncPeer))
            {
                TestContext.WriteLine($"{_time,6} |SYNC PEER {syncPeer.Node:s} WAKES UP");
                syncPeer.IsReported = false;
                return null;
            }

            FastBlocksBatch responseBatch = _pendingResponses[syncPeer];
            IBlockTree tree = _peerTrees[syncPeer];
            _pendingResponses.Remove(syncPeer);

            if (_timingOut.Contains(syncPeer))
            {
                TestContext.WriteLine($"{_time,6} |SYNC PEER {syncPeer.Node:s} TIMED OUT");
                // timeout punishment
                syncPeer.BusyUntil = _time + 5000;
                syncPeer.IsReported = true;
                return responseBatch;
            }

            TestContext.WriteLine($"{_time,6} |SYNC PEER {syncPeer.Node:s} RESPONDING TO {responseBatch}");

            switch (responseBatch.BatchType)
            {
                case FastBlocksBatchType.None:
                    break;
                case FastBlocksBatchType.Headers:
                    var headersSyncBatch = responseBatch.Headers;
                    PrepareHeadersResponse(headersSyncBatch, syncPeer, tree);
                    break;
                case FastBlocksBatchType.Bodies:
                    var bodiesSyncBatch = responseBatch.Bodies;
                    PrepareBodiesResponse(bodiesSyncBatch, syncPeer, tree);
                    break;
                case FastBlocksBatchType.Receipts:
                    var receiptSyncBatch = responseBatch.Receipts;
                    PrepareReceiptsResponse(receiptSyncBatch, syncPeer, tree);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            return responseBatch;
        }

        private void PrepareReceiptsResponse(ReceiptsSyncBatch receiptSyncBatch, LatencySyncPeerMock syncPeer, IBlockTree tree)
        {
            receiptSyncBatch.Response = new TxReceipt[receiptSyncBatch.Request.Length][];
            for (int i = 0; i < receiptSyncBatch.Request.Length; i++)
            {
                Block block = tree.FindBlock(receiptSyncBatch.Request[i], BlockTreeLookupOptions.None);
                receiptSyncBatch.Response[i] = new TxReceipt[block.Transactions.Length];
                for (int j = 0; j < block.Transactions.Length; j++)
                {
                    receiptSyncBatch.Response[i][j] = _remoteReceiptStorage.Find(block.Transactions[j].Hash);

                    if (i < 10 && j == 0 && _maliciousByInvalidReceipts.Contains(syncPeer))
                    {
                        receiptSyncBatch.Response[i][j] = new TxReceipt();
                        receiptSyncBatch.Response[i][j].Logs = new LogEntry[0];
                        receiptSyncBatch.Response[i][j].StatusCode = (byte) (1 - receiptSyncBatch.Response[i][j].StatusCode);
                        receiptSyncBatch.Response[i][j].PostTransactionState = Keccak.Compute(receiptSyncBatch.Response[i][j].PostTransactionState?.Bytes ?? new byte[] {1});
                    }
                }
            }
        }

        private void PrepareBodiesResponse(BodiesSyncBatch bodiesSyncBatch, LatencySyncPeerMock syncPeer, IBlockTree tree)
        {
            int requestSize = bodiesSyncBatch.Request.Length;
            int responseSize = bodiesSyncBatch.Request.Length;
            if (_incorrectByTooLongMessages.Contains(syncPeer))
            {
                responseSize *= 2;
                TestContext.WriteLine($"{_time,6} | SYNC PEER {syncPeer.Node:s} WILL SEND TOO LONG MESSAGE ({responseSize} INSTEAD OF {requestSize})");
            }
            else if (_incorrectByTooShortMessages.Contains(syncPeer))
            {
                responseSize = Math.Max(1, responseSize / 2);
                TestContext.WriteLine($"{_time,6} | SYNC PEER {syncPeer.Node:s} WILL SEND TOO SHORT MESSAGE ({responseSize} INSTEAD OF {requestSize})");
            }

            bodiesSyncBatch.Response = new BlockBody[responseSize];
            int maxResponseSize = _peerMaxResponseSizes.ContainsKey(syncPeer) ? Math.Min(responseSize, _peerMaxResponseSizes[syncPeer]) : responseSize;

            for (int i = 0; i < Math.Min(maxResponseSize, requestSize); i++)
            {
                Block block = tree.FindBlock(bodiesSyncBatch.Request[i], BlockTreeLookupOptions.None);
                bodiesSyncBatch.Response[i] = new BlockBody(block.Transactions, block.Ommers);
            }

            if (_maliciousByShortAtStart.Contains(syncPeer))
            {
                bodiesSyncBatch.Response[0] = null;
                TestContext.WriteLine($"{_time,6} | SYNC PEER {syncPeer.Node:s} WILL SEND A MALICIOUS (SHORT AT START) MESSAGE");
            }

            if (_maliciousByInvalidTxs.Contains(syncPeer))
            {
                for (int i = 0; i < bodiesSyncBatch.Response.Length; i++)
                {
                    BlockBody valid = bodiesSyncBatch.Response[i];
                    bodiesSyncBatch.Response[i] = new BlockBody(new[] {Build.A.Transaction.WithData(Bytes.FromHexString("bad")).TestObject}, valid.Ommers);
                }
            }

            if (_maliciousByInvalidOmmers.Contains(syncPeer))
            {
                for (int i = 0; i < bodiesSyncBatch.Response.Length; i++)
                {
                    BlockBody valid = bodiesSyncBatch.Response[i];
                    bodiesSyncBatch.Response[i] = new BlockBody(valid.Transactions, new[] {Build.A.BlockHeader.WithAuthor(new Address(Keccak.Compute("bad_ommer").Bytes.Take(20).ToArray())).TestObject});
                }
            }
        }

        private void PrepareHeadersResponse(HeadersSyncBatch headersSyncBatch, LatencySyncPeerMock syncPeer, IBlockTree tree)
        {
            if (headersSyncBatch != null)
            {
                long startNumber = headersSyncBatch.StartNumber;
                if (_maliciousByShiftedOneBack.Contains(syncPeer))
                {
                    startNumber++;
                    TestContext.WriteLine($"{_time,6} | SYNC PEER {syncPeer.Node:s} WILL SEND SHIFTED MESSAGES ({startNumber} INSTEAD OF {headersSyncBatch.StartNumber})");
                }
                else if (_maliciousByShiftedOneForward.Contains(syncPeer))
                {
                    startNumber = Math.Max(0, startNumber - 1);
                    TestContext.WriteLine($"{_time,6} | SYNC PEER {syncPeer.Node:s} WILL SEND SHIFTED MESSAGES ({startNumber} INSTEAD OF {headersSyncBatch.StartNumber})");
                }

                Keccak hash = tree.FindHash(startNumber);

                if (hash == null)
                {
                    TestContext.WriteLine($"{_time,6} | SYNC PEER {syncPeer.Node:s} CANNOT FIND {headersSyncBatch.StartNumber}");
                    return;
                }

                int requestSize = headersSyncBatch.RequestSize;
                if (_incorrectByTooLongMessages.Contains(syncPeer))
                {
                    requestSize *= 2;
                    TestContext.WriteLine($"{_time,6} | SYNC PEER {syncPeer.Node:s} WILL SEND TOO LONG MESSAGE ({requestSize} INSTEAD OF {headersSyncBatch.RequestSize})");
                }
                else if (_incorrectByTooShortMessages.Contains(syncPeer))
                {
                    requestSize = Math.Max(1, requestSize / 2);
                    TestContext.WriteLine($"{_time,6} | SYNC PEER {syncPeer.Node:s} WILL SEND TOO SHORT MESSAGE ({requestSize} INSTEAD OF {headersSyncBatch.RequestSize})");
                }

                BlockHeader[] headers = tree.FindHeaders(hash, requestSize, 0, false);
                if (_invalidBlocks.ContainsKey(syncPeer))
                {
                    for (int i = 0; i < headers.Length; i++)
                    {
                        if (_invalidBlocks[syncPeer].Contains(headers[i].Number))
                        {
                            TestContext.WriteLine($"{_time,6} | SYNC PEER {syncPeer.Node:s} WILL SEND AN INVALID BLOCK AT {headers[i].Number}");
                            headers[i] = Build.A.Block.WithDifficulty(1).TestObject.Header;
                        }
                    }
                }

                if (headers.Length > 3 && _maliciousByRepetition.Contains(syncPeer))
                {
                    headers[headers.Length - 1] = headers[headers.Length - 3];
                    headers[headers.Length - 2] = headers[headers.Length - 3];
                    TestContext.WriteLine($"{_time,6} | SYNC PEER {syncPeer.Node:s} WILL SEND A MALICIOUS (REPEATED) MESSAGE");
                }

                if (_maliciousByShortAtStart.Contains(syncPeer))
                {
                    headers[0] = null;
                    TestContext.WriteLine($"{_time,6} | SYNC PEER {syncPeer.Node:s} WILL SEND A MALICIOUS (SHORT AT START) MESSAGE");
                }


                headersSyncBatch.Response = headers;
                if (_peerMaxResponseSizes.ContainsKey(syncPeer))
                {
                    int maxResponseSize = _peerMaxResponseSizes[syncPeer];
                    TestContext.WriteLine($"{_time,6} | SYNC PEER {syncPeer.Node:s} WILL SEND NULLS AFTER INDEX {maxResponseSize}");
                    for (int i = 0; i < headers.Length; i++)
                    {
                        if (i >= maxResponseSize)
                        {
                            headers[i] = null;
                        }
                    }
                }
            }
        }
    }
}