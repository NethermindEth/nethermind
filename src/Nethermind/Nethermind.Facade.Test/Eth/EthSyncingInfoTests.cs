// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Eth;
using Nethermind.Logging;
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
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            ISyncProgressResolver syncProgressResolver = Substitute.For<ISyncProgressResolver>();
            syncProgressResolver.IsFastBlocksBodiesFinished().Returns(false);
            syncProgressResolver.IsFastBlocksReceiptsFinished().Returns(false);
            blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(6178001L).TestObject);
            blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(6178000L).TestObject).TestObject);
            EthSyncingInfo ethSyncingInfo = new(blockTree, receiptStorage, syncConfig,
                new StaticSelector(SyncMode.All), syncProgressResolver, LimboLogs.Instance);
            SyncingResult syncingResult = ethSyncingInfo.GetFullInfo();
            Assert.That(syncingResult.IsSyncing, Is.EqualTo(false));
            Assert.That(syncingResult.CurrentBlock, Is.EqualTo(0));
            Assert.That(syncingResult.HighestBlock, Is.EqualTo(0));
            Assert.That(syncingResult.StartingBlock, Is.EqualTo(0));
            Assert.That(syncingResult.SyncMode, Is.EqualTo(SyncMode.None));
        }

        [Test]
        public void GetFullInfo_WhenSyncing()
        {
            ISyncConfig syncConfig = new SyncConfig();
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            ISyncProgressResolver syncProgressResolver = Substitute.For<ISyncProgressResolver>();
            syncProgressResolver.IsFastBlocksBodiesFinished().Returns(false);
            syncProgressResolver.IsFastBlocksReceiptsFinished().Returns(false);
            blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(6178010L).TestObject);
            blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(6178000L).TestObject).TestObject);
            EthSyncingInfo ethSyncingInfo = new(blockTree, receiptStorage, syncConfig,
                new StaticSelector(SyncMode.All), syncProgressResolver, LimboLogs.Instance);
            SyncingResult syncingResult = ethSyncingInfo.GetFullInfo();
            Assert.That(syncingResult.IsSyncing, Is.EqualTo(true));
            Assert.That(syncingResult.CurrentBlock, Is.EqualTo(6178000L));
            Assert.That(syncingResult.HighestBlock, Is.EqualTo(6178010));
            Assert.That(syncingResult.StartingBlock, Is.EqualTo(0));
            Assert.That(syncingResult.SyncMode, Is.EqualTo(SyncMode.All));
        }

        [TestCase(6178001L, 6178000L, false)]
        [TestCase(6178010L, 6178000L, true)]
        public void IsSyncing_ReturnsExpectedResult(long bestHeader, long currentHead, bool expectedResult)
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            ISyncProgressResolver syncProgressResolver = Substitute.For<ISyncProgressResolver>();
            syncProgressResolver.IsFastBlocksBodiesFinished().Returns(false);
            syncProgressResolver.IsFastBlocksReceiptsFinished().Returns(false);
            blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(bestHeader).TestObject);
            blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(currentHead).TestObject).TestObject);
            EthSyncingInfo ethSyncingInfo = new(blockTree, receiptStorage, new SyncConfig(),
                new StaticSelector(SyncMode.All), syncProgressResolver, LimboLogs.Instance);
            SyncingResult syncingResult = ethSyncingInfo.GetFullInfo();
            Assert.That(syncingResult.IsSyncing, Is.EqualTo(expectedResult));
        }

        [TestCase(false, true, true)]
        [TestCase(true, false, true)]
        [TestCase(false, false, true)]
        [TestCase(true, true, false)]
        public void IsSyncing_AncientBarriers(bool resolverDownloadingBodies,
            bool resolverDownloadingreceipts, bool expectedResult)
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
                PivotNumber = "1000"
            };
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            ISyncProgressResolver syncProgressResolver = Substitute.For<ISyncProgressResolver>();
            syncProgressResolver.IsFastBlocksBodiesFinished().Returns(resolverDownloadingBodies);
            syncProgressResolver.IsFastBlocksReceiptsFinished().Returns(resolverDownloadingreceipts);

            blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(6178001L).TestObject);
            blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(6178000L).TestObject).TestObject);

            EthSyncingInfo ethSyncingInfo = new(blockTree, receiptStorage, syncConfig,
                new StaticSelector(SyncMode.FastBlocks), syncProgressResolver, LimboLogs.Instance);
            SyncingResult syncingResult = ethSyncingInfo.GetFullInfo();
            Assert.That(syncingResult, Is.EqualTo(CreateSyncingResult(expectedResult, 6178000L, 6178001L, SyncMode.FastBlocks)));
        }

        [Test]
        public void Should_calculate_sync_time()
        {
            SyncConfig syncConfig = new();
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            ISyncProgressResolver syncProgressResolver = Substitute.For<ISyncProgressResolver>();
            syncProgressResolver.IsFastBlocksBodiesFinished().Returns(false);
            syncProgressResolver.IsFastBlocksReceiptsFinished().Returns(false);

            blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(100).TestObject);
            blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(100).TestObject)
                .TestObject);

            EthSyncingInfo ethSyncingInfo = new(blockTree, receiptStorage, syncConfig,
                new StaticSelector(SyncMode.All), syncProgressResolver, LimboLogs.Instance);

            ethSyncingInfo.IsSyncing().Should().Be(false);
            ethSyncingInfo.UpdateAndGetSyncTime().TotalMicroseconds.Should().Be(0);

            blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(100).TestObject);
            blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(80).TestObject)
                .TestObject);

            // First call starting timer
            ethSyncingInfo.IsSyncing().Should().Be(true);
            ethSyncingInfo.UpdateAndGetSyncTime().TotalMicroseconds.Should().Be(0);

            Thread.Sleep(100);

            // Second call timer should count some time
            ethSyncingInfo.IsSyncing().Should().Be(true);
            ethSyncingInfo.UpdateAndGetSyncTime().TotalMicroseconds.Should().NotBe(0);

            // Sync ended time should be zero
            blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(100).TestObject);
            blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(100).TestObject)
                .TestObject);

            ethSyncingInfo.IsSyncing().Should().Be(false);
            ethSyncingInfo.UpdateAndGetSyncTime().TotalMicroseconds.Should().Be(0);
        }

        [TestCase(6178001L, 6178000L)]
        [TestCase(8001L, 8000L)]
        public void IsSyncing_ReturnsFalseOnFastSyncWithoutPivot(long bestHeader, long currentHead)
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            ISyncProgressResolver syncProgressResolver = Substitute.For<ISyncProgressResolver>();
            syncProgressResolver.IsFastBlocksBodiesFinished().Returns(false);
            syncProgressResolver.IsFastBlocksReceiptsFinished().Returns(false);
            blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(bestHeader).TestObject);
            blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(currentHead).TestObject).TestObject);
            SyncConfig syncConfig = new()
            {
                FastSync = true,
                SnapSync = true,
                FastBlocks = true,
                PivotNumber = "0", // Equivalent to not having a pivot
            };
            EthSyncingInfo ethSyncingInfo = new(blockTree, receiptStorage, syncConfig,
                new StaticSelector(SyncMode.All), syncProgressResolver, LimboLogs.Instance);
            SyncingResult syncingResult = ethSyncingInfo.GetFullInfo();

            Assert.That(syncingResult.IsSyncing, Is.False);
        }

        private SyncingResult CreateSyncingResult(bool isSyncing, long currentBlock, long highestBlock, SyncMode syncMode)
        {
            if (!isSyncing) return SyncingResult.NotSyncing;
            return new SyncingResult { CurrentBlock = currentBlock, HighestBlock = highestBlock, IsSyncing = true, StartingBlock = 0, SyncMode = syncMode };
        }
    }
}
