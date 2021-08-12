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

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.ParallelSync
{
    [Parallelizable(ParallelScope.All)]
    [TestFixture]
    public class MultiSyncModeSelectorTests
    {
        public static class Scenario
        {
            public const long FastSyncCatchUpHeightDelta = 64;

            public static BlockHeader Pivot { get; set; } = Build.A.Block.WithDifficulty(1).WithTotalDifficulty((UInt256) 1024).WithNumber(1024).TestObject.Header;

            public static BlockHeader MidWayToPivot { get; set; } = Build.A.Block.WithDifficulty(1).WithTotalDifficulty((UInt256) 512).WithNumber(512).TestObject.Header;

            public static BlockHeader ChainHead { get; set; } = Build.A.Block.WithDifficulty(1).WithTotalDifficulty(Pivot.TotalDifficulty + 2048).WithNumber(Pivot.Number + 2048).TestObject.Header;
            
            public static BlockHeader ChainHeadWrongDifficulty
            {
                get
                {
                    BlockHeader header = Build.A.Block.WithDifficulty(1).WithTotalDifficulty(Pivot.TotalDifficulty + 2048 + 128).WithNumber(Pivot.Number + 2048).TestObject.Header;
                    header.Hash = ChainHead.Hash;
                    return header;
                }
            }
            
            public static BlockHeader ChainHeadParentWrongDifficulty
            {
                get
                {
                    BlockHeader header = Build.A.Block.WithDifficulty(1).WithTotalDifficulty(Pivot.TotalDifficulty + 2048 + 128).WithNumber(Pivot.Number + 2048).TestObject.Header;
                    header.Hash = ChainHead.ParentHash;
                    return header;
                }
            }

            public static BlockHeader FutureHead { get; set; } = Build.A.Block.WithDifficulty(1).WithTotalDifficulty(Pivot.TotalDifficulty + 2048 + 128).WithNumber(Pivot.Number + 2048 + 128).TestObject.Header;

            public static BlockHeader SlightlyFutureHead { get; set; } = Build.A.Block.WithDifficulty(1).WithTotalDifficulty(Pivot.TotalDifficulty + 2048 + 4).WithNumber(Pivot.Number + 2048 + 4).TestObject.Header;

            public static BlockHeader SlightlyFutureHeadWithFastSyncLag { get; set; } = Build.A.Block.WithDifficulty(1).WithTotalDifficulty(Pivot.TotalDifficulty + 2048 + 4).WithNumber(ChainHead.Number +  MultiSyncModeSelector.FastSyncLag + 1).TestObject.Header;

            public static BlockHeader MaliciousPrePivot { get; set; } = Build.A.Block.WithDifficulty(1).WithTotalDifficulty((UInt256) 1000000).WithNumber(512).TestObject.Header;

            public static BlockHeader NewBetterBranchWithLowerNumber { get; set; } = Build.A.Block.WithDifficulty(1).WithTotalDifficulty((UInt256) 1000000).WithNumber(ChainHead.Number - 16).TestObject.Header;

            public static BlockHeader ValidGenesis { get; set; } = Build.A.Block.WithDifficulty(1).WithTotalDifficulty(UInt256.One).Genesis.TestObject.Header;

            public static BlockHeader InvalidGenesis { get; set; } = Build.A.Block.WithDifficulty(1).WithTotalDifficulty(UInt256.One).Genesis.TestObject.Header;

            public static BlockHeader InvalidGenesisWithHighTotalDifficulty { get; set; } = Build.A.Block.Genesis.WithDifficulty((UInt256) 1000000).WithTotalDifficulty((UInt256) 1000000).TestObject.Header;

            public static IEnumerable<BlockHeader> ScenarioHeaders
            {
                get
                {
                    yield return Pivot;
                    yield return MidWayToPivot;
                    yield return ChainHead;
                    yield return ChainHeadWrongDifficulty;
                    yield return ChainHeadParentWrongDifficulty;
                    yield return FutureHead;
                    yield return SlightlyFutureHead;
                    yield return MaliciousPrePivot;
                    yield return NewBetterBranchWithLowerNumber;
                    yield return ValidGenesis;
                    yield return InvalidGenesis;
                    yield return InvalidGenesisWithHighTotalDifficulty;
                }
            }

            public class ScenarioBuilder
            {
                private List<Func<string>> _configActions = new();

                private List<Func<string>> _peeringSetups = new();

                private List<Func<string>> _syncProgressSetups = new();

                private List<Action> _overwrites = new();

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
                    SyncProgressResolver.FindBestFullState().Returns(0);
                    SyncProgressResolver.IsLoadingBlocksFromDb().Returns(false);
                    SyncProgressResolver.IsFastBlocksFinished().Returns(FastBlocksState.None);

                    SyncConfig.FastSync = false;
                    SyncConfig.FastBlocks = false;
                    SyncConfig.BeamSync = false;
                    SyncConfig.PivotNumber = Pivot.Number.ToString();
                    SyncConfig.PivotHash = Keccak.Zero.ToString();
                    SyncConfig.SynchronizationEnabled = true;
                    SyncConfig.NetworkingEnabled = true;
                    SyncConfig.DownloadBodiesInFastSync = true;
                    SyncConfig.DownloadReceiptsInFastSync = true;
                    SyncConfig.FastSyncCatchUpHeightDelta = FastSyncCatchUpHeightDelta;
                }

                private List<ISyncPeer> _peers = new();

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
                            SyncProgressResolver.FindBestFullState().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(ChainHead.Number);
                            SyncProgressResolver.GetTotalDifficulty(Arg.Any<Keccak>()).Returns(info =>
                            {
                                var hash = info.Arg<Keccak>();

                                foreach (BlockHeader scenarioHeader in ScenarioHeaders)
                                {
                                    if (scenarioHeader.Hash == hash)
                                    {
                                        return scenarioHeader.TotalDifficulty;
                                    }
                                    else if (scenarioHeader.ParentHash == hash)
                                    {
                                        return scenarioHeader.TotalDifficulty - scenarioHeader.Difficulty;
                                    }
                                }

                                return null;
                            });
                            SyncProgressResolver.IsFastBlocksFinished().Returns(FastBlocksState.FinishedReceipts);
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
                            SyncProgressResolver.FindBestFullState().Returns(ChainHead.Number - FastSyncCatchUpHeightDelta + 1);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(ChainHead.Number - FastSyncCatchUpHeightDelta + 1);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(FastBlocksState.FinishedReceipts);
                            SyncProgressResolver.ChainDifficulty.Returns(ChainHead.TotalDifficulty ?? 0);
                            return "fully syncing";
                        }
                    );
                    return this;
                }

                public ScenarioBuilder IfPeersMovedForwardBeforeThisNodeProcessedFirstFullBlock()
                {
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestFullBlock().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestFullState().Returns(ChainHead.Number - 2);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(0);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(FastBlocksState.None);
                            SyncProgressResolver.ChainDifficulty.Returns((ChainHead.TotalDifficulty ?? 0) + (UInt256)2);
                            return "fully syncing";
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
                            SyncProgressResolver.FindBestFullState().Returns(0);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(0);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(FastBlocksState.None);
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
                            SyncProgressResolver.FindBestFullState().Returns(0);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(0);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(FastBlocksState.FinishedReceipts);
                            SyncProgressResolver.ChainDifficulty.Returns(UInt256.Zero);
                            return "mid fast sync";
                        }
                    );
                    return this;
                }

                public ScenarioBuilder IfThisNodeJustCameBackFromBeingOfflineForLongTimeAndFinishedFastSyncCatchUp()
                {
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestFullBlock().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestFullState().Returns(ChainHead.Number - FastSyncCatchUpHeightDelta - 1);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(ChainHead.Number - FastSyncCatchUpHeightDelta - 1);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(FastBlocksState.FinishedReceipts);
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
                            SyncProgressResolver.FindBestFullState().Returns(0);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(0);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(FastBlocksState.None);
                            SyncProgressResolver.ChainDifficulty.Returns(UInt256.Zero);
                            return "mid fast blocks but fast sync finished";
                        }
                    );
                    return this;
                }

                public ScenarioBuilder IfThisNodeFinishedStateSyncButNotFastBlocks(FastBlocksState fastBlocksState = FastBlocksState.None)
                {
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(ChainHead.Number - MultiSyncModeSelector.FastSyncLag);
                            SyncProgressResolver.FindBestFullBlock().Returns(0);
                            SyncProgressResolver.FindBestFullState().Returns(ChainHead.Number - MultiSyncModeSelector.FastSyncLag);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(0);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(fastBlocksState);
                            SyncProgressResolver.ChainDifficulty.Returns(UInt256.Zero);
                            return "just finished state sync but not fast blocks";
                        }
                    );
                    return this;
                }

                public ScenarioBuilder IfThisNodeJustFinishedStateSyncAndFastBlocks(FastBlocksState fastBlocksState = FastBlocksState.FinishedReceipts)
                {
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(ChainHead.Number - MultiSyncModeSelector.FastSyncLag);
                            SyncProgressResolver.FindBestFullBlock().Returns(0);
                            SyncProgressResolver.FindBestFullState().Returns(ChainHead.Number - MultiSyncModeSelector.FastSyncLag);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(0);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(fastBlocksState);
                            SyncProgressResolver.ChainDifficulty.Returns(UInt256.Zero);
                            return "just finished state sync and fast blocks";
                        }
                    );
                    return this;
                }

                public ScenarioBuilder IfThisNodeJustFinishedStateSyncButNeedsToCatchUpToHeaders()
                {
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(ChainHead.Number - MultiSyncModeSelector.FastSyncLag);
                            SyncProgressResolver.FindBestFullBlock().Returns(0);
                            SyncProgressResolver.FindBestFullState().Returns(ChainHead.Number - MultiSyncModeSelector.FastSyncLag - 7);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(0);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(FastBlocksState.FinishedReceipts);
                            SyncProgressResolver.ChainDifficulty.Returns(UInt256.Zero);
                            return "just finished state sync and needs to catch up";
                        }
                    );
                    return this;
                }

                public ScenarioBuilder IfThisNodeJustFinishedStateSyncCatchUp()
                {
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestFullBlock().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestFullState().Returns(ChainHead.Number - MultiSyncModeSelector.FastSyncLag);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(0);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(FastBlocksState.FinishedReceipts);
                            SyncProgressResolver.ChainDifficulty.Returns(UInt256.Zero);
                            return "just finished state sync catch up";
                        }
                    );
                    return this;
                }

                public ScenarioBuilder IfThisNodeJustFinishedFastBlocksAndFastSync(FastBlocksState fastBlocksState = FastBlocksState.FinishedReceipts)
                {
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(ChainHead.Number - MultiSyncModeSelector.FastSyncLag);
                            SyncProgressResolver.FindBestFullBlock().Returns(0);
                            SyncProgressResolver.FindBestFullState().Returns(0);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(0);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(fastBlocksState);
                            SyncProgressResolver.ChainDifficulty.Returns(UInt256.Zero);
                            return "just after fast blocks and fast sync";
                        }
                    );
                    return this;
                }

                public ScenarioBuilder IfThisNodeJustStartedFullSyncProcessing(FastBlocksState fastBlocksState = FastBlocksState.FinishedReceipts)
                {
                    long currentBlock = ChainHead.Number - MultiSyncModeSelector.FastSyncLag + 1;
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(currentBlock);
                            SyncProgressResolver.FindBestFullBlock().Returns(currentBlock);
                            SyncProgressResolver.FindBestFullState().Returns(currentBlock);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(0);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(fastBlocksState);
                            SyncProgressResolver.ChainDifficulty.Returns((UInt256) currentBlock);
                            return "just started full sync";
                        }
                    );
                    return this;
                }

                public ScenarioBuilder IfThisNodeRecentlyStartedFullSyncProcessing(FastBlocksState fastBlocksState = FastBlocksState.FinishedReceipts)
                {
                    long currentBlock = ChainHead.Number - MultiSyncModeSelector.FastSyncLag / 2;
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestFullBlock().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestFullState().Returns(currentBlock);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(currentBlock);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(fastBlocksState);
                            SyncProgressResolver.ChainDifficulty.Returns((UInt256) currentBlock);
                            return "recently started full sync";
                        }
                    );
                    return this;
                }

                /// <summary>
                /// Empty clique chains do not update state root on empty blocks (no block reward)
                /// </summary>
                /// <returns></returns>
                public ScenarioBuilder IfThisNodeRecentlyStartedFullSyncProcessingOnEmptyCliqueChain()
                {
                    // so the state root check can think that state root is after processed
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestFullBlock().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestFullState().Returns(ChainHead.Number - MultiSyncModeSelector.FastSyncLag + 1);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(ChainHead.Number - MultiSyncModeSelector.FastSyncLag);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(FastBlocksState.FinishedReceipts);
                            SyncProgressResolver.ChainDifficulty.Returns((UInt256) ChainHead.Number - MultiSyncModeSelector.FastSyncLag);
                            return "recently started full sync on empty clique chain";
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
                            SyncProgressResolver.FindBestFullState().Returns(currentBlock);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(currentBlock);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(FastBlocksState.FinishedReceipts);
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
                            SyncProgressResolver.FindBestFullState().Returns(0);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(currentBlock);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(FastBlocksState.FinishedReceipts);
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
                            SyncProgressResolver.FindBestFullState().Returns(currentBlock);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(currentBlock);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(FastBlocksState.FinishedReceipts);
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

                            SyncProgressResolver.FindBestFullState().Returns(ChainHead.Number - 1);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(ChainHead.Number);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(FastBlocksState.FinishedReceipts);
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

                public ScenarioBuilder AndPeersMovedSlightlyForwardWithFastSyncLag()
                {
                    AddPeeringSetup("peers moved slightly forward", AddPeer(SlightlyFutureHeadWithFastSyncLag));
                    return this;
                }

                public ScenarioBuilder PeersFromDesirableBranchAreKnown()
                {
                    AddPeeringSetup("better branch", AddPeer(NewBetterBranchWithLowerNumber));
                    return this;
                }
                
                public ScenarioBuilder PeersWithWrongDifficultyAreKnown()
                {
                    AddPeeringSetup("wrong difficulty", AddPeer(ChainHeadWrongDifficulty), AddPeer(ChainHeadParentWrongDifficulty));
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

                public ScenarioBuilder ThenInAnyFastSyncConfiguration()
                {
                    WhenBeamSyncIsConfigured();
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
                    var fastBlocksStates = Enum.GetValues(typeof(FastBlocksState)).Cast<FastBlocksState>().ToList();
                    IfThisNodeJustCameBackFromBeingOfflineForLongTimeAndFinishedFastSyncCatchUp();
                    IfThisNodeHasNeverSyncedBefore();
                    IfThisNodeIsFullySynced();
                    IfThisNodeIsProcessingAlreadyDownloadedBlocksInFullSync();
                    IfThisNodeIsInTheMiddleOfFastSyncAndFastBlocks();
                    IfThisNodeFinishedFastBlocksButNotFastSync();
                    fastBlocksStates.ForEach(s => IfThisNodeJustFinishedFastBlocksAndFastSync(s));
                    IfThisNodeFinishedStateSyncButNotFastBlocks();
                    IfThisNodeJustFinishedStateSyncButNeedsToCatchUpToHeaders();
                    fastBlocksStates.ForEach(s => IfThisNodeJustFinishedStateSyncAndFastBlocks(s));
                    fastBlocksStates.ForEach(s => IfThisNodeJustStartedFullSyncProcessing(s));
                    fastBlocksStates.ForEach(s => IfThisNodeRecentlyStartedFullSyncProcessing(s));
                    IfTheSyncProgressIsCorrupted();
                    IfThisNodeNeedsAFastSyncCatchUp();
                    IfThisNodeJustFinishedStateSyncCatchUp();
                    IfThisNodeNearlyNeedsAFastSyncCatchUp();
                    IfThisNodeHasStateThatIsFarInThePast();
                    IfThisNodeRecentlyStartedFullSyncProcessingOnEmptyCliqueChain();
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

                        MultiSyncModeSelector selector = new(SyncProgressResolver, SyncPeerPool, SyncConfig, LimboLogs.Instance);
                        selector.DisableTimer();
                        selector.Update();
                        selector.Current.Should().Be(syncMode);
                    }

                    SetDefaults();

                    if (_syncProgressSetups.Count == 0 || _peeringSetups.Count == 0 || _configActions.Count == 0)
                        throw new ArgumentException($"Invalid test configuration. _syncProgressSetups.Count {_syncProgressSetups.Count}, _peeringSetups.Count {_peeringSetups.Count}, _configActions.Count {_configActions.Count}");
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
                return new();
            }
        }

        [Test]
        public void Genesis_network()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeHasNeverSyncedBefore()
                .AndAPeerWithGenesisOnlyIsKnown()
                .ThenInAnySyncConfiguration()
                .TheSyncModeShouldBe(SyncMode.Disconnected);
        }

        [Test]
        public void Network_with_malicious_genesis()
        {
            // we will ignore the other node because its block is at height 0 (we never sync genesis only)
            Scenario.GoesLikeThis()
                .IfThisNodeHasNeverSyncedBefore()
                .AndAPeerWithHighDiffGenesisOnlyIsKnown()
                .ThenInAnySyncConfiguration()
                .TheSyncModeShouldBe(SyncMode.Disconnected);
        }

        [Test]
        public void Empty_peers_or_no_connection()
        {
            Scenario.GoesLikeThis()
                .WhateverTheSyncProgressIs()
                .AndNoPeersAreKnown()
                .ThenInAnySyncConfiguration()
                .TheSyncModeShouldBe(SyncMode.Disconnected);
        }

        [Test]
        public void Disabled_sync()
        {
            Scenario.GoesLikeThis()
                .WhateverTheSyncProgressIs()
                .WhateverThePeerPoolLooks()
                .WhenSynchronizationIsDisabled()
                .ThenInAnySyncConfiguration()
                .TheSyncModeShouldBe(SyncMode.Disconnected);
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
                .TheSyncModeShouldBe(SyncMode.FastHeaders);
        }

        [Test]
        public void Sync_start_in_beam_sync()
        {
            // note that before we download at least one header we cannot start fast sync
            Scenario.GoesLikeThis()
                .IfThisNodeHasNeverSyncedBefore()
                .AndGoodPeersAreKnown()
                .WhenBeamSyncIsConfigured()
                .TheSyncModeShouldBe(SyncMode.FastHeaders);
        }

        [Test]
        public void In_the_middle_of_fast_sync_with_fast_blocks()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeIsInTheMiddleOfFastSyncAndFastBlocks()
                .AndGoodPeersAreKnown()
                .WhenFastSyncWithFastBlocksIsConfigured()
                .TheSyncModeShouldBe(SyncMode.FastHeaders | SyncMode.FastSync);
        }

        [Test]
        public void In_the_middle_of_fast_sync_with_fast_blocks_with_lesser_peers()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeIsInTheMiddleOfFastSyncAndFastBlocks()
                .AndPeersAreOnlyUsefulForFastBlocks()
                .WhenFastSyncWithFastBlocksIsConfigured()
                .TheSyncModeShouldBe(SyncMode.FastHeaders);
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
        
        [TestCase(FastBlocksState.None)]
        [TestCase(FastBlocksState.FinishedHeaders)]
        public void Finished_fast_sync_but_not_state_sync_and_lesser_peers_are_known_in_fast_blocks(FastBlocksState fastBlocksState)
        {
            Scenario.GoesLikeThis()
                .IfThisNodeJustFinishedFastBlocksAndFastSync(fastBlocksState)
                .AndPeersAreOnlyUsefulForFastBlocks()
                .WhenFastSyncWithFastBlocksIsConfigured()
                .TheSyncModeShouldBe(fastBlocksState.GetSyncMode());
        }

        [Test]
        public void Finished_fast_sync_but_not_state_sync()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeJustFinishedFastBlocksAndFastSync()
                .AndGoodPeersAreKnown()
                .WhenFastSyncWithoutFastBlocksIsConfigured()
                .WhenFastSyncWithFastBlocksIsConfigured()
                .TheSyncModeShouldBe(SyncMode.StateNodes);
        }

        [Test]
        public void Finished_fast_sync_but_not_state_sync_and_fast_blocks_in_progress()
        {
            Scenario.GoesLikeThis()
                .ThisNodeFinishedFastSyncButNotFastBlocks()
                .AndGoodPeersAreKnown()
                .WhenFastSyncWithFastBlocksIsConfigured()
                .TheSyncModeShouldBe(SyncMode.StateNodes | SyncMode.FastHeaders);
        }

        [Test]
        public void Finished_fast_sync_but_not_state_sync_and_fast_blocks_in_progress_and_beam_sync_enabled()
        {
            Scenario.GoesLikeThis()
                .ThisNodeFinishedFastSyncButNotFastBlocks()
                .AndGoodPeersAreKnown()
                .WhenBeamSyncIsConfigured()
                .TheSyncModeShouldBe(SyncMode.StateNodes | SyncMode.FastHeaders | SyncMode.Beam);
        }

        [Test]
        public void Finished_state_node_but_not_fast_blocks()
        {
            Scenario.GoesLikeThis()
                .ThisNodeFinishedFastSyncButNotFastBlocks()
                .AndGoodPeersAreKnown()
                .WhenBeamSyncIsConfigured()
                .TheSyncModeShouldBe(SyncMode.StateNodes | SyncMode.FastHeaders | SyncMode.Beam);
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

        [TestCase(FastBlocksState.FinishedHeaders)]
        [TestCase(FastBlocksState.FinishedBodies)]
        [TestCase(FastBlocksState.FinishedReceipts)]
        public void Just_after_finishing_state_sync_and_fast_blocks(FastBlocksState fastBlocksState)
        {
            Scenario.GoesLikeThis()
                .IfThisNodeJustFinishedStateSyncAndFastBlocks(fastBlocksState)
                .AndGoodPeersAreKnown()
                .WhenBeamSyncIsConfigured()
                .TheSyncModeShouldBe(SyncMode.Full | fastBlocksState.GetSyncMode(true));
        }
        
        [TestCase(FastBlocksState.None)]
        [TestCase(FastBlocksState.FinishedHeaders)]
        [TestCase(FastBlocksState.FinishedBodies)]
        public void Just_after_finishing_state_sync_but_not_fast_blocks(FastBlocksState fastBlocksState)
        {
            Scenario.GoesLikeThis()
                .IfThisNodeFinishedStateSyncButNotFastBlocks(fastBlocksState)
                .AndGoodPeersAreKnown()
                .WhenBeamSyncIsConfigured()
                .TheSyncModeShouldBe(SyncMode.Full | fastBlocksState.GetSyncMode(true));
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
                .ThenInAnyFastSyncConfiguration()
                .TheSyncModeShouldBe(SyncMode.None);
        }

        [Test]
        public void When_just_started_full_sync()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeJustStartedFullSyncProcessing()
                .AndGoodPeersAreKnown()
                .ThenInAnyFastSyncConfiguration()
                .TheSyncModeShouldBe(SyncMode.Full);
        }
        
        [TestCase(FastBlocksState.None)]
        [TestCase(FastBlocksState.FinishedHeaders)]
        [TestCase(FastBlocksState.FinishedBodies)]
        [TestCase(FastBlocksState.FinishedReceipts)]
        public void When_just_started_full_sync_with_fast_blocks(FastBlocksState fastBlocksState)
        {
            Scenario.GoesLikeThis()
                .IfThisNodeJustStartedFullSyncProcessing(fastBlocksState)
                .AndGoodPeersAreKnown()
                .WhenFastSyncWithFastBlocksIsConfigured()
                .TheSyncModeShouldBe(SyncMode.Full | fastBlocksState.GetSyncMode(true));
        }

        [Test]
        public void When_just_started_full_sync_and_peers_moved_forward()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeJustStartedFullSyncProcessing()
                .AndPeersMovedForward()
                .ThenInAnyFastSyncConfiguration()
                .TheSyncModeShouldBe(SyncMode.Full);
        }

        [Description("Fixes this scenario: // 2020-04-23 19:46:46.0143|INFO|180|Changing state to Full at processed:0|beam state:9930654|state:9930654|block:0|header:9930654|peer block:9930686 // 2020-04-23 19:46:47.0361|INFO|68|Changing state to StateNodes at processed:0|beam state:9930654|state:9930654|block:9930686|header:9930686|peer block:9930686")]
        [Test]
        public void When_just_started_full_sync_and_peers_moved_slightly_forward()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeJustStartedFullSyncProcessing()
                .AndPeersMovedSlightlyForward()
                .ThenInAnyFastSyncConfiguration()
                .TheSyncModeShouldBe(SyncMode.Full);
        }

        [Test]
        public void When_recently_started_full_sync()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeRecentlyStartedFullSyncProcessing()
                .AndGoodPeersAreKnown()
                .ThenInAnyFastSyncConfiguration()
                .TheSyncModeShouldBe(SyncMode.Full);
        }
        
        [Test]
        public void When_recently_started_full_sync_on_empty_clique_chain()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeRecentlyStartedFullSyncProcessingOnEmptyCliqueChain()
                .AndGoodPeersAreKnown()
                .ThenInAnyFastSyncConfiguration()
                .TheSyncModeShouldBe(SyncMode.Full);
        }

        [Test]
        public void When_progress_is_corrupted()
        {
            Scenario.GoesLikeThis()
                .IfTheSyncProgressIsCorrupted()
                .AndGoodPeersAreKnown()
                .WhenBeamSyncIsConfigured()
                .TheSyncModeShouldBe(SyncMode.WaitingForBlock);
        }

        [Test]
        public void Waiting_for_processor()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeIsProcessingAlreadyDownloadedBlocksInFullSync()
                .AndGoodPeersAreKnown()
                .WhenBeamSyncIsConfigured()
                .TheSyncModeShouldBe(SyncMode.WaitingForBlock);
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
                .ThenInAnyFastSyncConfiguration()
                .TheSyncModeShouldBe(SyncMode.Full);
        }
        
        [Test]
        public void Should_not_sync_when_synced_and_peer_reports_wrong_higher_total_difficulty()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeIsFullySynced()
                .PeersWithWrongDifficultyAreKnown()
                .ThenInAnyFastSyncConfiguration()
                .TheSyncModeShouldBe(SyncMode.WaitingForBlock);
        }

        [Test]
        public void Fast_sync_catch_up()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeNeedsAFastSyncCatchUp()
                .AndGoodPeersAreKnown()
                .ThenInAnyFastSyncConfiguration()
                .TheSyncModeShouldBe(SyncMode.FastSync);
        }

        [Test]
        public void Nearly_fast_sync_catch_up()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeNearlyNeedsAFastSyncCatchUp()
                .AndGoodPeersAreKnown()
                .ThenInAnyFastSyncConfiguration()
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
                .IfThisNodeJustFinishedFastBlocksAndFastSync(FastBlocksState.FinishedHeaders)
                .AndPeersMovedSlightlyForward()
                .WhenBeamSyncIsConfigured()
                .TheSyncModeShouldBe(SyncMode.StateNodes | SyncMode.FastSync | SyncMode.Beam);
        }

        [TestCase(FastBlocksState.None)]
        [TestCase(FastBlocksState.FinishedHeaders)]
        public void When_peers_move_slightly_forward_when_state_syncing_without_beam(FastBlocksState fastBlocksState)
        {
            Scenario.GoesLikeThis()
                .IfThisNodeJustFinishedFastBlocksAndFastSync(fastBlocksState)
                .AndPeersMovedSlightlyForward()
                .WhenFastSyncWithFastBlocksIsConfigured()
                .TheSyncModeShouldBe(SyncMode.StateNodes | SyncMode.FastSync | fastBlocksState.GetSyncMode());
        }

        [Test]
        public void When_state_sync_finished_but_needs_to_catch_up()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeJustFinishedStateSyncButNeedsToCatchUpToHeaders()
                .AndGoodPeersAreKnown()
                .WhenBeamSyncIsConfigured()
                .TheSyncModeShouldBe(SyncMode.StateNodes | SyncMode.Beam);
        }

        /// <summary>
        /// we DO NOT want the thing like below to happen (incorrectly go back to StateNodes from Full)
        /// 2020-04-25 19:58:32.1466|INFO|254|Changing state to Full at processed:0|beam state:9943624|state:9943624|block:0|header:9943624|peer block:9943656
        /// 2020-04-25 19:58:32.1466|INFO|254|Sync mode changed from StateNodes to Full
        /// 2020-04-25 19:58:33.1652|INFO|266|Changing state to StateNodes at processed:0|beam state:9943624|state:9943624|block:9943656|header:9943656|peer block:9943656
        /// </summary>
        [Test]
        public void When_state_sync_just_caught_up()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeJustFinishedStateSyncCatchUp()
                .AndGoodPeersAreKnown()
                .ThenInAnyFastSyncConfiguration()
                .TheSyncModeShouldBe(SyncMode.Full);
        }

        /// <summary>
        /// We should switch to State Sync in a case like below
        /// 2020-04-27 11:48:30.6691|Changing state to StateNodes at processed:2594949|beam state:2594949|state:2594949|block:2596807|header:2596807|peer block:2596807
        /// </summary>
        [Test]
        public void When_long_range_state_catch_up_is_needed()
        {
            Scenario.GoesLikeThis()
                .IfThisNodeJustCameBackFromBeingOfflineForLongTimeAndFinishedFastSyncCatchUp()
                .AndGoodPeersAreKnown()
                .WhenBeamSyncIsConfigured()
                .TheSyncModeShouldBe(SyncMode.StateNodes | SyncMode.Beam);
        }

        [Test]
        public void Does_not_move_back_to_state_sync_mistakenly_when_in_full_sync_because_of_thinking_that_it_needs_to_catch_up()
        {
            Scenario.GoesLikeThis()
                .IfPeersMovedForwardBeforeThisNodeProcessedFirstFullBlock()
                .AndPeersMovedSlightlyForwardWithFastSyncLag()
                .WhenFastSyncWithFastBlocksIsConfigured()
                .TheSyncModeShouldBe(SyncMode.Full | SyncMode.FastHeaders);
        }
        
        [Test]
        public void Switch_correctly_from_full_sync_to_state_nodes_catch_up()
        {
            ISyncProgressResolver syncProgressResolver = Substitute.For<ISyncProgressResolver>();
            syncProgressResolver.FindBestHeader().Returns(Scenario.ChainHead.Number);
            syncProgressResolver.FindBestFullBlock().Returns(Scenario.ChainHead.Number);
            syncProgressResolver.FindBestFullState().Returns(Scenario.ChainHead.Number - MultiSyncModeSelector.FastSyncLag);
            syncProgressResolver.FindBestProcessedBlock().Returns(0);
            syncProgressResolver.IsFastBlocksFinished().Returns(FastBlocksState.FinishedReceipts);
            syncProgressResolver.ChainDifficulty.Returns(UInt256.Zero);

            List<ISyncPeer> syncPeers = new();

            BlockHeader header = Scenario.ChainHead;
            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            syncPeer.HeadHash.Returns(header.Hash);
            syncPeer.HeadNumber.Returns(header.Number);
            syncPeer.TotalDifficulty.Returns(header.TotalDifficulty ?? 0);
            syncPeer.IsInitialized.Returns(true);
            syncPeer.ClientId.Returns("nethermind");
            
            syncPeers.Add(syncPeer);
            ISyncPeerPool syncPeerPool = Substitute.For<ISyncPeerPool>();
            IEnumerable<PeerInfo> peerInfos = syncPeers.Select(p => new PeerInfo(p));
            syncPeerPool.InitializedPeers.Returns(peerInfos);
            syncPeerPool.AllPeers.Returns(peerInfos);

            ISyncConfig syncConfig = new SyncConfig() {FastSyncCatchUpHeightDelta = 2};
            syncConfig.FastSync = true;
            
            MultiSyncModeSelector selector = new(syncProgressResolver, syncPeerPool, syncConfig, LimboLogs.Instance);
            selector.DisableTimer();
            syncProgressResolver.FindBestProcessedBlock().Returns(Scenario.ChainHead.Number);
            selector.Update();
            selector.Current.Should().Be(SyncMode.Full);

            for (uint i = 0; i < syncConfig.FastSyncCatchUpHeightDelta + 1; i++)
            {
                long number = header.Number + i;
                syncPeer.HeadNumber.Returns(number);
                syncPeer.TotalDifficulty.Returns(header.TotalDifficulty.Value + i);
                syncProgressResolver.FindBestHeader().Returns(number);
                syncProgressResolver.FindBestFullBlock().Returns(number);
                selector.Update();
            }

            selector.Current.Should().Be(SyncMode.StateNodes);
        }
    }
    
    public enum FastBlocksState
    {
        None,
        FinishedHeaders,
        FinishedBodies,
        FinishedReceipts
    }
    
    internal static class Extensions
    {
        public static SyncMode GetSyncMode(this FastBlocksState state, bool isFullSync = false)
        {
            switch (state)
            {
                case FastBlocksState.None:
                    return SyncMode.FastHeaders;
                case FastBlocksState.FinishedHeaders:
                    return isFullSync ? SyncMode.FastBodies : SyncMode.None;
                case FastBlocksState.FinishedBodies:
                    return isFullSync ? SyncMode.FastReceipts : SyncMode.None;
                default:
                    return SyncMode.None;
            }
        }
        
        public static FastBlocksFinishedState IsFastBlocksFinished(this ISyncProgressResolver syncProgressResolver)
        {
            return new(syncProgressResolver);
        }

        internal class FastBlocksFinishedState
        {
            private readonly ISyncProgressResolver _syncProgressResolver;

            public FastBlocksFinishedState(ISyncProgressResolver syncProgressResolver)
            {
                _syncProgressResolver = syncProgressResolver;
            }
            
            public void Returns(FastBlocksState returns)
            {
                _syncProgressResolver.IsFastBlocksHeadersFinished().Returns(returns >= FastBlocksState.FinishedHeaders);
                _syncProgressResolver.IsFastBlocksBodiesFinished().Returns(returns >= FastBlocksState.FinishedBodies);
                _syncProgressResolver.IsFastBlocksReceiptsFinished().Returns(returns >= FastBlocksState.FinishedReceipts);
            }
        }
    }
}
