using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    [TestFixture]
    public class BlockhashProviderTests
    {
        [Test]
        public void Can_get_parent_hash()
        {
            const int chainLength = 512;

            Block genesis = Build.A.Block.Genesis.TestObject;
            
            BlockTree tree = Build.A.BlockTree(genesis).OfChainLength(chainLength).TestObject;

            BlockhashProvider provider = new BlockhashProvider(tree);
            BlockHeader head = tree.FindHeader(chainLength - 1);
            Block current = Build.A.Block.WithParent(head).TestObject;
            Keccak result = provider.GetBlockhash(current.Header, chainLength - 1);
            Assert.AreEqual(head.Hash, result);
        }

        [Test]
        public void Cannot_ask_for_self()
        {
            const int chainLength = 512;

            Block genesis = Build.A.Block.Genesis.TestObject;
            BlockTree tree = Build.A.BlockTree(genesis).OfChainLength(chainLength).TestObject;

            BlockhashProvider provider = new BlockhashProvider(tree);
            BlockHeader head = tree.FindHeader(chainLength - 1);
            Block current = Build.A.Block.WithParent(head).TestObject;
            Keccak result = provider.GetBlockhash(current.Header, chainLength);
            Assert.Null(result);
        }

        [Test]
        public void Cannot_ask_about_future()
        {
            const int chainLength = 512;

            Block genesis = Build.A.Block.Genesis.TestObject;
            BlockTree tree = Build.A.BlockTree(genesis).OfChainLength(chainLength).TestObject;

            BlockhashProvider provider = new BlockhashProvider(tree);
            BlockHeader head = tree.FindHeader(chainLength - 1);
            Block current = Build.A.Block.WithParent(head).TestObject;
            Keccak result = provider.GetBlockhash(current.Header, chainLength + 1);
            Assert.Null(result);
        }

        [Test]
        public void Can_lookup_up_to_256_before()
        {
            const int chainLength = 512;

            Block genesis = Build.A.Block.Genesis.TestObject;
            BlockTree tree = Build.A.BlockTree(genesis).OfChainLength(chainLength).TestObject;

            BlockhashProvider provider = new BlockhashProvider(tree);
            BlockHeader head = tree.FindHeader(chainLength - 1);
            Block current = Build.A.Block.WithParent(head).TestObject;
            Keccak result = provider.GetBlockhash(current.Header, chainLength - 256);
            Assert.NotNull(result);
        }

        [Test]
        public void No_lookup_more_than_256_before()
        {
            const int chainLength = 512;

            Block genesis = Build.A.Block.Genesis.TestObject;
            BlockTree tree = Build.A.BlockTree(genesis).OfChainLength(chainLength).TestObject;

            BlockhashProvider provider = new BlockhashProvider(tree);
            BlockHeader head = tree.FindHeader(chainLength - 1);
            Block current = Build.A.Block.WithParent(head).TestObject;
            Keccak result = provider.GetBlockhash(current.Header, chainLength - 257);
            Assert.Null(result);
        }
    }
}
