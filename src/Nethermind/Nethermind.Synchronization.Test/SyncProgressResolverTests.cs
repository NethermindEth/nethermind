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

using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
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
            SyncConfig syncConfig = new SyncConfig();
            syncConfig.PivotNumber = "1";

            SyncProgressResolver syncProgressResolver = new SyncProgressResolver(blockTree, receiptStorage, stateDb, new MemDb(), NullTrieNodeResolver.Instance, syncConfig, LimboLogs.Instance);
            blockTree.BestSuggestedHeader.Returns((BlockHeader) null);
            Assert.AreEqual(0, syncProgressResolver.FindBestHeader());
        }

        [Test]
        public void Best_block_is_0_when_no_block_was_suggested()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            IDb stateDb = new MemDb();
            SyncConfig syncConfig = new SyncConfig();
            syncConfig.PivotNumber = "1";

            SyncProgressResolver syncProgressResolver = new SyncProgressResolver(blockTree, receiptStorage, stateDb, new MemDb(), NullTrieNodeResolver.Instance, syncConfig, LimboLogs.Instance);
            blockTree.BestSuggestedBody.Returns((Block) null);
            Assert.AreEqual(0, syncProgressResolver.FindBestFullBlock());
        }

        [Test]
        public void Best_state_is_head_when_there_are_no_suggested_blocks()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            IDb stateDb = Substitute.For<IDb>();
            SyncConfig syncConfig = new SyncConfig();
            syncConfig.PivotNumber = "1";

            SyncProgressResolver syncProgressResolver = new SyncProgressResolver(blockTree, receiptStorage, stateDb, new MemDb(), NullTrieNodeResolver.Instance, syncConfig, LimboLogs.Instance);
            var head = Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(5).WithStateRoot(TestItem.KeccakA).TestObject).TestObject;
            blockTree.Head.Returns(head);
            blockTree.BestSuggestedHeader.Returns(head.Header);
            stateDb.Get(head.StateRoot).Returns(new byte[] {1});
            stateDb.Innermost.Returns(stateDb);
            Assert.AreEqual(head.Number, syncProgressResolver.FindBestFullState());
        }

        [Test]
        public void Best_state_is_suggested_if_there_is_suggested_block_with_state()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            IDb stateDb = Substitute.For<IDb>();
            SyncConfig syncConfig = new SyncConfig();
            syncConfig.PivotNumber = "1";

            SyncProgressResolver syncProgressResolver = new SyncProgressResolver(blockTree, receiptStorage, stateDb, new MemDb(), NullTrieNodeResolver.Instance, syncConfig, LimboLogs.Instance);
            var head = Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(5).WithStateRoot(TestItem.KeccakA).TestObject).TestObject;
            var suggested = Build.A.BlockHeader.WithNumber(6).WithStateRoot(TestItem.KeccakB).TestObject;
            blockTree.Head.Returns(head);
            blockTree.BestSuggestedHeader.Returns(suggested);
            blockTree.FindHeader(Arg.Any<Keccak>(), BlockTreeLookupOptions.TotalDifficultyNotNeeded).Returns(head.Header);
            stateDb.Get(head.StateRoot).Returns(new byte[] {1});
            stateDb.Get(suggested.StateRoot).Returns(new byte[] {1});
            Assert.AreEqual(suggested.Number, syncProgressResolver.FindBestFullState());
        }

        [Test]
        public void Best_state_is_head_if_there_is_suggested_block_without_state()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            IDb stateDb = Substitute.For<IDb>();
            SyncConfig syncConfig = new SyncConfig();
            syncConfig.PivotNumber = "1";

            SyncProgressResolver syncProgressResolver = new SyncProgressResolver(blockTree, receiptStorage, stateDb, new MemDb(), NullTrieNodeResolver.Instance, syncConfig, LimboLogs.Instance);
            var head =  Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(5).WithStateRoot(TestItem.KeccakA).TestObject).TestObject;
            var suggested = Build.A.BlockHeader.WithNumber(6).WithStateRoot(TestItem.KeccakB).TestObject;
            blockTree.Head.Returns(head);
            blockTree.BestSuggestedHeader.Returns(suggested);
            blockTree.FindHeader(Arg.Any<Keccak>(), BlockTreeLookupOptions.TotalDifficultyNotNeeded).Returns(head?.Header);
            stateDb.Get(head.StateRoot).Returns(new byte[] {1});
            stateDb.Innermost.Returns(stateDb);
            stateDb.Get(suggested.StateRoot).Returns((byte[]) null);
            Assert.AreEqual(head.Number, syncProgressResolver.FindBestFullState());
        }

        [Test]
        public void Is_fast_block_finished_returns_true_when_no_fast_block_sync_is_used()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            IDb stateDb = new MemDb();
            SyncConfig syncConfig = new SyncConfig();
            syncConfig.FastBlocks = false;
            syncConfig.PivotNumber = "1";

            SyncProgressResolver syncProgressResolver = new SyncProgressResolver(blockTree, receiptStorage, stateDb, new MemDb(), NullTrieNodeResolver.Instance, syncConfig, LimboLogs.Instance);
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
            SyncConfig syncConfig = new SyncConfig();
            syncConfig.FastBlocks = true;
            syncConfig.DownloadBodiesInFastSync = true;
            syncConfig.DownloadReceiptsInFastSync = true;
            syncConfig.PivotNumber = "1";

            blockTree.LowestInsertedHeader.Returns(Build.A.BlockHeader.WithNumber(2).WithStateRoot(TestItem.KeccakA).TestObject);

            SyncProgressResolver syncProgressResolver = new SyncProgressResolver(blockTree, receiptStorage, stateDb, new MemDb(), NullTrieNodeResolver.Instance, syncConfig, LimboLogs.Instance);
            Assert.False(syncProgressResolver.IsFastBlocksHeadersFinished());
        }

        [Test]
        public void Is_fast_block_bodies_finished_returns_false_when_blocks_not_downloaded()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            IDb stateDb = new MemDb();
            SyncConfig syncConfig = new SyncConfig();
            syncConfig.FastBlocks = true;
            syncConfig.DownloadBodiesInFastSync = true;
            syncConfig.DownloadReceiptsInFastSync = true;
            syncConfig.PivotNumber = "1";

            blockTree.LowestInsertedHeader.Returns(Build.A.BlockHeader.WithNumber(1).WithStateRoot(TestItem.KeccakA).TestObject);
            blockTree.LowestInsertedBodyNumber.Returns(2);

            SyncProgressResolver syncProgressResolver = new SyncProgressResolver(blockTree, receiptStorage, stateDb, new MemDb(), NullTrieNodeResolver.Instance, syncConfig, LimboLogs.Instance);
            Assert.False(syncProgressResolver.IsFastBlocksBodiesFinished());
        }

        [Test]
        public void Is_fast_block_receipts_finished_returns_false_when_receipts_not_downloaded()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            IDb stateDb = new MemDb();
            SyncConfig syncConfig = new SyncConfig();
            syncConfig.FastBlocks = true;
            syncConfig.DownloadBodiesInFastSync = true;
            syncConfig.DownloadReceiptsInFastSync = true;
            syncConfig.PivotNumber = "1";

            blockTree.LowestInsertedHeader.Returns(Build.A.BlockHeader.WithNumber(1).WithStateRoot(TestItem.KeccakA).TestObject);
            blockTree.LowestInsertedBodyNumber.Returns(1);
            receiptStorage.LowestInsertedReceiptBlockNumber.Returns(2);

            SyncProgressResolver syncProgressResolver = new SyncProgressResolver(
                blockTree, receiptStorage, stateDb, new MemDb(), NullTrieNodeResolver.Instance, syncConfig, LimboLogs.Instance);
            Assert.False(syncProgressResolver.IsFastBlocksReceiptsFinished());
        }

        [Test]
        public void Is_fast_block_bodies_finished_returns_true_when_bodies_not_downloaded_and_we_do_not_want_to_download_bodies()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            IDb stateDb = new MemDb();
            SyncConfig syncConfig = new SyncConfig();
            syncConfig.FastBlocks = true;
            syncConfig.DownloadBodiesInFastSync = false;
            syncConfig.DownloadReceiptsInFastSync = true;
            syncConfig.PivotNumber = "1";

            blockTree.LowestInsertedHeader.Returns(Build.A.BlockHeader.WithNumber(1).WithStateRoot(TestItem.KeccakA).TestObject);
            blockTree.LowestInsertedBodyNumber.Returns(2);
            receiptStorage.LowestInsertedReceiptBlockNumber.Returns(1);

            SyncProgressResolver syncProgressResolver = new SyncProgressResolver(blockTree, receiptStorage, stateDb, new MemDb(), NullTrieNodeResolver.Instance, syncConfig, LimboLogs.Instance);
            Assert.True(syncProgressResolver.IsFastBlocksBodiesFinished());
        }

        [Test]
        public void Is_fast_block_receipts_finished_returns_true_when_receipts_not_downloaded_and_we_do_not_want_to_download_receipts()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            IDb stateDb = new MemDb();
            SyncConfig syncConfig = new SyncConfig();
            syncConfig.FastBlocks = true;
            syncConfig.DownloadBodiesInFastSync = true;
            syncConfig.DownloadReceiptsInFastSync = false;
            syncConfig.PivotNumber = "1";

            blockTree.LowestInsertedHeader.Returns(Build.A.BlockHeader.WithNumber(1).WithStateRoot(TestItem.KeccakA).TestObject);
            blockTree.LowestInsertedBodyNumber.Returns(1);
            receiptStorage.LowestInsertedReceiptBlockNumber.Returns(2);

            SyncProgressResolver syncProgressResolver = new SyncProgressResolver(
                blockTree, receiptStorage, stateDb, new MemDb(), NullTrieNodeResolver.Instance, syncConfig, LimboLogs.Instance);
            Assert.True(syncProgressResolver.IsFastBlocksReceiptsFinished());
        }
    }
}
