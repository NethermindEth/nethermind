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
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.ParallelSync
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class MultiSyncModeSelectorTests
    {
        public static class Scenario
        {
            public const long Pivot = 1024;

            public const long FastSyncCatchUpHeightDelta = 64;

            public static BlockHeader ChainHead { get; set; } = Build.A.Block.WithTotalDifficulty(Pivot * 10).WithNumber(Pivot * 10).TestObject.Header;

            public static BlockHeader ValidGenesis { get; set; } = Build.A.Block.WithDifficulty(1).WithTotalDifficulty(UInt256.One).Genesis.TestObject.Header;

            public static BlockHeader InvalidGenesis { get; set; } = Build.A.Block.WithDifficulty(1).WithTotalDifficulty(UInt256.One).Genesis.TestObject.Header;

            public static BlockHeader InvalidGenesisWithHighTotalDifficulty { get; set; } = Build.A.Block.Genesis.WithDifficulty((UInt256) 1000000).WithTotalDifficulty((UInt256) 1000000).TestObject.Header;

            public class ScenarioBuilder
            {
                private List<Action> _configActions = new List<Action>();

                private List<Action> _actionsOnDefaults = new List<Action>();

                public ISyncPeerPool SyncPeerPool { get; set; }

                public ISyncProgressResolver SyncProgressResolver { get; set; }

                public ISyncConfig SyncConfig { get; set; } = new SyncConfig();

                public ScenarioBuilder()
                {
                }

                private void SetDefaults()
                {
                    SyncPeerPool = Substitute.For<ISyncPeerPool>();
                    SyncPeerPool.UsefulPeersWhateverDiff.Returns(_peers.Select(p => new PeerInfo(p)));
                    SyncPeerPool.UsefulPeers.Returns(_peers.Select(p => new PeerInfo(p)));
                    SyncPeerPool.AllPeers.Returns(_peers);

                    SyncProgressResolver = Substitute.For<ISyncProgressResolver>();
                    SyncProgressResolver.ChainDifficulty.Returns(ValidGenesis.TotalDifficulty ?? 0);
                    SyncProgressResolver.FindBestHeader().Returns(0);
                    SyncProgressResolver.FindBestFullBlock().Returns(0);
                    SyncProgressResolver.FindBestBeamState().Returns(0);
                    SyncProgressResolver.FindBestFullState().Returns(0);
                    SyncProgressResolver.IsLoadingBlocksFromDb().Returns(false);
                    SyncProgressResolver.IsFastBlocksFinished().Returns(false);

                    SyncConfig.FastSync = false;
                    SyncConfig.FastBlocks = false;
                    SyncConfig.BeamSync = false;
                    SyncConfig.PivotNumber = Pivot.ToString();
                    SyncConfig.PivotHash = Keccak.Zero.ToString();
                    SyncConfig.SynchronizationEnabled = true;
                    SyncConfig.DownloadBodiesInFastSync = true;
                    SyncConfig.DownloadReceiptsInFastSync = true;
                    SyncConfig.FastSyncCatchUpHeightDelta = FastSyncCatchUpHeightDelta;
                }

                private List<ISyncPeer> _peers = new List<ISyncPeer>();

                private void AddPeer(BlockHeader header, bool isInitialized = true, string clientType = "Nethermind")
                {
                    ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
                    syncPeer.HeadHash.Returns(header.Hash);
                    syncPeer.HeadNumber.Returns(header.Number);
                    syncPeer.TotalDifficulty.Returns(header.TotalDifficulty ?? 0);
                    syncPeer.IsInitialized.Returns(isInitialized);
                    syncPeer.ClientId.Returns(clientType);

                    _actionsOnDefaults.Add(() => _peers.Add(syncPeer));
                }

                public ScenarioBuilder ThisNodeHasNeverSyncedBefore()
                {
                    return this;
                }

                public ScenarioBuilder ThisNodeIsFullySynced()
                {
                    _actionsOnDefaults.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestFullBlock().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestBeamState().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestFullState().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(ChainHead.Number);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(false);
                            SyncProgressResolver.IsLoadingBlocksFromDb().Returns(false);
                            SyncProgressResolver.ChainDifficulty.Returns(ChainHead.TotalDifficulty ?? 0);
                        }
                    );
                    return this;
                }

                public ScenarioBuilder APeerWithGenesisOnlyIsKnown()
                {
                    AddPeer(ValidGenesis);
                    return this;
                }

                public ScenarioBuilder APeerWithHighDiffGenesisOnlyIsKnown()
                {
                    AddPeer(ValidGenesis);
                    return this;
                }

                public ScenarioBuilder GoodPeersAreKnown()
                {
                    AddPeer(ChainHead);
                    return this;
                }

                public ScenarioBuilder NoPeersAreKnown()
                {
                    return this;
                }

                public ScenarioBuilder SynchronizationIsDisabled()
                {
                    _actionsOnDefaults.Add(() => SyncConfig.SynchronizationEnabled = false);
                    return this;
                }

                public ScenarioBuilder NodeIsLoadingBlocksFromDb()
                {
                    _actionsOnDefaults.Add(() => SyncProgressResolver.IsLoadingBlocksFromDb().Returns(true));
                    return this;
                }

                public ScenarioBuilder InAnySyncConfiguration()
                {
                    BeamSyncIsConfigured();
                    FullArchiveSyncIsConfigured();
                    FastSyncWithFastBlocksIsConfigured();
                    FastSyncWithoutFastBlocksIsConfigured();
                    return this;
                }

                public ScenarioBuilder BeamSyncIsConfigured()
                {
                    _configActions.Add(() =>
                    {
                        SyncConfig.FastSync = true;
                        SyncConfig.FastBlocks = true;
                        SyncConfig.BeamSync = true;
                    });

                    return this;
                }

                public ScenarioBuilder FastSyncWithFastBlocksIsConfigured()
                {
                    _configActions.Add(() =>
                    {
                        SyncConfig.FastSync = true;
                        SyncConfig.FastBlocks = true;
                        SyncConfig.BeamSync = false;
                    });

                    return this;
                }

                public ScenarioBuilder FastSyncWithoutFastBlocksIsConfigured()
                {
                    _configActions.Add(() =>
                    {
                        SyncConfig.FastSync = true;
                        SyncConfig.FastBlocks = false;
                        SyncConfig.BeamSync = false;
                    });

                    return this;
                }

                public ScenarioBuilder FullArchiveSyncIsConfigured()
                {
                    _configActions.Add(() =>
                    {
                        SyncConfig.FastSync = false;
                        SyncConfig.FastBlocks = false;
                        SyncConfig.BeamSync = false;
                    });

                    return this;
                }

                public void ShouldGive(SyncMode syncMode)
                {
                    void Test()
                    {
                        MultiSyncModeSelector selector = new MultiSyncModeSelector(SyncProgressResolver, SyncPeerPool, SyncConfig, LimboLogs.Instance);
                        selector.DisableTimer();
                        selector.Update();
                        selector.Current.Should().Be(syncMode);
                    }

                    SetDefaults();
                    foreach (Action actionOnDefaults in _actionsOnDefaults)
                    {
                        actionOnDefaults.Invoke();
                    }

                    foreach (Action configAction in _configActions)
                    {
                        configAction.Invoke();
                        Test();
                    }
                }
            }

            public static ScenarioBuilder Where()
            {
                return new ScenarioBuilder();
            }
        }

        [Test]
        public void Empty_network()
        {
            Scenario.Where()
                .ThisNodeHasNeverSyncedBefore()
                .APeerWithGenesisOnlyIsKnown()
                .InAnySyncConfiguration()
                .ShouldGive(SyncMode.None);
        }

        [Test]
        public void Empty_network_with_malicious_genesis()
        {
            // we will ignore the other node because its block is at height 0 (we never sync genesis only)
            Scenario.Where()
                .ThisNodeHasNeverSyncedBefore()
                .APeerWithHighDiffGenesisOnlyIsKnown()
                .InAnySyncConfiguration()
                .ShouldGive(SyncMode.None);
        }

        [Test]
        public void No_peers()
        {
            Scenario.Where()
                .ThisNodeHasNeverSyncedBefore()
                .NoPeersAreKnown()
                .InAnySyncConfiguration()
                .ShouldGive(SyncMode.None);
        }

        [Test]
        public void Disabled_sync()
        {
            Scenario.Where()
                .ThisNodeHasNeverSyncedBefore()
                .GoodPeersAreKnown()
                .SynchronizationIsDisabled()
                .InAnySyncConfiguration()
                .ShouldGive(SyncMode.None);
        }

        [Test]
        public void Load_from_db()
        {
            Scenario.Where()
                .ThisNodeHasNeverSyncedBefore()
                .GoodPeersAreKnown()
                .NodeIsLoadingBlocksFromDb()
                .InAnySyncConfiguration()
                .ShouldGive(SyncMode.DbLoad);
        }

        [Test]
        public void Simple_archive()
        {
            Scenario.Where()
                .ThisNodeHasNeverSyncedBefore()
                .GoodPeersAreKnown()
                .FullArchiveSyncIsConfigured()
                .ShouldGive(SyncMode.Full);
        }
        
        [Test]
        public void Simple_fast_sync()
        {
            Scenario.Where()
                .ThisNodeHasNeverSyncedBefore()
                .GoodPeersAreKnown()
                .FastSyncWithoutFastBlocksIsConfigured()
                .ShouldGive(SyncMode.FastSync);
        }
        
        [Test]
        public void Simple_fast_sync_with_fast_blocks()
        {
            // note that before we download at least one header we cannot start fast sync
            Scenario.Where()
                .ThisNodeHasNeverSyncedBefore()
                .GoodPeersAreKnown()
                .FastSyncWithFastBlocksIsConfigured()
                .ShouldGive(SyncMode.FastBlocks);
        }
        
        [Test]
        public void Sync_start_in_beam_sync()
        {
            // note that before we download at least one header we cannot start fast sync
            Scenario.Where()
                .ThisNodeHasNeverSyncedBefore()
                .GoodPeersAreKnown()
                .BeamSyncIsConfigured()
                .ShouldGive(SyncMode.FastBlocks);
        }

        // [Test]
        // public void Can_keep_changing_in_fast_sync()
        // {
        //     ISyncPeerPool syncPeerPool = Substitute.For<ISyncPeerPool>();
        //     ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        //     syncPeer.IsInitialized.Returns(true);
        //     syncPeer.TotalDifficulty.Returns((UInt256) (1024 * 1024));
        //     syncPeer.HeadNumber.Returns(0);
        //
        //     PeerInfo peerInfo1 = new PeerInfo(syncPeer);
        //     syncPeerPool.AllPeers.Returns(new[] {syncPeer});
        //     syncPeerPool.UsefulPeersWhateverDiff.Returns(new[] {peerInfo1});
        //     syncPeerPool.PeerCount.Returns(1);
        //
        //     SyncConfig syncConfig = new SyncConfig();
        //     syncConfig.FastSync = true;
        //     syncConfig.PivotNumber = null;
        //     syncConfig.PivotHash = null;
        //
        //     ISyncProgressResolver syncProgressResolver = Substitute.For<ISyncProgressResolver>();
        //     syncProgressResolver.ChainDifficulty.Returns(UInt256.One);
        //
        //     MultiSyncModeSelector selector = new MultiSyncModeSelector(syncProgressResolver, syncPeerPool, syncConfig, LimboLogs.Instance);
        //     Assert.AreEqual(SyncMode.None, selector.Current);
        //
        //     (long BestRemote, long BestLocalHeader, long BestLocalFullBlock, long BestLocalState, SyncMode ExpectedState, string Description)[] states =
        //     {
        //         (0, 0, 0, 0, SyncMode.None, "start"),
        //         (1032, 0, 0, 0, SyncMode.FastSync, "learn about remote"),
        //         (1032, 512, 0, 0, SyncMode.FastSync, "start downloading headers"),
        //         (1032, 1000, 0, 0, SyncMode.StateNodes, "finish downloading headers"),
        //         (1048, 1000, 0, 1000, SyncMode.FastSync, "download node states up to best header"),
        //         (1048, 1016, 0, 1000, SyncMode.StateNodes, "catch up headers"),
        //         (1048, 1032, 0, 1016, SyncMode.StateNodes, "headers went too far, catch up with the nodes"),
        //         (1048, 1032, 0, 1032, SyncMode.Full, "ready to full sync"),
        //         (1068, 1048, 1048, 1036, SyncMode.StateNodes, "full sync - blocks ahead of processing"),
        //         (1093, 1060, 1060, 1056, SyncMode.FastSync, "found better peer, need to catch up"),
        //         (1093, 1060, 1060, 1060, SyncMode.FastSync, "first take headers"),
        //         (1093, 1092, 1060, 1060, SyncMode.StateNodes, "then nodes again"),
        //         (2096, 1092, 1060, 1092, SyncMode.FastSync, "found even better peer - get all headers"),
        //     };
        //
        //     for (int i = 0; i < states.Length; i++)
        //     {
        //         var testCase = states[i];
        //         syncProgressResolver.FindBestFullState().Returns(testCase.BestLocalState);
        //         syncProgressResolver.FindBestHeader().Returns(testCase.BestLocalHeader);
        //         syncProgressResolver.FindBestFullBlock().Returns(testCase.BestLocalFullBlock);
        //         syncProgressResolver.IsFastBlocksFinished().Returns(true);
        //
        //         Assert.GreaterOrEqual(testCase.BestLocalHeader, testCase.BestLocalState, "checking if the test case is correct - local state always less then local header");
        //         Assert.GreaterOrEqual(testCase.BestLocalHeader, testCase.BestLocalFullBlock, "checking if the test case is correct - local full block always less then local header");
        //         peerInfo1.HeadNumber.Returns(testCase.BestRemote);
        //         selector.Update();
        //         Assert.AreEqual(testCase.ExpectedState, selector.Current, testCase.Description);
        //     }
        // }
        //
        // [TestCase(true, 1032, 999, 0, 0, SyncMode.FastSync)]
        // [TestCase(false, 1032, 1000, 0, 0, SyncMode.Full)]
        // [TestCase(true, 1032, 1000, 0, 0, SyncMode.StateNodes)]
        // [TestCase(true, 1032, 1000, 0, 1000, SyncMode.Full)]
        // [TestCase(true, 0, 1032, 0, 1032, SyncMode.None)]
        // [TestCase(true, 1, 1032, 0, 1032, SyncMode.Full)]
        // [TestCase(true, 33, 1032, 0, 1032, SyncMode.Full)]
        // [TestCase(false, 0, 1032, 0, 1032, SyncMode.None)]
        // [TestCase(true, 4506571, 4506571, 4506571, 4506452, SyncMode.StateNodes)]
        // public void Selects_correctly(bool useFastSync, long bestRemote, long bestHeader, long bestBlock, long bestLocalState, SyncMode expected)
        // {
        //     bool changedInvoked = false;
        //
        //     MultiSyncModeSelector selector = BuildSelector(new SyncConfig() {FastSync = useFastSync}, bestRemote, bestHeader, bestBlock, bestLocalState);
        //     selector.Changed += (s, e) => changedInvoked = true;
        //
        //     SyncMode beforeUpdate = selector.Current;
        //
        //     selector.Update();
        //     Assert.AreEqual(expected, selector.Current, "as expected");
        //     if (expected != beforeUpdate)
        //     {
        //         Assert.True(changedInvoked, "changed");
        //     }
        // }
        //
        // [TestCase(1032, 999, 0, 0, SyncMode.Full)]
        // [TestCase(1032, 1000, 0, 0, SyncMode.Full)]
        // [TestCase(1032, 1000, 0, 0, SyncMode.Full)]
        // [TestCase(1032, 1000, 0, 1000, SyncMode.Full)]
        // [TestCase(0, 1032, 0, 1032, SyncMode.None)]
        // [TestCase(1, 1032, 0, 1032, SyncMode.Full)]
        // [TestCase(33, 1032, 0, 1032, SyncMode.Full)]
        // [TestCase(4506571, 4506571, 4506571, 4506452, SyncMode.Full)]
        // public void Selects_correctly_in_beam_sync(long bestRemote, long bestHeader, long bestBlock, long bestLocalState, SyncMode expected)
        // {
        //     bool changedInvoked = false;
        //
        //     MultiSyncModeSelector selector = BuildSelector(new SyncConfig() {BeamSync = true}, bestRemote, bestHeader, bestBlock, bestLocalState);
        //     selector.Changed += (s, e) => changedInvoked = true;
        //
        //     SyncMode beforeUpdate = selector.Current;
        //
        //     selector.Update();
        //     Assert.AreEqual(expected, selector.Current, "as expected");
        //     if (expected != beforeUpdate)
        //     {
        //         Assert.True(changedInvoked, "changed");
        //     }
        // }
        //
        // [TestCase(true, 1032, 0, 0, 0, SyncMode.None)]
        // [TestCase(false, 1032, 0, 0, 0, SyncMode.None)]
        // [TestCase(true, 1032, 1000, 0, 0, SyncMode.None)]
        // [TestCase(false, 1032, 1000, 0, 0, SyncMode.None)]
        // [TestCase(true, 1032, 1000, 0, 1000, SyncMode.None)]
        // [TestCase(false, 1032, 1000, 0, 1000, SyncMode.None)]
        // public void Does_not_change_when_no_peers(bool useFastSync, long bestRemote, long bestLocalHeader, long bestLocalFullBLock, long bestLocalState, SyncMode expected)
        // {
        //     MultiSyncModeSelector selector = BuildSelectorNoPeers(useFastSync, bestRemote, bestLocalHeader, bestLocalFullBLock, bestLocalState);
        //     selector.Update();
        //     Assert.AreEqual(expected, selector.Current);
        // }
        //
        // [TestCase(0, 1032, 1000000, SyncMode.FastSync)]
        // [TestCase(0, 1032, 100, SyncMode.FastSync)]
        // [TestCase(0, 1032, null, SyncMode.FastSync)]
        // [TestCase(10, 1032, 100, SyncMode.FastSync)]
        // [TestCase(10, 1032, 1000000, SyncMode.Full)]
        // public void Selects_correctly_in_fast_sync_based_on_CatchUpHeightDelta(long currentHead, long bestRemote, long? fastSyncCatchUpHeightDelta, SyncMode expected)
        // {
        //     bool changedInvoked = false;
        //
        //     MultiSyncModeSelector selector = BuildSelector(new SyncConfig() {FastSync = true, FastSyncCatchUpHeightDelta = fastSyncCatchUpHeightDelta}, bestRemote, currentHead, currentHead, currentHead, currentHead);
        //     selector.Changed += (s, e) => changedInvoked = true;
        //     selector.Update();
        //
        //     SyncMode beforeUpdate = selector.Current;
        //
        //     Assert.AreEqual(expected, selector.Current, "as expected");
        //     if (expected != beforeUpdate)
        //     {
        //         Assert.True(changedInvoked, "changed");
        //     }
        // }
        //
        // private static MultiSyncModeSelector BuildSelector(SyncConfig syncConfig, long bestRemote = 0L, long bestHeader = 0L, long bestBlock = 0L, long bestLocalState = 0L, long bestProcessed = 0L)
        // {
        //     ISyncPeerPool syncPeerPool = Substitute.For<ISyncPeerPool>();
        //     PeerInfo peerInfo1 = BuildPeerInfo(bestRemote, true);
        //     PeerInfo peerInfo2 = BuildPeerInfo(bestRemote, true);
        //     PeerInfo peerInfo3 = BuildPeerInfo(0, true);
        //     PeerInfo peerInfo4 = BuildPeerInfo(bestRemote * 2, false);
        //     syncPeerPool.AllPeers.Returns(new ISyncPeer[] { });
        //     syncPeerPool.UsefulPeersWhateverDiff.Returns(new[] {peerInfo1, peerInfo2});
        //     syncPeerPool.PeerCount.Returns(3);
        //
        //     ISyncProgressResolver syncProgressResolver = Substitute.For<ISyncProgressResolver>();
        //     syncProgressResolver.FindBestHeader().Returns(bestHeader);
        //     syncProgressResolver.FindBestFullBlock().Returns(bestBlock);
        //     syncProgressResolver.FindBestFullState().Returns(bestLocalState);
        //     syncProgressResolver.IsFastBlocksFinished().Returns(true);
        //     syncProgressResolver.FindBestProcessedBlock().Returns(bestProcessed);
        //     syncProgressResolver.ChainDifficulty.Returns(UInt256.MaxValue);
        //
        //     MultiSyncModeSelector selector = new MultiSyncModeSelector(syncProgressResolver, syncPeerPool, syncConfig, LimboLogs.Instance);
        //     return selector;
        // }
        //
        // private static PeerInfo BuildPeerInfo(long bestRemote, bool isInitialized)
        // {
        //     ISyncPeer syncPeer1 = Substitute.For<ISyncPeer>();
        //     syncPeer1.TotalDifficulty.Returns((UInt256) (1024 * 1024));
        //     syncPeer1.HeadNumber.Returns(bestRemote);
        //     syncPeer1.IsInitialized.Returns(isInitialized);
        //     PeerInfo peerInfo1 = new PeerInfo(syncPeer1);
        //     return peerInfo1;
        // }
        //
        // private static MultiSyncModeSelector BuildSelectorNoPeers(bool useFastSync, long bestRemote = 0L, long bestHeader = 0L, long bestBlock = 0L, long bestLocalState = 0L)
        // {
        //     ISyncPeerPool syncPeerPool = Substitute.For<ISyncPeerPool>();
        //     syncPeerPool.AllPeers.Returns(new ISyncPeer[] { });
        //     syncPeerPool.PeerCount.Returns(0);
        //
        //     SyncConfig syncConfig = new SyncConfig();
        //     syncConfig.FastSync = !useFastSync;
        //
        //     ISyncProgressResolver syncProgressResolver = Substitute.For<ISyncProgressResolver>();
        //     syncProgressResolver.FindBestHeader().Returns(bestHeader);
        //     syncProgressResolver.FindBestFullBlock().Returns(bestBlock);
        //     syncProgressResolver.FindBestFullState().Returns(bestLocalState);
        //     syncProgressResolver.IsFastBlocksFinished().Returns(true);
        //     syncProgressResolver.FindBestProcessedBlock().Returns(bestLocalState);
        //
        //     MultiSyncModeSelector selector = new MultiSyncModeSelector(syncProgressResolver, syncPeerPool, syncConfig, LimboLogs.Instance);
        //     return selector;
        // }
    }
}