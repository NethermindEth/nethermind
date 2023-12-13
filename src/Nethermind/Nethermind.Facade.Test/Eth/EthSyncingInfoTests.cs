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
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            ISyncConfig syncConfig = new SyncConfig();
            blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(6178001L).TestObject);
            blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(6178000L).TestObject).TestObject);
            EthSyncingInfo ethSyncingInfo = new(blockTree, receiptStorage, syncConfig, new StaticSelector(SyncMode.All), LimboLogs.Instance);
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
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            ISyncConfig syncConfig = new SyncConfig();
            blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(6178010L).TestObject);
            blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(6178000L).TestObject).TestObject);
            EthSyncingInfo ethSyncingInfo = new(blockTree, receiptStorage, syncConfig, new StaticSelector(SyncMode.All), LimboLogs.Instance);
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
            blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(bestHeader).TestObject);
            blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(currentHead).TestObject).TestObject);
            EthSyncingInfo ethSyncingInfo = new(blockTree, receiptStorage, new SyncConfig(), new StaticSelector(SyncMode.All), LimboLogs.Instance);
            SyncingResult syncingResult = ethSyncingInfo.GetFullInfo();
            Assert.That(syncingResult.IsSyncing, Is.EqualTo(expectedResult));
        }

        [TestCase(800, 1000, true, false, 1100, false)]
        [TestCase(801, 1000, true, false, 1100, true)]
        [TestCase(799, 1000, true, false, 1100, false)]
        [TestCase(800, 1000, true, false, 1050, true)]
        [TestCase(801, 1000, true, false, 1050, true)]
        [TestCase(799, 1000, true, false, 1050, true)]

        [TestCase(1000, 900, false, true, 1100, false)]
        [TestCase(1000, 901, false, true, 1100, true)]
        [TestCase(1000, 899, false, true, 1100, false)]
        [TestCase(1000, 900, false, true, 1050, true)]
        [TestCase(1000, 901, false, true, 1050, true)]
        [TestCase(1000, 899, false, true, 1050, true)]

        [TestCase(800, 900, true, true, 1100, false)]
        [TestCase(800, 901, true, true, 1100, true)]
        [TestCase(800, 899, true, true, 1100, false)]
        [TestCase(801, 900, true, true, 1100, true)]
        [TestCase(801, 901, true, true, 1100, true)]
        [TestCase(801, 899, true, true, 1100, true)]
        [TestCase(799, 900, true, true, 1100, false)]
        [TestCase(799, 901, true, true, 1100, true)]
        [TestCase(799, 899, true, true, 1100, false)]

        [TestCase(null, 899, true, true, 1100, true)]
        [TestCase(799, null, true, true, 1100, true)]
        [TestCase(null, null, true, true, 1100, true)]
        public void IsSyncing_AncientBarriers(long? bodiesTail, long? receiptsTail, bool downloadBodies,
            bool downloadReceipts, long currentHead, bool expectedResult)
        {
            const long highestBlock = 1100;
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            ISyncConfig syncConfig = new SyncConfig
            {
                FastSync = true,
                AncientBodiesBarrier = 800,
                // AncientBodiesBarrierCalc = Max(1, Min(Pivot, BodiesBarrier)) = BodiesBarrier = 800
                AncientReceiptsBarrier = 900,
                // AncientReceiptsBarrierCalc = Max(1, Min(Pivot, Max(BodiesBarrier, ReceiptsBarrier))) = ReceiptsBarrier = 900
                DownloadBodiesInFastSync = downloadBodies,
                DownloadReceiptsInFastSync = downloadReceipts,
                PivotNumber = "1000"
            };

            blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(highestBlock).TestObject);
            blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(currentHead).TestObject)
                .TestObject);
            blockTree.LowestInsertedBodyNumber.Returns(bodiesTail);

            receiptStorage.LowestInsertedReceiptBlockNumber.Returns(receiptsTail);

            EthSyncingInfo ethSyncingInfo = new(blockTree, receiptStorage, syncConfig, new StaticSelector(SyncMode.FastBlocks), LimboLogs.Instance);
            SyncingResult syncingResult = ethSyncingInfo.GetFullInfo();
            Assert.That(syncingResult, Is.EqualTo(CreateSyncingResult(expectedResult, currentHead, highestBlock, SyncMode.FastBlocks)));
        }

        [Test]
        public void Should_calculate_sync_time()
        {
            SyncConfig syncConfig = new();
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();

            blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(100).TestObject);
            blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(100).TestObject)
                .TestObject);

            EthSyncingInfo ethSyncingInfo = new(blockTree, receiptStorage, syncConfig, new StaticSelector(SyncMode.All), LimboLogs.Instance);

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
            blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(bestHeader).TestObject);
            blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(currentHead).TestObject).TestObject);
            SyncConfig syncConfig = new()
            {
                FastSync = true,
                SnapSync = true,
                FastBlocks = true,
                PivotNumber = "0", // Equivalent to not having a pivot
            };
            EthSyncingInfo ethSyncingInfo = new(blockTree, receiptStorage, syncConfig, new StaticSelector(SyncMode.All), LimboLogs.Instance);
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
