using Nevermind.Blockchain;
using Nevermind.Core.Crypto;
using NUnit.Framework;

namespace Nevermind.Core.Test
{
    [TestFixture]
    public class BlockStoreTests
    {
        [Test]
        public void Add_and_find_on_main()
        {
            BlockStore blockStore = new BlockStore();
            Block block = BuildTestBlock();
            blockStore.AddBlock(block, true);
            Block found = blockStore.FindBlock(block.Hash, true);
            Assert.AreSame(block, found);
        }

        [Test]
        public void Add_and_find_branch()
        {
            BlockStore blockStore = new BlockStore();
            Block block = BuildTestBlock();
            blockStore.AddBlock(block, false);
            Block found = blockStore.FindBlock(block.Hash, false);
            Assert.AreSame(block, found);
        }

        [Test]
        public void Add_on_branch_move_find()
        {
            BlockStore blockStore = new BlockStore();
            Block block = BuildTestBlock();
            blockStore.AddBlock(block, false);
            blockStore.MoveToMain(block.Hash);
            Block found = blockStore.FindBlock(block.Hash, true);
            Assert.AreSame(block, found);
        }
        
        [Test]
        public void Add_on_main_move_find()
        {
            BlockStore blockStore = new BlockStore();
            Block block = BuildTestBlock();
            blockStore.AddBlock(block, true);
            blockStore.MoveToBranch(block.Hash);
            Block found = blockStore.FindBlock(block.Hash, false);
            Assert.AreSame(block, found);
        }
        
        [Test]
        public void Add_on_branch_and_not_find_on_main()
        {
            BlockStore blockStore = new BlockStore();
            Block block = BuildTestBlock();
            blockStore.AddBlock(block, false);
            Block found = blockStore.FindBlock(block.Hash, true);
            Assert.IsNull(found);
        }

        private static Block BuildTestBlock()
        {
            BlockHeader header = new BlockHeader(Keccak.Compute("parent"), Keccak.OfAnEmptySequenceRlp, new Address(Keccak.Compute("address")), 0, 0, 0, 0, new byte[0]);
            header.Bloom = new Bloom();
            header.GasUsed = 0;
            header.MixHash = Keccak.Compute("mix hash");
            header.Nonce = 0;
            header.OmmersHash = Keccak.OfAnEmptySequenceRlp;
            header.ReceiptsRoot = Keccak.EmptyTreeHash;
            header.StateRoot = Keccak.EmptyTreeHash;
            header.TransactionsRoot = Keccak.EmptyTreeHash;
            header.RecomputeHash();
            Block block = new Block(header);
            return block;
        }
    }
}