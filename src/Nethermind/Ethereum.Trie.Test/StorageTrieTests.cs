// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using NUnit.Framework;

namespace Ethereum.Trie.Test
{
    [TestFixture]
    public class StorageTrieTests
    {
        private StorageTree CreateStorageTrie()
        {
            return new StorageTree(TestTrieStoreFactory.Build(new MemDb(), LimboLogs.Instance).GetTrieStore(TestItem.KeccakA), Keccak.EmptyTreeHash, LimboLogs.Instance);
        }

        [Test]
        public void Storage_trie_set_reset_with_empty()
        {
            StorageTree tree = CreateStorageTrie();
            Hash256 rootBefore = tree.RootHash;
            tree.Set(1, new byte[] { 1 });
            tree.Set(1, new byte[] { });
            tree.UpdateRootHash();
            Hash256 rootAfter = tree.RootHash;
            Assert.That(rootAfter, Is.EqualTo(rootBefore));
        }

        [Test]
        public void Storage_trie_set_reset_with_long_zero()
        {
            StorageTree tree = CreateStorageTrie();
            Hash256 rootBefore = tree.RootHash;
            tree.Set(1, new byte[] { 1 });
            tree.Set(1, new byte[] { 0, 0, 0, 0, 0 });
            tree.UpdateRootHash();
            Hash256 rootAfter = tree.RootHash;
            Assert.That(rootAfter, Is.EqualTo(rootBefore));
        }

        [Test]
        public void Storage_trie_set_reset_with_short_zero()
        {
            StorageTree tree = CreateStorageTrie();
            Hash256 rootBefore = tree.RootHash;
            tree.Set(1, new byte[] { 1 });
            tree.Set(1, new byte[] { 0 });
            tree.UpdateRootHash();
            Hash256 rootAfter = tree.RootHash;
            Assert.That(rootAfter, Is.EqualTo(rootBefore));
        }

        [Test]
        public void Storage_trie_set_reset_with_32_byte_zero()
        {
            StorageTree tree = CreateStorageTrie();
            Hash256 rootBefore = tree.RootHash;
            tree.Set(1, new byte[] { 1 });
            tree.Set(1, new byte[32]); // Standard Ethereum storage size, all zeros
            tree.UpdateRootHash();
            Hash256 rootAfter = tree.RootHash;
            Assert.That(rootAfter, Is.EqualTo(rootBefore));
        }

        [Test]
        public void Storage_trie_set_reset_with_leading_zeros_and_nonzero()
        {
            StorageTree tree = CreateStorageTrie();
            Hash256 rootBefore = tree.RootHash;
            tree.Set(1, new byte[] { 1 });
            tree.Set(1, new byte[] { 0, 0, 1 }); // Leading zeros but has non-zero byte
            tree.UpdateRootHash();
            Hash256 rootAfter = tree.RootHash;
            Assert.That(rootAfter, Is.Not.EqualTo(rootBefore), "Value with non-zero bytes should not reset to empty tree");
        }

        [Test]
        public void Storage_trie_set_reset_with_zeros_in_middle()
        {
            StorageTree tree = CreateStorageTrie();
            Hash256 rootBefore = tree.RootHash;
            tree.Set(1, new byte[] { 1 });
            tree.Set(1, new byte[] { 1, 0, 0, 1 }); // Zeros in middle but has non-zero bytes
            tree.UpdateRootHash();
            Hash256 rootAfter = tree.RootHash;
            Assert.That(rootAfter, Is.Not.EqualTo(rootBefore), "Value with non-zero bytes should not reset to empty tree");
        }
    }
}
