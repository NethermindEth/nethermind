using Nevermind.Core.Crypto;
using Nevermind.Core.Encoding;
using Nevermind.Store;
using NUnit.Framework;

namespace Ethereum.Trie.Test
{
    [TestFixture]
    public class StorageTrieTests
    {
        [Test]
        public void Storage_trie_set_reset_with_empty()
        {
            StorageTree tree = new StorageTree(new InMemoryDb());
            Keccak rootBefore = tree.RootHash;
            tree.Set(1, new byte[] { 1 });
            tree.Set(1, new byte[] { });
            Keccak rootAfter = tree.RootHash;
            Assert.AreEqual(rootBefore, rootAfter);
        }

        [Test]
        public void Storage_trie_set_reset_with_long_zero()
        {
            StorageTree tree = new StorageTree(new InMemoryDb());
            Keccak rootBefore = tree.RootHash;
            tree.Set(1, new byte[] { 1 });
            tree.Set(1, new byte[] { 0, 0, 0, 0, 0 });
            Keccak rootAfter = tree.RootHash;
            Assert.AreEqual(rootBefore, rootAfter);
        }

        [Test]
        public void Storage_trie_set_reset_with_short_zero()
        {
            StorageTree tree = new StorageTree(new InMemoryDb());
            Keccak rootBefore = tree.RootHash;
            tree.Set(1, new byte[] { 1 });
            tree.Set(1, new byte[] { 0 });
            Keccak rootAfter = tree.RootHash;
            Assert.AreEqual(rootBefore, rootAfter);
        }
    }
}