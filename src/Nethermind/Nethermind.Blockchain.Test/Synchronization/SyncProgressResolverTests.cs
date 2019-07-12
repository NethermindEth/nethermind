/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Synchronization.FastSync;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Synchronization
{
    [TestFixture]
    public class SyncProgressResolverTests
    {
        [Test]
        public void Header_block_is_0_when_no_header_was_suggested()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            INodeDataDownloader nodeDataDownloader = Substitute.For<INodeDataDownloader>();
            SyncConfig syncConfig = new SyncConfig();

            SyncProgressResolver syncProgressResolver = new SyncProgressResolver(blockTree, receiptStorage, nodeDataDownloader, syncConfig, LimboLogs.Instance);
            blockTree.BestSuggestedHeader.Returns((BlockHeader) null);
            Assert.AreEqual(0, syncProgressResolver.FindBestHeader());
        }
        
        [Test]
        public void Best_block_is_0_when_no_block_was_suggested()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            INodeDataDownloader nodeDataDownloader = Substitute.For<INodeDataDownloader>();
            SyncConfig syncConfig = new SyncConfig();

            SyncProgressResolver syncProgressResolver = new SyncProgressResolver(blockTree, receiptStorage, nodeDataDownloader, syncConfig, LimboLogs.Instance);
            blockTree.BestSuggestedBody.Returns((Block) null);
            Assert.AreEqual(0, syncProgressResolver.FindBestFullBlock());
        }
        
        [Test]
        public void Best_state_is_head_when_there_are_no_suggested_blocks()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            INodeDataDownloader nodeDataDownloader = Substitute.For<INodeDataDownloader>();
            SyncConfig syncConfig = new SyncConfig();

            SyncProgressResolver syncProgressResolver = new SyncProgressResolver(blockTree, receiptStorage, nodeDataDownloader, syncConfig, LimboLogs.Instance);
            var head = Build.A.BlockHeader.WithNumber(5).TestObject;
            blockTree.Head.Returns(head);
            nodeDataDownloader.IsFullySynced(head).Returns(true);
            Assert.AreEqual(head.Number, syncProgressResolver.FindBestFullState());
        }
        
        [Test]
        public void Best_state_is_suggested_if_there_is_suggested_block_with_state()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            INodeDataDownloader nodeDataDownloader = Substitute.For<INodeDataDownloader>();
            SyncConfig syncConfig = new SyncConfig();

            SyncProgressResolver syncProgressResolver = new SyncProgressResolver(blockTree, receiptStorage, nodeDataDownloader, syncConfig, LimboLogs.Instance);
            var head = Build.A.BlockHeader.WithNumber(5).TestObject;
            var suggested = Build.A.BlockHeader.WithNumber(6).TestObject;
            blockTree.Head.Returns(head);
            blockTree.BestSuggestedHeader.Returns(suggested);
            blockTree.FindHeader(Arg.Any<Keccak>(), BlockTreeLookupOptions.TotalDifficultyNotNeeded).Returns(head);
            nodeDataDownloader.IsFullySynced(head).Returns(true);
            nodeDataDownloader.IsFullySynced(suggested).Returns(true);
            Assert.AreEqual(suggested.Number, syncProgressResolver.FindBestFullState());
        }
        
        [Test]
        public void Best_state_is_head_if_there_is_suggested_block_without_state()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            INodeDataDownloader nodeDataDownloader = Substitute.For<INodeDataDownloader>();
            SyncConfig syncConfig = new SyncConfig();

            SyncProgressResolver syncProgressResolver = new SyncProgressResolver(blockTree, receiptStorage, nodeDataDownloader, syncConfig, LimboLogs.Instance);
            var head = Build.A.BlockHeader.WithNumber(5).TestObject;
            var suggested = Build.A.BlockHeader.WithNumber(6).TestObject;
            blockTree.Head.Returns(head);
            blockTree.BestSuggestedHeader.Returns(suggested);
            blockTree.FindHeader(Arg.Any<Keccak>(), BlockTreeLookupOptions.TotalDifficultyNotNeeded).Returns(head);
            nodeDataDownloader.IsFullySynced(head).Returns(true);
            nodeDataDownloader.IsFullySynced(suggested).Returns(false);
            Assert.AreEqual(head.Number, syncProgressResolver.FindBestFullState());
        }
        
        [Test]
        public void Is_fast_block_finished_returns_true_when_no_fast_block_sync_is_used()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            INodeDataDownloader nodeDataDownloader = Substitute.For<INodeDataDownloader>();
            SyncConfig syncConfig = new SyncConfig();
            syncConfig.FastBlocks = false;

            SyncProgressResolver syncProgressResolver = new SyncProgressResolver(blockTree, receiptStorage, nodeDataDownloader, syncConfig, LimboLogs.Instance);
            Assert.True(syncProgressResolver.IsFastBlocksFinished());
        }
        
        [Test]
        public void Is_fast_block_finished_returns_false_when_headers_not_downloaded()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            INodeDataDownloader nodeDataDownloader = Substitute.For<INodeDataDownloader>();
            SyncConfig syncConfig = new SyncConfig();
            syncConfig.FastBlocks = true;
            syncConfig.DownloadBodiesInFastSync = true;
            syncConfig.DownloadReceiptsInFastSync = true;

            blockTree.LowestInsertedHeader.Returns(Build.A.BlockHeader.WithNumber(2).TestObject);

            SyncProgressResolver syncProgressResolver = new SyncProgressResolver(blockTree, receiptStorage, nodeDataDownloader, syncConfig, LimboLogs.Instance);
            Assert.False(syncProgressResolver.IsFastBlocksFinished());
        }
        
        [Test]
        public void Is_fast_block_finished_returns_false_when_blocks_not_downloaded()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            INodeDataDownloader nodeDataDownloader = Substitute.For<INodeDataDownloader>();
            SyncConfig syncConfig = new SyncConfig();
            syncConfig.FastBlocks = true;
            syncConfig.DownloadBodiesInFastSync = true;
            syncConfig.DownloadReceiptsInFastSync = true;

            blockTree.LowestInsertedHeader.Returns(Build.A.BlockHeader.WithNumber(1).TestObject);
            blockTree.LowestInsertedBody.Returns(Build.A.Block.WithNumber(2).TestObject);

            SyncProgressResolver syncProgressResolver = new SyncProgressResolver(blockTree, receiptStorage, nodeDataDownloader, syncConfig, LimboLogs.Instance);
            Assert.False(syncProgressResolver.IsFastBlocksFinished());
        }
        
        [Test]
        public void Is_fast_block_finished_returns_false_when_receipts_not_downloaded()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            INodeDataDownloader nodeDataDownloader = Substitute.For<INodeDataDownloader>();
            SyncConfig syncConfig = new SyncConfig();
            syncConfig.FastBlocks = true;
            syncConfig.DownloadBodiesInFastSync = true;
            syncConfig.DownloadReceiptsInFastSync = true;

            blockTree.LowestInsertedHeader.Returns(Build.A.BlockHeader.WithNumber(1).TestObject);
            blockTree.LowestInsertedBody.Returns(Build.A.Block.WithNumber(1).TestObject);
            receiptStorage.LowestInsertedReceiptBlock.Returns(2);

            SyncProgressResolver syncProgressResolver = new SyncProgressResolver(blockTree, receiptStorage, nodeDataDownloader, syncConfig, LimboLogs.Instance);
            Assert.False(syncProgressResolver.IsFastBlocksFinished());
        }
        
        [Test]
        public void Is_fast_block_finished_returns_true_when_bodies_not_downloaded_and_we_do_not_want_to_download_bodies()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            INodeDataDownloader nodeDataDownloader = Substitute.For<INodeDataDownloader>();
            SyncConfig syncConfig = new SyncConfig();
            syncConfig.FastBlocks = true;
            syncConfig.DownloadBodiesInFastSync = false;
            syncConfig.DownloadReceiptsInFastSync = true;

            blockTree.LowestInsertedHeader.Returns(Build.A.BlockHeader.WithNumber(1).TestObject);
            blockTree.LowestInsertedBody.Returns(Build.A.Block.WithNumber(2).TestObject);
            receiptStorage.LowestInsertedReceiptBlock.Returns(1);

            SyncProgressResolver syncProgressResolver = new SyncProgressResolver(blockTree, receiptStorage, nodeDataDownloader, syncConfig, LimboLogs.Instance);
            Assert.True(syncProgressResolver.IsFastBlocksFinished());
        }
        
        [Test]
        public void Is_fast_block_finished_returns_true_when_receipts_not_downloaded_and_we_do_not_want_to_download_receipts()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            INodeDataDownloader nodeDataDownloader = Substitute.For<INodeDataDownloader>();
            SyncConfig syncConfig = new SyncConfig();
            syncConfig.FastBlocks = true;
            syncConfig.DownloadBodiesInFastSync = true;
            syncConfig.DownloadReceiptsInFastSync = false;

            blockTree.LowestInsertedHeader.Returns(Build.A.BlockHeader.WithNumber(1).TestObject);
            blockTree.LowestInsertedBody.Returns(Build.A.Block.WithNumber(1).TestObject);
            receiptStorage.LowestInsertedReceiptBlock.Returns(2);

            SyncProgressResolver syncProgressResolver = new SyncProgressResolver(blockTree, receiptStorage, nodeDataDownloader, syncConfig, LimboLogs.Instance);
            Assert.True(syncProgressResolver.IsFastBlocksFinished());
        }
    }
}