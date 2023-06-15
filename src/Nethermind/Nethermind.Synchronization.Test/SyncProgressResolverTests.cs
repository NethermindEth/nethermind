// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class SyncProgressResolverTests
    {
        [Test]
        public void Header_block_is_0_when_no_header_was_suggested()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            IDb stateDb = new MemDb();
            SyncConfig syncConfig = new();
            syncConfig.PivotNumber = "1";
            ProgressTracker progressTracker = new(blockTree, stateDb, LimboLogs.Instance);

            SyncProgressResolver syncProgressResolver = new(blockTree, receiptStorage, stateDb, NullTrieNodeResolver.Instance, progressTracker, syncConfig, LimboLogs.Instance);
            blockTree.BestSuggestedHeader.Returns((BlockHeader)null);
            Assert.That(syncProgressResolver.FindBestHeader(), Is.EqualTo(0));
        }

        [Test]
        public void Best_block_is_0_when_no_block_was_suggested()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            IDb stateDb = new MemDb();
            SyncConfig syncConfig = new();
            syncConfig.PivotNumber = "1";
            ProgressTracker progressTracker = new(blockTree, stateDb, LimboLogs.Instance);

            SyncProgressResolver syncProgressResolver = new(blockTree, receiptStorage, stateDb, NullTrieNodeResolver.Instance, progressTracker, syncConfig, LimboLogs.Instance);
            blockTree.BestSuggestedBody.Returns((Block)null);
            Assert.That(syncProgressResolver.FindBestFullBlock(), Is.EqualTo(0));
        }

        [Test]
        public void Best_state_is_head_when_there_are_no_suggested_blocks()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            IDb stateDb = new MemDb();
            SyncConfig syncConfig = new();
            syncConfig.PivotNumber = "1";
            ProgressTracker progressTracker = new(blockTree, stateDb, LimboLogs.Instance);

            SyncProgressResolver syncProgressResolver = new(blockTree, receiptStorage, stateDb, NullTrieNodeResolver.Instance, progressTracker, syncConfig, LimboLogs.Instance);
            var head = Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(5).WithStateRoot(TestItem.KeccakA).TestObject).TestObject;
            blockTree.Head.Returns(head);
            blockTree.BestSuggestedHeader.Returns(head.Header);
            stateDb[head.StateRoot.Bytes] = new byte[] { 1 };
            Assert.That(syncProgressResolver.FindBestFullState(), Is.EqualTo(head.Number));
        }

        [Test]
        public void Best_state_is_suggested_if_there_is_suggested_block_with_state()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            IDb stateDb = new MemDb();
            SyncConfig syncConfig = new();
            syncConfig.PivotNumber = "1";
            ProgressTracker progressTracker = new(blockTree, stateDb, LimboLogs.Instance);

            SyncProgressResolver syncProgressResolver = new(blockTree, receiptStorage, stateDb, NullTrieNodeResolver.Instance, progressTracker, syncConfig, LimboLogs.Instance);
            var head = Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(5).WithStateRoot(TestItem.KeccakA).TestObject).TestObject;
            var suggested = Build.A.BlockHeader.WithNumber(6).WithStateRoot(TestItem.KeccakB).TestObject;
            blockTree.Head.Returns(head);
            blockTree.BestSuggestedHeader.Returns(suggested);
            blockTree.FindHeader(Arg.Any<Keccak>(), BlockTreeLookupOptions.TotalDifficultyNotNeeded).Returns(head.Header);

            stateDb[head.StateRoot.Bytes] = new byte[] { 1 };
            stateDb[suggested.StateRoot.Bytes] = new byte[] { 1 };
            Assert.That(syncProgressResolver.FindBestFullState(), Is.EqualTo(suggested.Number));
        }

        [Test]
        public void Best_state_is_head_if_there_is_suggested_block_without_state()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            IDb stateDb = new MemDb();
            SyncConfig syncConfig = new();
            syncConfig.PivotNumber = "1";
            ProgressTracker progressTracker = new(blockTree, stateDb, LimboLogs.Instance);

            SyncProgressResolver syncProgressResolver = new(blockTree, receiptStorage, stateDb, NullTrieNodeResolver.Instance, progressTracker, syncConfig, LimboLogs.Instance);
            var head = Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(5).WithStateRoot(TestItem.KeccakA).TestObject).TestObject;
            var suggested = Build.A.BlockHeader.WithNumber(6).WithStateRoot(TestItem.KeccakB).TestObject;
            blockTree.Head.Returns(head);
            blockTree.BestSuggestedHeader.Returns(suggested);
            blockTree.FindHeader(Arg.Any<Keccak>(), BlockTreeLookupOptions.TotalDifficultyNotNeeded).Returns(head?.Header);
            stateDb[head.StateRoot.Bytes] = new byte[] { 1 };
            stateDb[suggested.StateRoot.Bytes] = null;
            Assert.That(syncProgressResolver.FindBestFullState(), Is.EqualTo(head.Number));
        }

        [Test]
        public void Is_fast_block_finished_returns_true_when_no_fast_block_sync_is_used()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            IDb stateDb = new MemDb();
            SyncConfig syncConfig = new();
            syncConfig.FastBlocks = false;
            syncConfig.PivotNumber = "1";
            ProgressTracker progressTracker = new(blockTree, stateDb, LimboLogs.Instance);

            SyncProgressResolver syncProgressResolver = new(blockTree, receiptStorage, stateDb, NullTrieNodeResolver.Instance, progressTracker, syncConfig, LimboLogs.Instance);
            Assert.True(syncProgressResolver.IsFastBlocksHeadersFinished());
            Assert.True(syncProgressResolver.IsFastBlocksBodiesFinished());
            Assert.True(syncProgressResolver.IsFastBlocksReceiptsFinished());
        }

        [Test]
        public void Is_fast_block_headers_finished_returns_false_when_headers_not_downloaded()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            IDb stateDb = new MemDb();
            SyncConfig syncConfig = new();
            syncConfig.FastBlocks = true;
            syncConfig.FastSync = true;
            syncConfig.DownloadBodiesInFastSync = true;
            syncConfig.DownloadReceiptsInFastSync = true;
            syncConfig.PivotNumber = "1";
            ProgressTracker progressTracker = new(blockTree, stateDb, LimboLogs.Instance);

            blockTree.LowestInsertedHeader.Returns(Build.A.BlockHeader.WithNumber(2).WithStateRoot(TestItem.KeccakA).TestObject);

            SyncProgressResolver syncProgressResolver = new(blockTree, receiptStorage, stateDb, NullTrieNodeResolver.Instance, progressTracker, syncConfig, LimboLogs.Instance);
            Assert.False(syncProgressResolver.IsFastBlocksHeadersFinished());
        }

        [Test]
        public void Is_fast_block_bodies_finished_returns_false_when_blocks_not_downloaded()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            IDb stateDb = new MemDb();
            SyncConfig syncConfig = new();
            syncConfig.FastBlocks = true;
            syncConfig.FastSync = true;
            syncConfig.DownloadBodiesInFastSync = true;
            syncConfig.DownloadReceiptsInFastSync = true;
            syncConfig.PivotNumber = "1";
            ProgressTracker progressTracker = new(blockTree, stateDb, LimboLogs.Instance);

            blockTree.LowestInsertedHeader.Returns(Build.A.BlockHeader.WithNumber(1).WithStateRoot(TestItem.KeccakA).TestObject);
            blockTree.LowestInsertedBodyNumber.Returns(2);

            SyncProgressResolver syncProgressResolver = new(blockTree, receiptStorage, stateDb, NullTrieNodeResolver.Instance, progressTracker, syncConfig, LimboLogs.Instance);
            Assert.False(syncProgressResolver.IsFastBlocksBodiesFinished());
        }

        [Test]
        public void Is_fast_block_receipts_finished_returns_false_when_receipts_not_downloaded()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            IDb stateDb = new MemDb();
            SyncConfig syncConfig = new();
            syncConfig.FastBlocks = true;
            syncConfig.FastSync = true;
            syncConfig.DownloadBodiesInFastSync = true;
            syncConfig.DownloadReceiptsInFastSync = true;
            syncConfig.PivotNumber = "1";
            ProgressTracker progressTracker = new(blockTree, stateDb, LimboLogs.Instance);

            blockTree.LowestInsertedHeader.Returns(Build.A.BlockHeader.WithNumber(1).WithStateRoot(TestItem.KeccakA).TestObject);
            blockTree.LowestInsertedBodyNumber.Returns(1);
            receiptStorage.LowestInsertedReceiptBlockNumber.Returns(2);

            SyncProgressResolver syncProgressResolver = new(
                blockTree, receiptStorage, stateDb, NullTrieNodeResolver.Instance, progressTracker, syncConfig, LimboLogs.Instance);
            Assert.False(syncProgressResolver.IsFastBlocksReceiptsFinished());
        }

        [Test]
        public void Is_fast_block_bodies_finished_returns_true_when_bodies_not_downloaded_and_we_do_not_want_to_download_bodies()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            IDb stateDb = new MemDb();
            SyncConfig syncConfig = new();
            syncConfig.FastBlocks = true;
            syncConfig.DownloadBodiesInFastSync = false;
            syncConfig.DownloadReceiptsInFastSync = true;
            syncConfig.PivotNumber = "1";
            ProgressTracker progressTracker = new(blockTree, stateDb, LimboLogs.Instance);

            blockTree.LowestInsertedHeader.Returns(Build.A.BlockHeader.WithNumber(1).WithStateRoot(TestItem.KeccakA).TestObject);
            blockTree.LowestInsertedBodyNumber.Returns(2);
            receiptStorage.LowestInsertedReceiptBlockNumber.Returns(1);

            SyncProgressResolver syncProgressResolver = new(blockTree, receiptStorage, stateDb, NullTrieNodeResolver.Instance, progressTracker, syncConfig, LimboLogs.Instance);
            Assert.True(syncProgressResolver.IsFastBlocksBodiesFinished());
        }

        [Test]
        public void Is_fast_block_receipts_finished_returns_true_when_receipts_not_downloaded_and_we_do_not_want_to_download_receipts()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            IDb stateDb = new MemDb();
            SyncConfig syncConfig = new();
            syncConfig.FastBlocks = true;
            syncConfig.DownloadBodiesInFastSync = true;
            syncConfig.DownloadReceiptsInFastSync = false;
            syncConfig.PivotNumber = "1";
            ProgressTracker progressTracker = new(blockTree, stateDb, LimboLogs.Instance);

            blockTree.LowestInsertedHeader.Returns(Build.A.BlockHeader.WithNumber(1).WithStateRoot(TestItem.KeccakA).TestObject);
            blockTree.LowestInsertedBodyNumber.Returns(1);
            receiptStorage.LowestInsertedReceiptBlockNumber.Returns(2);

            SyncProgressResolver syncProgressResolver = new(
                blockTree, receiptStorage, stateDb, NullTrieNodeResolver.Instance, progressTracker, syncConfig, LimboLogs.Instance);
            Assert.True(syncProgressResolver.IsFastBlocksReceiptsFinished());
        }
    }
}
