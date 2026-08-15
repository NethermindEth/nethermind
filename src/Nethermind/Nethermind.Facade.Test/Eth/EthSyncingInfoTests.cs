// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Eth;
using Nethermind.Logging;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
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
            Assert.That(syncingResult.IsSyncing, Is.EqualTo(false));
            Assert.That(syncingResult.CurrentBlock, Is.EqualTo(0UL));
            Assert.That(syncingResult.HighestBlock, Is.EqualTo(0UL));
            Assert.That(syncingResult.StartingBlock, Is.EqualTo(0UL));
            Assert.That(syncingResult.SyncMode, Is.EqualTo(SyncMode.None));
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
            Assert.That(syncingResult.IsSyncing, Is.EqualTo(true));
            Assert.That(syncingResult.CurrentBlock, Is.EqualTo(6178000UL));
            Assert.That(syncingResult.HighestBlock, Is.EqualTo(6178010UL));
            Assert.That(syncingResult.StartingBlock, Is.EqualTo(0UL));
            Assert.That(syncingResult.SyncMode, Is.EqualTo(SyncMode.All));
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

        /// <summary>
        /// CL-driven forward sync keeps Head close to BestSuggestedHeader while the current
        /// FCU/beacon destination (GetTargetBlockHeight) remains far ahead. eth_syncing must
        /// use that destination or it reports false for the entire catch-up (see #12673).
        /// BestSuggestedBeaconHeader is deliberately unused: it is not the current CL target.
        /// </summary>
        [Test]
        public void GetFullInfo_WhenHeadTracksSuggestedButBeaconTipIsAhead_ReportsSyncing()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            ISyncPointers syncPointers = Substitute.For<ISyncPointers>();
            ISyncProgressResolver syncProgressResolver = Substitute.For<ISyncProgressResolver>();
            syncProgressResolver.IsFastBlocksBodiesFinished().Returns(true);
            syncProgressResolver.IsFastBlocksReceiptsFinished().Returns(true);
            blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(10005UL).TestObject);
            blockTree.BestSuggestedBeaconHeader.Returns((BlockHeader?)null);
            blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(10000UL).TestObject).TestObject);

            IBeaconSyncStrategy beaconSyncStrategy = Substitute.For<IBeaconSyncStrategy>();
            beaconSyncStrategy.GetTargetBlockHeight().Returns(130000UL);

            EthSyncingInfo ethSyncingInfo = new(blockTree, syncPointers, new SyncConfig(),
                new StaticSelector(SyncMode.WaitingForBlock), syncProgressResolver, beaconSyncStrategy, LimboLogs.Instance);

            SyncingResult syncingResult = ethSyncingInfo.GetFullInfo();

            Assert.That(syncingResult.IsSyncing, Is.True);
            Assert.That(syncingResult.CurrentBlock, Is.EqualTo(10000UL));
            Assert.That(syncingResult.HighestBlock, Is.EqualTo(130000UL));
            Assert.That(syncingResult.SyncMode, Is.EqualTo(SyncMode.WaitingForBlock));
        }

        /// <summary>
        /// An abandoned higher beacon fork can leave BestSuggestedBeaconHeader stuck above Head
        /// after FCU moved to a lower canonical head. eth_syncing must follow GetTargetBlockHeight
        /// (current FCU/pivot destination), not that historical high-water mark.
        /// </summary>
        [Test]
        public void GetFullInfo_AbandonedBeaconHighWater_CurrentTargetCaughtUp_DoesNotKeepReportingSyncing()
        {
            AssertAbandonedHighWaterDoesNotKeepSyncing(10000UL);
        }

        [Test]
        public void GetFullInfo_AbandonedBeaconHighWater_BeaconSyncFinished_DoesNotKeepReportingSyncing()
        {
            AssertAbandonedHighWaterDoesNotKeepSyncing(null);
        }

        private static void AssertAbandonedHighWaterDoesNotKeepSyncing(ulong? targetHeight)
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            ISyncPointers syncPointers = Substitute.For<ISyncPointers>();
            ISyncProgressResolver syncProgressResolver = Substitute.For<ISyncProgressResolver>();
            syncProgressResolver.IsFastBlocksBodiesFinished().Returns(true);
            syncProgressResolver.IsFastBlocksReceiptsFinished().Returns(true);
            blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(10005UL).TestObject);
            blockTree.BestSuggestedBeaconHeader.Returns(Build.A.BlockHeader.WithNumber(130000UL).TestObject);
            blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(10000UL).TestObject).TestObject);

            IBeaconSyncStrategy beaconSyncStrategy = Substitute.For<IBeaconSyncStrategy>();
            beaconSyncStrategy.GetTargetBlockHeight().Returns(targetHeight);

            EthSyncingInfo ethSyncingInfo = new(blockTree, syncPointers, new SyncConfig(),
                new StaticSelector(SyncMode.WaitingForBlock), syncProgressResolver, beaconSyncStrategy, LimboLogs.Instance);

            SyncingResult syncingResult = ethSyncingInfo.GetFullInfo();

            Assert.That(syncingResult.IsSyncing, Is.False);
            Assert.That(syncingResult.HighestBlock, Is.Not.EqualTo(130000UL));
            Assert.That(syncingResult, Is.EqualTo(SyncingResult.NotSyncing));
        }

        [TestCase(6178001UL, 6178000UL, false)]
        [TestCase(6178010UL, 6178000UL, true)]
        public void GetFullInfo_WithNoBeaconSync_IgnoresBestSuggestedBeaconHeader(ulong bestHeader, ulong currentHead, bool expectedSyncing)
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            ISyncPointers syncPointers = Substitute.For<ISyncPointers>();
            ISyncProgressResolver syncProgressResolver = Substitute.For<ISyncProgressResolver>();
            syncProgressResolver.IsFastBlocksBodiesFinished().Returns(true);
            syncProgressResolver.IsFastBlocksReceiptsFinished().Returns(true);
            blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(bestHeader).TestObject);
            blockTree.BestSuggestedBeaconHeader.Returns(Build.A.BlockHeader.WithNumber(130000UL).TestObject);
            blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(currentHead).TestObject).TestObject);

            EthSyncingInfo ethSyncingInfo = new(blockTree, syncPointers, new SyncConfig(),
                new StaticSelector(SyncMode.All), syncProgressResolver, No.BeaconSync, LimboLogs.Instance);

            SyncingResult syncingResult = ethSyncingInfo.GetFullInfo();

            Assert.That(syncingResult.IsSyncing, Is.EqualTo(expectedSyncing));
            Assert.That(syncingResult.HighestBlock, Is.Not.EqualTo(130000UL));
            if (expectedSyncing)
            {
                Assert.That(syncingResult.HighestBlock, Is.EqualTo(bestHeader));
                Assert.That(syncingResult.CurrentBlock, Is.EqualTo(currentHead));
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

            Assert.That(ethSyncingInfo.IsSyncing(), Is.EqualTo(false));
            Assert.That(ethSyncingInfo.UpdateAndGetSyncTime().TotalMicroseconds, Is.EqualTo(0));

            blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(100UL).TestObject);
            blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(80UL).TestObject)
                .TestObject);

            // First call starts the timer; at the metric's whole-second resolution this is still 0
            Assert.That(ethSyncingInfo.IsSyncing(), Is.EqualTo(true));
            Assert.That((long)ethSyncingInfo.UpdateAndGetSyncTime().TotalSeconds, Is.EqualTo(0));

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
            Assert.That(ethSyncingInfo.IsSyncing(), Is.EqualTo(true));
            Assert.That(ethSyncingInfo.UpdateAndGetSyncTime(), Is.GreaterThanOrEqualTo(afterSync));
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

        private SyncingResult CreateSyncingResult(bool isSyncing, ulong currentBlock, ulong highestBlock, SyncMode syncMode)
        {
            if (!isSyncing) return SyncingResult.NotSyncing;
            return new SyncingResult { CurrentBlock = currentBlock, HighestBlock = highestBlock, IsSyncing = true, StartingBlock = 0, SyncMode = syncMode };
        }
    }
}
