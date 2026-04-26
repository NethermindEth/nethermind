// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.State;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.SnapSync;
using NSubstitute;
using NSubstitute.ReturnsExtensions;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class SyncProgressResolverTests
    {
        private IBlockTree _blockTree = null!;
        private IStateReader _stateReader = null!;

        [SetUp]
        public void Setup()
        {
            _blockTree = Substitute.For<IBlockTree>();
            _stateReader = Substitute.For<IStateReader>();
        }

        [Test]
        public void Header_block_is_0_when_no_header_was_suggested()
        {
            SyncProgressResolver syncProgressResolver = CreateProgressResolver(false, new SyncConfig { PivotNumber = 1 });
            _blockTree.BestSuggestedHeader.ReturnsNull();
            Assert.That(syncProgressResolver.FindBestHeader(), Is.EqualTo(0));
        }

        [Test]
        public void Best_block_is_0_when_no_block_was_suggested()
        {
            SyncProgressResolver syncProgressResolver = CreateProgressResolver(false, new SyncConfig { PivotNumber = 1 });
            _blockTree.BestSuggestedBody.ReturnsNull();
            Assert.That(syncProgressResolver.FindBestFullBlock(), Is.EqualTo(0));
        }

        [Test]
        public void Best_state_is_head_when_there_are_no_suggested_blocks()
        {
            SyncProgressResolver syncProgressResolver = CreateProgressResolver(false, new SyncConfig { PivotNumber = 1 });
            Block head = Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(5).WithStateRoot(TestItem.KeccakA).TestObject).TestObject;
            _blockTree.Head.Returns(head);
            _blockTree.BestSuggestedHeader.Returns(head.Header);
            _stateReader.HasStateForBlock(head.Header).Returns(true);
            Assert.That(syncProgressResolver.FindBestFullState(), Is.EqualTo(head.Number));
        }

        [TestCase(true, 6)]
        [TestCase(false, 5)]
        public void Best_state_depends_on_whether_suggested_block_has_state(bool suggestedHasState, long expectedNumber)
        {
            SyncProgressResolver syncProgressResolver = CreateProgressResolver(false, new SyncConfig { PivotNumber = 1 });
            Block head = Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(5).WithStateRoot(TestItem.KeccakA).TestObject).TestObject;
            BlockHeader suggested = Build.A.BlockHeader.WithNumber(6).WithStateRoot(TestItem.KeccakB).TestObject;
            _blockTree.Head.Returns(head);
            _blockTree.BestSuggestedHeader.Returns(suggested);
            _blockTree.FindHeader(Arg.Any<Hash256>(), BlockTreeLookupOptions.TotalDifficultyNotNeeded).Returns(head.Header);
            _stateReader.HasStateForBlock(head.Header!).Returns(true);
            _stateReader.HasStateForBlock(suggested).Returns(suggestedHasState);
            Assert.That(syncProgressResolver.FindBestFullState(), Is.EqualTo(expectedNumber));
        }

        [Test]
        public void Is_fast_block_finished_returns_true_when_no_fast_sync_is_used()
        {
            SyncProgressResolver syncProgressResolver = CreateProgressResolver(false, new SyncConfig { FastSync = false, PivotNumber = 1 });
            Assert.That(syncProgressResolver.IsFastBlocksHeadersFinished(), Is.True);
            Assert.That(syncProgressResolver.IsFastBlocksBodiesFinished(), Is.True);
            Assert.That(syncProgressResolver.IsFastBlocksReceiptsFinished(), Is.True);
        }

        [Test]
        public void Is_fast_block_bodies_finished_returns_false_when_blocks_not_downloaded()
        {
            SyncConfig syncConfig = new()
            {
                FastSync = true,
                DownloadBodiesInFastSync = true,
                DownloadReceiptsInFastSync = true,
                PivotNumber = 1,
            };
            _blockTree.SyncPivot.Returns((1, Hash256.Zero));
            _blockTree.LowestInsertedHeader.Returns(Build.A.BlockHeader.WithNumber(1).WithStateRoot(TestItem.KeccakA).TestObject);

            SyncProgressResolver syncProgressResolver = CreateProgressResolver(false, syncConfig);
            Assert.That(syncProgressResolver.IsFastBlocksBodiesFinished(), Is.False);
        }

        [Test]
        public void Is_fast_block_receipts_finished_returns_true_when_receipts_not_downloaded_and_we_do_not_want_to_download_receipts()
        {
            SyncConfig syncConfig = new()
            {
                FastSync = true,
                DownloadBodiesInFastSync = true,
                DownloadReceiptsInFastSync = false,
                PivotNumber = 1,
            };
            _blockTree.LowestInsertedHeader.Returns(Build.A.BlockHeader.WithNumber(1).WithStateRoot(TestItem.KeccakA).TestObject);

            SyncProgressResolver syncProgressResolver = CreateProgressResolver(true, syncConfig);
            Assert.That(syncProgressResolver.IsFastBlocksReceiptsFinished(), Is.True);
        }


        private SyncProgressResolver CreateProgressResolver(bool isReceiptFinished, SyncConfig syncConfig)
        {
            ISyncFeed<ReceiptsSyncBatch?> receiptFeed = Substitute.For<ISyncFeed<ReceiptsSyncBatch?>>();
            receiptFeed.IsFinished.Returns(isReceiptFinished);

            return new SyncProgressResolver(
                _blockTree,
                new FullStateFinder(_blockTree, _stateReader),
                syncConfig,
                Substitute.For<ISyncFeed<HeadersSyncBatch?>>(),
                Substitute.For<ISyncFeed<BodiesSyncBatch?>>(),
                receiptFeed,
                Substitute.For<ISyncFeed<SnapSyncBatch?>>()
            );
        }
    }
}
