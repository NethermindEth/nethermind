// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Json;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Eth;
using Nethermind.Logging;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using SyncMetrics = Nethermind.Synchronization.Metrics;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Facade.Test.Eth
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class EthSyncingInfoTests
    {
        [Test]
        public void GetFullInfo_WhenNotSyncing()
        {
            ISyncConfig syncConfig = new SyncConfig();
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            ISyncPointers syncPointers = Substitute.For<ISyncPointers>();
            ISyncProgressResolver syncProgressResolver = Substitute.For<ISyncProgressResolver>();
            syncProgressResolver.IsFastBlocksBodiesFinished().Returns(false);
            syncProgressResolver.IsFastBlocksReceiptsFinished().Returns(false);
            blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(6178001UL).TestObject);
            blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(6178000UL).TestObject).TestObject);
            EthSyncingInfo ethSyncingInfo = new(blockTree, syncPointers, syncConfig,
                new StaticSelector(SyncMode.All), syncProgressResolver, No.BeaconSync, LimboLogs.Instance);
            SyncingResult syncingResult = ethSyncingInfo.GetFullInfo();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(syncingResult.IsSyncing, Is.EqualTo(false));
                Assert.That(syncingResult.CurrentBlock, Is.EqualTo(0UL));
                Assert.That(syncingResult.HighestBlock, Is.EqualTo(0UL));
                Assert.That(syncingResult.StartingBlock, Is.EqualTo(0UL));
                Assert.That(syncingResult.SyncMode, Is.EqualTo(SyncMode.None));
            }
        }

        [Test]
        public void GetFullInfo_WhenSyncing()
        {
            ISyncConfig syncConfig = new SyncConfig();
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            ISyncPointers syncPointers = Substitute.For<ISyncPointers>();
            ISyncProgressResolver syncProgressResolver = Substitute.For<ISyncProgressResolver>();
            syncProgressResolver.IsFastBlocksBodiesFinished().Returns(false);
            syncProgressResolver.IsFastBlocksReceiptsFinished().Returns(false);
            blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(6178010UL).TestObject);
            blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(6178000UL).TestObject).TestObject);
            EthSyncingInfo ethSyncingInfo = new(blockTree, syncPointers, syncConfig,
                new StaticSelector(SyncMode.All), syncProgressResolver, No.BeaconSync, LimboLogs.Instance);
            SyncingResult syncingResult = ethSyncingInfo.GetFullInfo();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(syncingResult.IsSyncing, Is.EqualTo(true));
                Assert.That(syncingResult.CurrentBlock, Is.EqualTo(6178000UL));
                Assert.That(syncingResult.HighestBlock, Is.EqualTo(6178010UL));
                Assert.That(syncingResult.StartingBlock, Is.EqualTo(6178000UL));
                Assert.That(syncingResult.SyncMode, Is.EqualTo(SyncMode.All));
            }
        }

        [TestCase(6178001UL, 6178000UL, false)]
        [TestCase(6178010UL, 6178000UL, true)]
        public void IsSyncing_ReturnsExpectedResult(ulong bestHeader, ulong currentHead, bool expectedResult)
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            ISyncPointers syncPointers = Substitute.For<ISyncPointers>();
            ISyncProgressResolver syncProgressResolver = Substitute.For<ISyncProgressResolver>();
            syncProgressResolver.IsFastBlocksBodiesFinished().Returns(false);
            syncProgressResolver.IsFastBlocksReceiptsFinished().Returns(false);
            blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(bestHeader).TestObject);
            blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(currentHead).TestObject).TestObject);
            EthSyncingInfo ethSyncingInfo = new(blockTree, syncPointers, new SyncConfig(),
                new StaticSelector(SyncMode.All), syncProgressResolver, No.BeaconSync, LimboLogs.Instance);
            SyncingResult syncingResult = ethSyncingInfo.GetFullInfo();
            Assert.That(syncingResult.IsSyncing, Is.EqualTo(expectedResult));
        }

        [Test]
        public void IsSyncing_StillReportsHeadDistanceWhenSynchronizationDisabled()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            ISyncPointers syncPointers = Substitute.For<ISyncPointers>();
            ISyncProgressResolver syncProgressResolver = Substitute.For<ISyncProgressResolver>();
            SyncConfig syncConfig = new() { SynchronizationEnabled = false };
            blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(100UL).TestObject);
            blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(80UL).TestObject).TestObject);
            EthSyncingInfo ethSyncingInfo = new(blockTree, syncPointers, syncConfig,
                new StaticSelector(SyncMode.All), syncProgressResolver, No.BeaconSync, LimboLogs.Instance);

            SyncingResult syncingResult = ethSyncingInfo.GetFullInfo();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(syncingResult.IsSyncing, Is.True);
                Assert.That(syncingResult.CurrentBlock, Is.EqualTo(80UL));
                Assert.That(syncingResult.HighestBlock, Is.EqualTo(100UL));
            }
        }

        [TestCase(10005UL, 10000UL, 130000UL, true, 130000UL, TestName = "BeaconTargetFarAhead")]
        [TestCase(20000UL, 10000UL, 15000UL, true, 20000UL, TestName = "BeaconTargetBelowBestSuggested")]
        [TestCase(10005UL, 10000UL, 10000UL, false, 0UL, TestName = "BeaconTargetCaughtUp")]
        [TestCase(10005UL, 10000UL, null, false, 0UL, TestName = "BeaconSyncFinished")]
        [TestCase(0UL, 0UL, 1UL, true, 1UL, TestName = "AtGenesisWithNearbyBeaconTarget")]
        [TestCase(0UL, 0UL, 8UL, true, 8UL, TestName = "AtGenesisWithBeaconTargetAtMaxDistance")]
        public void GetFullInfo_UsesBeaconSyncTarget(ulong bestSuggested, ulong head, ulong? beaconTarget, bool expectedSyncing, ulong expectedHighest)
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            ISyncProgressResolver syncProgressResolver = Substitute.For<ISyncProgressResolver>();
            syncProgressResolver.IsFastBlocksBodiesFinished().Returns(true);
            syncProgressResolver.IsFastBlocksReceiptsFinished().Returns(true);
            blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(bestSuggested).TestObject);
            // Abandoned-fork high-water mark that must not keep the node reporting syncing
            blockTree.BestSuggestedBeaconHeader.Returns(Build.A.BlockHeader.WithNumber(130000UL).TestObject);
            blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(head).TestObject).TestObject);
            IBeaconSyncStrategy beaconSyncStrategy = Substitute.For<IBeaconSyncStrategy>();
            beaconSyncStrategy.GetTargetBlockHeight().Returns(beaconTarget);

            EthSyncingInfo ethSyncingInfo = new(blockTree, Substitute.For<ISyncPointers>(), new SyncConfig(),
                new StaticSelector(SyncMode.WaitingForBlock), syncProgressResolver, beaconSyncStrategy, LimboLogs.Instance);

            SyncingResult syncingResult = ethSyncingInfo.GetFullInfo();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(syncingResult.IsSyncing, Is.EqualTo(expectedSyncing));
                Assert.That(syncingResult.HighestBlock, Is.EqualTo(expectedHighest));
            }
        }

        [TestCase(false, true, true)]
        [TestCase(true, false, true)]
        [TestCase(false, false, true)]
        [TestCase(true, true, false)]
        public void IsSyncing_AncientBarriers(bool resolverDownloadingBodies,
            bool resolverDownloadingReceipts, bool expectedResult)
        {
            ISyncConfig syncConfig = new SyncConfig
            {
                FastSync = true,
                AncientBodiesBarrier = 800,
                // AncientBodiesBarrierCalc = Max(1, Min(Pivot, BodiesBarrier)) = BodiesBarrier = 800
                AncientReceiptsBarrier = 900,
                // AncientReceiptsBarrierCalc = Max(1, Min(Pivot, Max(BodiesBarrier, ReceiptsBarrier))) = ReceiptsBarrier = 900
                DownloadBodiesInFastSync = true,
                DownloadReceiptsInFastSync = true,
                PivotNumber = 1000
            };
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            blockTree.SyncPivot.Returns((1000UL, Keccak.Zero));
            ISyncPointers syncPointers = Substitute.For<ISyncPointers>();
            ISyncProgressResolver syncProgressResolver = Substitute.For<ISyncProgressResolver>();
            syncProgressResolver.IsFastBlocksBodiesFinished().Returns(resolverDownloadingBodies);
            syncProgressResolver.IsFastBlocksReceiptsFinished().Returns(resolverDownloadingReceipts);

            blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(6178001UL).TestObject);
            blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(6178000UL).TestObject).TestObject);

            EthSyncingInfo ethSyncingInfo = new(blockTree, syncPointers, syncConfig,
                new StaticSelector(SyncMode.FastBlocks), syncProgressResolver, No.BeaconSync, LimboLogs.Instance);
            SyncingResult syncingResult = ethSyncingInfo.GetFullInfo();
            Assert.That(syncingResult, Is.EqualTo(CreateSyncingResult(expectedResult, 6178000UL, 6178001UL, SyncMode.FastBlocks)));
        }

        [Test]
        public void Should_calculate_sync_time()
        {
            SyncConfig syncConfig = new();
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            ISyncPointers syncPointers = Substitute.For<ISyncPointers>();
            ISyncProgressResolver syncProgressResolver = Substitute.For<ISyncProgressResolver>();
            syncProgressResolver.IsFastBlocksBodiesFinished().Returns(false);
            syncProgressResolver.IsFastBlocksReceiptsFinished().Returns(false);

            blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(100UL).TestObject);
            blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(100UL).TestObject)
                .TestObject);

            EthSyncingInfo ethSyncingInfo = new(blockTree, syncPointers, syncConfig,
                new StaticSelector(SyncMode.All), syncProgressResolver, No.BeaconSync, LimboLogs.Instance);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(ethSyncingInfo.IsSyncing(), Is.EqualTo(false));
                Assert.That(ethSyncingInfo.UpdateAndGetSyncTime().TotalMicroseconds, Is.EqualTo(0));
            }

            blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(100UL).TestObject);
            blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(80UL).TestObject)
                .TestObject);

            // First call starts the timer; at the metric's whole-second resolution this is still 0
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ethSyncingInfo.IsSyncing(), Is.EqualTo(true));
                Assert.That((long)ethSyncingInfo.UpdateAndGetSyncTime().TotalSeconds, Is.EqualTo(0));
            }

            Thread.Sleep(100);

            // While syncing the timer accumulates
            Assert.That(ethSyncingInfo.IsSyncing(), Is.EqualTo(true));
            TimeSpan whileSyncing = ethSyncingInfo.UpdateAndGetSyncTime();
            Assert.That(whileSyncing, Is.GreaterThan(TimeSpan.Zero));

            // Sync ended: the total is retained (not reset to zero) so the final duration stays observable
            blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(100UL).TestObject);
            blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(100UL).TestObject)
                .TestObject);

            Assert.That(ethSyncingInfo.IsSyncing(), Is.EqualTo(false));
            TimeSpan afterSync = ethSyncingInfo.UpdateAndGetSyncTime();
            Assert.That(afterSync, Is.GreaterThanOrEqualTo(whileSyncing));

            // Falling behind again resumes (does not reset) the total — no false drop to zero
            blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(80UL).TestObject)
                .TestObject);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ethSyncingInfo.IsSyncing(), Is.EqualTo(true));
                Assert.That(ethSyncingInfo.UpdateAndGetSyncTime(), Is.GreaterThanOrEqualTo(afterSync));
            }
        }

        [TestCase(6178001UL, 6178000UL)]
        [TestCase(8001UL, 8000UL)]
        public void IsSyncing_ReturnsFalseOnFastSyncWithoutPivot(ulong bestHeader, ulong currentHead)
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            ISyncPointers syncPointers = Substitute.For<ISyncPointers>();
            ISyncProgressResolver syncProgressResolver = Substitute.For<ISyncProgressResolver>();
            syncProgressResolver.IsFastBlocksBodiesFinished().Returns(false);
            syncProgressResolver.IsFastBlocksReceiptsFinished().Returns(false);
            blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(bestHeader).TestObject);
            blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(currentHead).TestObject).TestObject);
            SyncConfig syncConfig = new()
            {
                FastSync = true,
                SnapSync = true,
                PivotNumber = 0, // Equivalent to not having a pivot
            };
            EthSyncingInfo ethSyncingInfo = new(blockTree, syncPointers, syncConfig,
                new StaticSelector(SyncMode.All), syncProgressResolver, No.BeaconSync, LimboLogs.Instance);
            SyncingResult syncingResult = ethSyncingInfo.GetFullInfo();

            Assert.That(syncingResult.IsSyncing, Is.False);
        }

        [Test]
        public void StartingBlock_is_head_when_sync_run_started()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IBeaconSyncStrategy beaconSyncStrategy = Substitute.For<IBeaconSyncStrategy>();
            ISyncProgressResolver syncProgressResolver = Substitute.For<ISyncProgressResolver>();
            syncProgressResolver.IsFastBlocksBodiesFinished().Returns(true);
            syncProgressResolver.IsFastBlocksReceiptsFinished().Returns(true);
            blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(100UL).TestObject);
            EthSyncingInfo ethSyncingInfo = new(blockTree, Substitute.For<ISyncPointers>(), new SyncConfig(),
                new StaticSelector(SyncMode.Full), syncProgressResolver, beaconSyncStrategy, LimboLogs.Instance);

            SetHead(100UL);
            Assert.That(ethSyncingInfo.GetFullInfo().IsSyncing, Is.False);

            beaconSyncStrategy.GetTargetBlockHeight().Returns(200UL);
            Assert.That(ethSyncingInfo.GetFullInfo().StartingBlock, Is.EqualTo(100UL));

            SetHead(150UL);
            SyncingResult syncingResult = ethSyncingInfo.GetFullInfo();
            Assert.That((syncingResult.StartingBlock, syncingResult.CurrentBlock, syncingResult.HighestBlock), Is.EqualTo((100UL, 150UL, 200UL)));

            SetHead(200UL);
            Assert.That(ethSyncingInfo.GetFullInfo().IsSyncing, Is.False);

            beaconSyncStrategy.GetTargetBlockHeight().Returns(300UL);
            Assert.That(ethSyncingInfo.GetFullInfo().StartingBlock, Is.EqualTo(200UL));

            void SetHead(ulong number) =>
                blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(number).TestObject).TestObject);
        }

        [TestCase(true, SyncMode.StateNodes | SyncMode.FastBodies, 3500L, 3500UL)]
        [TestCase(true, SyncMode.StateNodes, 12000L, 10000UL)]
        [TestCase(true, SyncMode.FastBodies, 3500L, null)]
        [TestCase(false, SyncMode.StateNodes, 3500L, null)]
        [NonParallelizable]
        public void GetFullInfo_reports_snap_progress(bool snapSync, SyncMode syncMode, long rangesProgress, ulong? expectedProcessed)
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(0UL).TestObject).TestObject);
            IBeaconSyncStrategy beaconSyncStrategy = Substitute.For<IBeaconSyncStrategy>();
            beaconSyncStrategy.GetTargetBlockHeight().Returns(20_000_000UL);
            EthSyncingInfo ethSyncingInfo = new(blockTree, Substitute.For<ISyncPointers>(), new SyncConfig { SnapSync = snapSync },
                new StaticSelector(syncMode), Substitute.For<ISyncProgressResolver>(), beaconSyncStrategy, LimboLogs.Instance);

            SetSyncMetrics(rangesProgress, 1, 2, 3, 4, 5, 6);
            try
            {
                SyncingResult syncingResult = ethSyncingInfo.GetFullInfo();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That((syncingResult.StartingBlock, syncingResult.CurrentBlock, syncingResult.HighestBlock), Is.EqualTo((0UL, 0UL, 20_000_000UL)));
                    Assert.That(syncingResult.WarpChunksProcessed, Is.EqualTo(expectedProcessed));
                    if (expectedProcessed is null)
                    {
                        Assert.That(syncingResult.WarpChunksAmount, Is.Null);
                        Assert.That(syncingResult.SyncedAccounts, Is.Null);
                        Assert.That(syncingResult.HealedTrienodes, Is.Null);
                    }
                    else
                    {
                        Assert.That(syncingResult.WarpChunksAmount, Is.EqualTo(10_000UL));
                        Assert.That((syncingResult.SyncedAccounts, syncingResult.SyncedStorage, syncingResult.SyncedBytecodes), Is.EqualTo(((ulong?)1, (ulong?)2, (ulong?)3)));
                        Assert.That((syncingResult.HealedTrienodes, syncingResult.HealedBytecodes), Is.EqualTo(((ulong?)10, (ulong?)5)));
                    }
                }
            }
            finally
            {
                SetSyncMetrics(0, 0, 0, 0, 0, 0, 0);
            }
        }

        private static IEnumerable<TestCaseData> SerializationCases()
        {
            yield return new TestCaseData(SyncingResult.NotSyncing, "false").SetName("NotSyncing");
            yield return new TestCaseData(
                new SyncingResult { IsSyncing = true, StartingBlock = 100, CurrentBlock = 150, HighestBlock = 200, SyncMode = SyncMode.Full },
                """{"startingBlock":"0x64","currentBlock":"0x96","highestBlock":"0xc8"}""")
                .SetName("BlockSync");
            yield return new TestCaseData(
                new SyncingResult
                {
                    IsSyncing = true,
                    HighestBlock = 20_000_000,
                    SyncMode = SyncMode.StateNodes,
                    SyncedAccounts = 1000,
                    SyncedStorage = 2000,
                    SyncedBytecodes = 30,
                    HealedTrienodes = 40,
                    HealedBytecodes = 5,
                    WarpChunksAmount = 10_000,
                    WarpChunksProcessed = 3500
                },
                """{"startingBlock":"0x0","currentBlock":"0x0","highestBlock":"0x1312d00","syncedAccounts":"0x3e8","syncedStorage":"0x7d0","syncedBytecodes":"0x1e","healedTrienodes":"0x28","healedBytecodes":"0x5","warpChunksAmount":"0x2710","warpChunksProcessed":"0xdac"}""")
                .SetName("SnapStateSync");
        }

        [TestCaseSource(nameof(SerializationCases))]
        public void Serializes_syncing_result(SyncingResult syncingResult, string expectedJson) =>
            Assert.That(new EthereumJsonSerializer().Serialize(syncingResult), Is.EqualTo(expectedJson));

        private static void SetSyncMetrics(long rangesProgress, long accounts, long storage, long codes, long stateNodes, long healedCodes, long storageNodes)
        {
            SyncMetrics.SnapRangesProgress = rangesProgress;
            SyncMetrics.SnapSyncedAccounts = accounts;
            SyncMetrics.SnapSyncedStorageSlots = storage;
            SyncMetrics.SnapSyncedCodes = codes;
            SyncMetrics.SyncedStateTrieNodes = stateNodes;
            SyncMetrics.SyncedCodes = healedCodes;
            SyncMetrics.SyncedStorageTrieNodes = storageNodes;
        }

        private SyncingResult CreateSyncingResult(bool isSyncing, ulong currentBlock, ulong highestBlock, SyncMode syncMode)
        {
            if (!isSyncing) return SyncingResult.NotSyncing;
            return new SyncingResult { CurrentBlock = currentBlock, HighestBlock = highestBlock, IsSyncing = true, StartingBlock = currentBlock, SyncMode = syncMode };
        }
    }
}
