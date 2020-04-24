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
            public const long FastSyncCatchUpHeightDelta = 64;

            public static BlockHeader Pivot { get; set; } = Build.A.Block.WithTotalDifficulty((UInt256) 1024).WithNumber(1024).TestObject.Header;

            public static BlockHeader MidWayToPivot { get; set; } = Build.A.Block.WithTotalDifficulty((UInt256) 512).WithNumber(512).TestObject.Header;

            public static BlockHeader ChainHead { get; set; } = Build.A.Block.WithTotalDifficulty(Pivot.TotalDifficulty + 2048).WithNumber(Pivot.Number + 2048).TestObject.Header;
            
            public static BlockHeader FutureHead { get; set; } = Build.A.Block.WithTotalDifficulty(Pivot.TotalDifficulty + 2048 + 128).WithNumber(Pivot.Number + 2048 + 128).TestObject.Header;
            
            public static BlockHeader SlightlyFutureHead { get; set; } = Build.A.Block.WithTotalDifficulty(Pivot.TotalDifficulty + 2048 + 4).WithNumber(Pivot.Number + 2048 + 4).TestObject.Header;

            public static BlockHeader MaliciousPrePivot { get; set; } = Build.A.Block.WithTotalDifficulty((UInt256) 1000000).WithNumber(512).TestObject.Header;

            public static BlockHeader NewBetterBranchWithLowerNumber { get; set; } = Build.A.Block.WithTotalDifficulty((UInt256) 1000000).WithNumber(ChainHead.Number - 16).TestObject.Header;

            public static BlockHeader ValidGenesis { get; set; } = Build.A.Block.WithDifficulty(1).WithTotalDifficulty(UInt256.One).Genesis.TestObject.Header;

            public static BlockHeader InvalidGenesis { get; set; } = Build.A.Block.WithDifficulty(1).WithTotalDifficulty(UInt256.One).Genesis.TestObject.Header;

            public static BlockHeader InvalidGenesisWithHighTotalDifficulty { get; set; } = Build.A.Block.Genesis.WithDifficulty((UInt256) 1000000).WithTotalDifficulty((UInt256) 1000000).TestObject.Header;

            public class ScenarioBuilder
            {
                private List<Func<string>> _configActions = new List<Func<string>>();

                private List<Func<string>> _peeringSetups = new List<Func<string>>();

                private List<Func<string>> _syncProgressSetups = new List<Func<string>>();

                private List<Action> _overwrites = new List<Action>();

                public ISyncPeerPool SyncPeerPool { get; set; }

                public ISyncProgressResolver SyncProgressResolver { get; set; }

                public ISyncConfig SyncConfig { get; set; } = new SyncConfig();

                public ScenarioBuilder()
                {
                }

                private void SetDefaults()
                {
                    SyncPeerPool = Substitute.For<ISyncPeerPool>();
                    var peerInfos = _peers.Select(p => new PeerInfo(p));
                    SyncPeerPool.InitializedPeers.Returns(peerInfos);
                    SyncPeerPool.AllPeers.Returns(peerInfos);

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
                    SyncConfig.PivotNumber = Pivot.Number.ToString();
                    SyncConfig.PivotHash = Keccak.Zero.ToString();
                    SyncConfig.SynchronizationEnabled = true;
                    SyncConfig.DownloadBodiesInFastSync = true;
                    SyncConfig.DownloadReceiptsInFastSync = true;
                    SyncConfig.FastSyncCatchUpHeightDelta = FastSyncCatchUpHeightDelta;
                }

                private List<ISyncPeer> _peers = new List<ISyncPeer>();

                private void AddPeeringSetup(string name, params ISyncPeer[] peers)
                {
                    _peeringSetups.Add(() =>
                    {
                        foreach (ISyncPeer syncPeer in peers)
                        {
                            _peers.Add(syncPeer);
                        }

                        return name;
                    });
                }

                private ISyncPeer AddPeer(BlockHeader header, bool isInitialized = true, string clientType = "Nethermind")
                {
                    ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
                    syncPeer.HeadHash.Returns(header.Hash);
                    syncPeer.HeadNumber.Returns(header.Number);
                    syncPeer.TotalDifficulty.Returns(header.TotalDifficulty ?? 0);
                    syncPeer.IsInitialized.Returns(isInitialized);
                    syncPeer.ClientId.Returns(clientType);
                    return syncPeer;
                }

                public ScenarioBuilder IfThisNodeHasNeverSyncedBefore()
                {
                    _syncProgressSetups.Add(() => "fresh start");
                    return this;
                }

                public ScenarioBuilder IfThisNodeIsFullySynced()
                {
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestFullBlock().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestBeamState().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestFullState().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(ChainHead.Number);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(true);
                            SyncProgressResolver.ChainDifficulty.Returns(ChainHead.TotalDifficulty ?? 0);
                            return "fully synced node";
                        }
                    );
                    return this;
                }

                public ScenarioBuilder IfThisNodeIsProcessingAlreadyDownloadedBlocksInFullSync()
                {
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestFullBlock().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestBeamState().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestFullState().Returns(ChainHead.Number - 128);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(ChainHead.Number - 128);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(true);
                            SyncProgressResolver.ChainDifficulty.Returns(ChainHead.TotalDifficulty ?? 0);
                            return "fully synching";
                        }
                    );
                    return this;
                }

                public ScenarioBuilder IfThisNodeIsInTheMiddleOfFastSyncAndFastBlocks()
                {
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(Pivot.Number + 16);
                            SyncProgressResolver.FindBestFullBlock().Returns(0);
                            SyncProgressResolver.FindBestBeamState().Returns(0);
                            SyncProgressResolver.FindBestFullState().Returns(0);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(0);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(false);
                            SyncProgressResolver.ChainDifficulty.Returns(UInt256.Zero);
                            return "mid fast sync and fast blocks";
                        }
                    );
                    return this;
                }

                public ScenarioBuilder IfThisNodeFinishedFastBlocksButNotFastSync()
                {
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(Pivot.Number + 16);
                            SyncProgressResolver.FindBestFullBlock().Returns(0);
                            SyncProgressResolver.FindBestBeamState().Returns(0);
                            SyncProgressResolver.FindBestFullState().Returns(0);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(0);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(true);
                            SyncProgressResolver.ChainDifficulty.Returns(UInt256.Zero);
                            return "mid fast sync";
                        }
                    );
                    return this;
                }

                public ScenarioBuilder ThisNodeFinishedFastSyncButNotFastBlocks()
                {
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(ChainHead.Number - MultiSyncModeSelector.FastSyncLag);
                            SyncProgressResolver.FindBestFullBlock().Returns(0);
                            SyncProgressResolver.FindBestBeamState().Returns(0);
                            SyncProgressResolver.FindBestFullState().Returns(0);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(0);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(false);
                            SyncProgressResolver.ChainDifficulty.Returns(UInt256.Zero);
                            return "mid fast blocks but fast sync finished";
                        }
                    );
                    return this;
                }

                public ScenarioBuilder IfThisNodeFinishedStateSyncButNotFastBlocks()
                {
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(ChainHead.Number - MultiSyncModeSelector.FastSyncLag);
                            SyncProgressResolver.FindBestFullBlock().Returns(0);
                            SyncProgressResolver.FindBestBeamState().Returns(0);
                            SyncProgressResolver.FindBestFullState().Returns(ChainHead.Number - MultiSyncModeSelector.FastSyncLag);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(0);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(false);
                            SyncProgressResolver.ChainDifficulty.Returns(UInt256.Zero);
                            return "just finished state sync but not fast blocks";
                        }
                    );
                    return this;
                }

                public ScenarioBuilder IfThisNodeJustFinishedStateSyncAndFastBlocks()
                {
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(ChainHead.Number - MultiSyncModeSelector.FastSyncLag);
                            SyncProgressResolver.FindBestFullBlock().Returns(0);
                            SyncProgressResolver.FindBestBeamState().Returns(0);
                            SyncProgressResolver.FindBestFullState().Returns(ChainHead.Number - MultiSyncModeSelector.FastSyncLag);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(0);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(true);
                            SyncProgressResolver.ChainDifficulty.Returns(UInt256.Zero);
                            return "just finished state sync and fast blocks";
                        }
                    );
                    return this;
                }

                public ScenarioBuilder IfThisNodeJustFinishedFastBlocksAndFastSync()
                {
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(ChainHead.Number - MultiSyncModeSelector.FastSyncLag);
                            SyncProgressResolver.FindBestFullBlock().Returns(0);
                            SyncProgressResolver.FindBestBeamState().Returns(0);
                            SyncProgressResolver.FindBestFullState().Returns(0);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(0);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(true);
                            SyncProgressResolver.ChainDifficulty.Returns(UInt256.Zero);
                            return "just after fast blocks and fast sync";
                        }
                    );
                    return this;
                }

                public ScenarioBuilder IfThisNodeJustStartedFullSyncProcessing()
                {
                    long currentBlock = ChainHead.Number - MultiSyncModeSelector.FastSyncLag + 1;
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(currentBlock);
                            SyncProgressResolver.FindBestFullBlock().Returns(currentBlock);
                            SyncProgressResolver.FindBestBeamState().Returns(0);
                            SyncProgressResolver.FindBestFullState().Returns(currentBlock);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(0);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(true);
                            SyncProgressResolver.ChainDifficulty.Returns((UInt256) currentBlock);
                            return "just started full sync";
                        }
                    );
                    return this;
                }

                public ScenarioBuilder IfThisNodeRecentlyStartedFullSyncProcessing()
                {
                    long currentBlock = ChainHead.Number - MultiSyncModeSelector.FastSyncLag / 2;
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestFullBlock().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestBeamState().Returns(0);
                            SyncProgressResolver.FindBestFullState().Returns(currentBlock);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(currentBlock);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(true);
                            SyncProgressResolver.ChainDifficulty.Returns((UInt256) currentBlock);
                            return "recently started full sync";
                        }
                    );
                    return this;
                }

                public ScenarioBuilder IfThisNodeNeedsAFastSyncCatchUp()
                {
                    long currentBlock = ChainHead.Number - FastSyncCatchUpHeightDelta;
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(currentBlock);
                            SyncProgressResolver.FindBestFullBlock().Returns(currentBlock);
                            SyncProgressResolver.FindBestBeamState().Returns(0);
                            SyncProgressResolver.FindBestFullState().Returns(currentBlock);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(currentBlock);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(true);
                            SyncProgressResolver.ChainDifficulty.Returns((UInt256) currentBlock);
                            return "fast sync catch up";
                        }
                    );
                    return this;
                }
                
                public ScenarioBuilder IfThisNodeHasStateThatIsFarInThePast()
                {
                    // this is a scenario when we actually have state but the lookup depth is limiting
                    // our ability to find out at what level the state is
                    long currentBlock = ChainHead.Number - FastSyncCatchUpHeightDelta - 16;
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestFullBlock().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestBeamState().Returns(0);
                            SyncProgressResolver.FindBestFullState().Returns(0);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(currentBlock);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(true);
                            SyncProgressResolver.ChainDifficulty.Returns((UInt256) currentBlock);
                            return "fast sync catch up";
                        }
                    );
                    return this;
                }

                public ScenarioBuilder IfThisNodeNearlyNeedsAFastSyncCatchUp()
                {
                    long currentBlock = ChainHead.Number - FastSyncCatchUpHeightDelta + 1;
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(currentBlock);
                            SyncProgressResolver.FindBestFullBlock().Returns(currentBlock);
                            SyncProgressResolver.FindBestBeamState().Returns(0);
                            SyncProgressResolver.FindBestFullState().Returns(currentBlock);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(currentBlock);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(true);
                            SyncProgressResolver.ChainDifficulty.Returns((UInt256) currentBlock);
                            return "fast sync catch up";
                        }
                    );
                    return this;
                }

                public ScenarioBuilder IfTheSyncProgressIsCorrupted()
                {
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestFullBlock().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestBeamState().Returns(0);
                            SyncProgressResolver.FindBestFullState().Returns(ChainHead.Number - 1);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(ChainHead.Number);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(true);
                            SyncProgressResolver.ChainDifficulty.Returns((UInt256) ChainHead.Number);
                            return "corrupted progress";
                        }
                    );

                    return this;
                }

                public ScenarioBuilder AndAPeerWithGenesisOnlyIsKnown()
                {
                    AddPeeringSetup("genesis network", AddPeer(ValidGenesis));
                    return this;
                }

                public ScenarioBuilder AndAPeerWithHighDiffGenesisOnlyIsKnown()
                {
                    AddPeeringSetup("malicious genesis network", AddPeer(ValidGenesis));
                    return this;
                }

                public ScenarioBuilder AndGoodPeersAreKnown()
                {
                    AddPeeringSetup("good network", AddPeer(ChainHead));
                    return this;
                }
                
                public ScenarioBuilder AndPeersMovedForward()
                {
                    AddPeeringSetup("peers moved forward", AddPeer(FutureHead));
                    return this;
                }
                
                public ScenarioBuilder AndPeersMovedSlightlyForward()
                {
                    AddPeeringSetup("peers moved slightly forward", AddPeer(SlightlyFutureHead));
                    return this;
                }

                public ScenarioBuilder PeersFromDesirableBranchAreKnown()
                {
                    AddPeeringSetup("better branch", AddPeer(NewBetterBranchWithLowerNumber));
                    return this;
                }

                public ScenarioBuilder AndDesirablePrePivotPeerIsKnown()
                {
                    AddPeeringSetup("good network", AddPeer(MaliciousPrePivot));
                    return this;
                }

                public ScenarioBuilder AndPeersAreOnlyUsefulForFastBlocks()
                {
                    AddPeeringSetup("network for fast blocks only", AddPeer(MidWayToPivot));
                    return this;
                }

                public ScenarioBuilder AndNoPeersAreKnown()
                {
                    AddPeeringSetup("empty network");
                    return this;
                }

                public ScenarioBuilder WhenSynchronizationIsDisabled()
                {
                    _overwrites.Add(() => SyncConfig.SynchronizationEnabled = false);
                    return this;
                }

                public ScenarioBuilder WhenThisNodeIsLoadingBlocksFromDb()
                {
                    _overwrites.Add(() => SyncProgressResolver.IsLoadingBlocksFromDb().Returns(true));
                    return this;
                }

                public ScenarioBuilder ThenInAnySyncConfiguration()
                {
                    WhenBeamSyncIsConfigured();
                    WhenFullArchiveSyncIsConfigured();
                    WhenFastSyncWithFastBlocksIsConfigured();
                    WhenFastSyncWithoutFastBlocksIsConfigured();
                    return this;
                }

                public ScenarioBuilder WhateverThePeerPoolLooks()
                {
                    AndNoPeersAreKnown();
                    AndGoodPeersAreKnown();
                    AndPeersMovedForward();
                    AndPeersMovedSlightlyForward();
                    AndDesirablePrePivotPeerIsKnown();
                    AndAPeerWithHighDiffGenesisOnlyIsKnown();
                    AndAPeerWithGenesisOnlyIsKnown();
                    AndPeersAreOnlyUsefulForFastBlocks();
                    PeersFromDesirableBranchAreKnown();
                    return this;
                }

                public ScenarioBuilder WhateverTheSyncProgressIs()
                {
                    IfThisNodeHasNeverSyncedBefore();
                    IfThisNodeIsFullySynced();
                    IfThisNodeIsProcessingAlreadyDownloadedBlocksInFullSync();
                    IfThisNodeIsInTheMiddleOfFastSyncAndFastBlocks();
                    IfThisNodeFinishedFastBlocksButNotFastSync();
                    IfThisNodeJustFinishedFastBlocksAndFastSync();
                    IfThisNodeFinishedStateSyncButNotFastBlocks();
                    IfThisNodeJustFinishedStateSyncAndFastBlocks();
                    IfThisNodeJustStartedFullSyncProcessing();
                    IfThisNodeRecentlyStartedFullSyncProcessing();
                    IfTheSyncProgressIsCorrupted();
                    IfThisNodeNeedsAFastSyncCatchUp();
                    IfThisNodeNearlyNeedsAFastSyncCatchUp();
                    IfThisNodeHasStateThatIsFarInThePast();
                    return this;
                }

                public ScenarioBuilder WhenBeamSyncIsConfigured()
                {
                    _configActions.Add(() =>
                    {
                        SyncConfig.FastSync = true;
                        SyncConfig.FastBlocks = true;
                        SyncConfig.BeamSync = true;
                        return "beam sync";
                    });

                    return this;
                }

                public ScenarioBuilder WhenFastSyncWithFastBlocksIsConfigured()
                {
                    _configActions.Add(() =>
                    {
                        SyncConfig.FastSync = true;
                        SyncConfig.FastBlocks = true;
                        SyncConfig.BeamSync = false;
                        return "fast sync with fast blocks";
                    });

                    return this;
                }

                public ScenarioBuilder WhenFastSyncWithoutFastBlocksIsConfigured()
                {
                    _configActions.Add(() =>
                    {
                        SyncConfig.FastSync = true;
                        SyncConfig.FastBlocks = false;
                        SyncConfig.BeamSync = false;
                        return "fast sync without fast blocks";
                    });

                    return this;
                }

                public ScenarioBuilder WhenFullArchiveSyncIsConfigured()
                {
                    _configActions.Add(() =>
                    {
                        SyncConfig.FastSync = false;
                        SyncConfig.FastBlocks = false;
                        SyncConfig.BeamSync = false;
                        return "full archive";
                    });

                    return this;
                }

                public void TheSyncModeShouldBe(SyncMode syncMode)
                {
                    void Test()
                    {
                        foreach (Action overwrite in _overwrites)
                        {
                            overwrite.Invoke();
                        }

                        MultiSyncModeSelector selector = new MultiSyncModeSelector(SyncProgressResolver, SyncPeerPool, SyncConfig, LimboLogs.Instance);
                        selector.DisableTimer();
                        selector.Update();
                        selector.Current.Should().Be(syncMode);
                    }

                    SetDefaults();

                    foreach (Func<string> syncProgressSetup in _syncProgressSetups)
                    {
                        foreach (Func<string> peeringSetup in _peeringSetups)
                        {
                            foreach (Func<string> configSetups in _configActions)
                            {
                                string syncProgressSetupName = syncProgressSetup.Invoke();
                                string peeringSetupName = peeringSetup.Invoke();
                                string configSetupName = configSetups.Invoke();

                                Console.WriteLine("=====================");
                                Console.WriteLine(syncProgressSetupName);
                                Console.WriteLine(peeringSetupName);
                                Console.WriteLine(configSetupName);
                                Test();
                                Console.WriteLine("=====================");
                            }
                        }
                    }
                }
            }

            public static ScenarioBuilder GoesLikeThis()
            {
                return new ScenarioBuilder();
            }
        }

        [Test]
        public void Genesis_network()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeHasNeverSyncedBefore()
                .AndAPeerWithGenesisOnlyIsKnown()
                .ThenInAnySyncConfiguration()
                .TheSyncModeShouldBe(SyncMode.None);
        }

        [Test]
        public void Network_with_malicious_genesis()
        {
            // we will ignore the other node because its block is at height 0 (we never sync genesis only)
            Scenario.GoesLikeThis()
                .IfThisNodeHasNeverSyncedBefore()
                .AndAPeerWithHighDiffGenesisOnlyIsKnown()
                .ThenInAnySyncConfiguration()
                .TheSyncModeShouldBe(SyncMode.None);
        }

        [Test]
        public void Empty_peers_or_no_connection()
        {
            Scenario.GoesLikeThis()
                .WhateverTheSyncProgressIs()
                .AndNoPeersAreKnown()
                .ThenInAnySyncConfiguration()
                .TheSyncModeShouldBe(SyncMode.None);
        }

        [Test]
        public void Disabled_sync()
        {
            Scenario.GoesLikeThis()
                .WhateverTheSyncProgressIs()
                .WhateverThePeerPoolLooks()
                .WhenSynchronizationIsDisabled()
                .ThenInAnySyncConfiguration()
                .TheSyncModeShouldBe(SyncMode.None);
        }

        [Test]
        public void Load_from_db()
        {
            Scenario.GoesLikeThis()
                .WhateverTheSyncProgressIs()
                .WhateverThePeerPoolLooks()
                .WhenThisNodeIsLoadingBlocksFromDb()
                .ThenInAnySyncConfiguration()
                .TheSyncModeShouldBe(SyncMode.DbLoad);
        }

        [Test]
        public void Simple_archive()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeHasNeverSyncedBefore()
                .AndGoodPeersAreKnown()
                .WhenFullArchiveSyncIsConfigured()
                .TheSyncModeShouldBe(SyncMode.Full);
        }

        [Test]
        public void Simple_fast_sync()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeHasNeverSyncedBefore()
                .AndGoodPeersAreKnown()
                .WhenFastSyncWithoutFastBlocksIsConfigured()
                .TheSyncModeShouldBe(SyncMode.FastSync);
        }

        [Test]
        public void Simple_fast_sync_with_fast_blocks()
        {
            // note that before we download at least one header we cannot start fast sync
            Scenario.GoesLikeThis()
                .IfThisNodeHasNeverSyncedBefore()
                .AndGoodPeersAreKnown()
                .WhenFastSyncWithFastBlocksIsConfigured()
                .TheSyncModeShouldBe(SyncMode.FastBlocks);
        }

        [Test]
        public void Sync_start_in_beam_sync()
        {
            // note that before we download at least one header we cannot start fast sync
            Scenario.GoesLikeThis()
                .IfThisNodeHasNeverSyncedBefore()
                .AndGoodPeersAreKnown()
                .WhenBeamSyncIsConfigured()
                .TheSyncModeShouldBe(SyncMode.FastBlocks);
        }

        [Test]
        public void In_the_middle_of_fast_sync_with_fast_blocks()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeIsInTheMiddleOfFastSyncAndFastBlocks()
                .AndGoodPeersAreKnown()
                .WhenFastSyncWithFastBlocksIsConfigured()
                .TheSyncModeShouldBe(SyncMode.FastBlocks | SyncMode.FastSync);
        }

        [Test]
        public void In_the_middle_of_fast_sync_with_fast_blocks_with_lesser_peers()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeIsInTheMiddleOfFastSyncAndFastBlocks()
                .AndPeersAreOnlyUsefulForFastBlocks()
                .WhenFastSyncWithFastBlocksIsConfigured()
                .TheSyncModeShouldBe(SyncMode.FastBlocks);
        }

        [Test]
        public void In_the_middle_of_fast_sync()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeIsInTheMiddleOfFastSyncAndFastBlocks()
                .AndGoodPeersAreKnown()
                .WhenFastSyncWithoutFastBlocksIsConfigured()
                .TheSyncModeShouldBe(SyncMode.FastSync);
        }

        [Test]
        public void In_the_middle_of_fast_sync_and_lesser_peers_are_known()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeIsInTheMiddleOfFastSyncAndFastBlocks()
                .AndPeersAreOnlyUsefulForFastBlocks()
                .WhenFastSyncWithoutFastBlocksIsConfigured()
                .TheSyncModeShouldBe(SyncMode.None);
        }

        [Test]
        public void Finished_fast_sync_but_not_state_sync_and_lesser_peers_are_known()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeJustFinishedFastBlocksAndFastSync()
                .AndPeersAreOnlyUsefulForFastBlocks()
                .WhenFastSyncWithoutFastBlocksIsConfigured()
                .TheSyncModeShouldBe(SyncMode.None);
        }

        [Test]
        public void Finished_fast_sync_but_not_state_sync()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeJustFinishedFastBlocksAndFastSync()
                .AndGoodPeersAreKnown()
                .WhenFastSyncWithoutFastBlocksIsConfigured()
                .TheSyncModeShouldBe(SyncMode.StateNodes);
        }

        [Test]
        public void Finished_fast_sync_but_not_state_sync_and_fast_blocks_in_progress()
        {
            Scenario.GoesLikeThis()
                .ThisNodeFinishedFastSyncButNotFastBlocks()
                .AndGoodPeersAreKnown()
                .WhenFastSyncWithFastBlocksIsConfigured()
                .TheSyncModeShouldBe(SyncMode.StateNodes | SyncMode.FastBlocks);
        }

        [Test]
        public void Finished_fast_sync_but_not_state_sync_and_fast_blocks_in_progress_and_beam_sync_enabled()
        {
            Scenario.GoesLikeThis()
                .ThisNodeFinishedFastSyncButNotFastBlocks()
                .AndGoodPeersAreKnown()
                .WhenBeamSyncIsConfigured()
                .TheSyncModeShouldBe(SyncMode.StateNodes | SyncMode.FastBlocks | SyncMode.Beam);
        }

        [Test]
        public void Finished_state_node_but_not_fast_blocks()
        {
            Scenario.GoesLikeThis()
                .ThisNodeFinishedFastSyncButNotFastBlocks()
                .AndGoodPeersAreKnown()
                .WhenBeamSyncIsConfigured()
                .TheSyncModeShouldBe(SyncMode.StateNodes | SyncMode.FastBlocks | SyncMode.Beam);
        }

        [Test]
        public void Beam_sync_before_fast_sync_is_finished_will_not_start()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeFinishedFastBlocksButNotFastSync()
                .AndGoodPeersAreKnown()
                .WhenBeamSyncIsConfigured()
                .TheSyncModeShouldBe(SyncMode.FastSync);
        }

        [Test]
        public void Just_after_finishing_state_sync_and_fast_blocks()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeJustFinishedStateSyncAndFastBlocks()
                .AndGoodPeersAreKnown()
                .WhenBeamSyncIsConfigured()
                .TheSyncModeShouldBe(SyncMode.Full);
        }

        [Test]
        public void Just_after_finishing_state_sync_but_not_fast_blocks()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeFinishedStateSyncButNotFastBlocks()
                .AndGoodPeersAreKnown()
                .WhenBeamSyncIsConfigured()
                .TheSyncModeShouldBe(SyncMode.Full | SyncMode.FastBlocks);
        }

        [Test]
        public void When_finished_fast_sync_and_pre_pivot_block_appears()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeIsFullySynced()
                .AndDesirablePrePivotPeerIsKnown()
                .WhenBeamSyncIsConfigured()
                .TheSyncModeShouldBe(SyncMode.None);
        }

        [Test]
        public void When_fast_syncing_and_pre_pivot_block_appears()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeFinishedFastBlocksButNotFastSync()
                .AndDesirablePrePivotPeerIsKnown()
                .WhenBeamSyncIsConfigured()
                .TheSyncModeShouldBe(SyncMode.None);
        }

        [Test]
        public void When_just_started_full_sync()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeJustStartedFullSyncProcessing()
                .AndGoodPeersAreKnown()
                .WhenBeamSyncIsConfigured()
                .TheSyncModeShouldBe(SyncMode.Full);
        }
        
        [Test]
        public void When_just_started_full_sync_and_peers_moved_forward()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeJustStartedFullSyncProcessing()
                .AndPeersMovedForward()
                .WhenBeamSyncIsConfigured()
                .TheSyncModeShouldBe(SyncMode.Full);
        }
        
        [Test, Description("Fixes this scenario: // 2020-04-23 19:46:46.0143|INFO|180|Changing state to Full at processed:0|beam state:9930654|state:9930654|block:0|header:9930654|peer block:9930686 // 2020-04-23 19:46:47.0361|INFO|68|Changing state to StateNodes at processed:0|beam state:9930654|state:9930654|block:9930686|header:9930686|peer block:9930686")]
        public void When_just_started_full_sync_and_peers_moved_slightly_forward()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeJustStartedFullSyncProcessing()
                .AndPeersMovedSlightlyForward()
                .WhenBeamSyncIsConfigured()
                .TheSyncModeShouldBe(SyncMode.Full);
        }

        [Test]
        public void When_recently_started_full_sync()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeRecentlyStartedFullSyncProcessing()
                .AndGoodPeersAreKnown()
                .WhenBeamSyncIsConfigured()
                .TheSyncModeShouldBe(SyncMode.Full);
        }

        [Test]
        public void When_progress_is_corrupted()
        {
            Scenario.GoesLikeThis()
                .IfTheSyncProgressIsCorrupted()
                .AndGoodPeersAreKnown()
                .WhenBeamSyncIsConfigured()
                .TheSyncModeShouldBe(SyncMode.None);
        }

        [Test]
        public void Waiting_for_processor()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeIsProcessingAlreadyDownloadedBlocksInFullSync()
                .AndGoodPeersAreKnown()
                .WhenBeamSyncIsConfigured()
                .TheSyncModeShouldBe(SyncMode.None);
        }

        [Test]
        public void Can_switch_to_a_better_branch_while_processing()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeIsProcessingAlreadyDownloadedBlocksInFullSync()
                .PeersFromDesirableBranchAreKnown()
                .WhenBeamSyncIsConfigured()
                .TheSyncModeShouldBe(SyncMode.Full);
        }

        [Test]
        public void Can_switch_to_a_better_branch_while_full_synced()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeIsFullySynced()
                .PeersFromDesirableBranchAreKnown()
                .WhenBeamSyncIsConfigured()
                .TheSyncModeShouldBe(SyncMode.Full);
        }

        [Test]
        public void Fast_sync_catch_up()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeNeedsAFastSyncCatchUp()
                .AndGoodPeersAreKnown()
                .WhenBeamSyncIsConfigured()
                .TheSyncModeShouldBe(SyncMode.FastSync);
        }

        [Test]
        public void Nearly_fast_sync_catch_up()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeNearlyNeedsAFastSyncCatchUp()
                .AndGoodPeersAreKnown()
                .WhenBeamSyncIsConfigured()
                .TheSyncModeShouldBe(SyncMode.Full);
        }
        
        [Test]
        public void State_far_in_the_past()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeHasStateThatIsFarInThePast()
                .AndGoodPeersAreKnown()
                .WhenBeamSyncIsConfigured()
                .TheSyncModeShouldBe(SyncMode.StateNodes | SyncMode.Beam);
        }
        
        [Test]
        public void When_peers_move_slightly_forward_when_state_syncing()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeJustFinishedFastBlocksAndFastSync()
                .AndPeersMovedSlightlyForward()
                .WhenBeamSyncIsConfigured()
                .TheSyncModeShouldBe(SyncMode.StateNodes | SyncMode.FastSync);
        }
    }
}