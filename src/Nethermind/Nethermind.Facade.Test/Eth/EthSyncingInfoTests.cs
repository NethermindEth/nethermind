// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
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
                new StaticSelector(SyncMode.All), syncProgressResolver, LimboLogs.Instance);
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
                new StaticSelector(SyncMode.All), syncProgressResolver, LimboLogs.Instance);
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
                new StaticSelector(SyncMode.All), syncProgressResolver, LimboLogs.Instance);
            SyncingResult syncingResult = ethSyncingInfo.GetFullInfo();
            Assert.That(syncingResult.IsSyncing, Is.EqualTo(expectedResult));
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
                new StaticSelector(SyncMode.FastBlocks), syncProgressResolver, LimboLogs.Instance);
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
                new StaticSelector(SyncMode.All), syncProgressResolver, LimboLogs.Instance);

            Assert.That(ethSyncingInfo.IsSyncing(), Is.EqualTo(false));
            Assert.That(ethSyncingInfo.UpdateAndGetSyncTime().TotalMicroseconds, Is.EqualTo(0));

            blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(100UL).TestObject);
            blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(80UL).TestObject)
                .TestObject);

            // First call starting timer
            Assert.That(ethSyncingInfo.IsSyncing(), Is.EqualTo(true));
            Assert.That(ethSyncingInfo.UpdateAndGetSyncTime().TotalMicroseconds, Is.EqualTo(0));

            Thread.Sleep(100);

            // Second call timer should count some time
            Assert.That(ethSyncingInfo.IsSyncing(), Is.EqualTo(true));
            Assert.That(ethSyncingInfo.UpdateAndGetSyncTime().TotalMicroseconds, Is.Not.EqualTo(0));

            // Sync ended time should be zero
            blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(100UL).TestObject);
            blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(100UL).TestObject)
                .TestObject);

            Assert.That(ethSyncingInfo.IsSyncing(), Is.EqualTo(false));
            Assert.That(ethSyncingInfo.UpdateAndGetSyncTime().TotalMicroseconds, Is.EqualTo(0));
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
                new StaticSelector(SyncMode.All), syncProgressResolver, LimboLogs.Instance);
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
