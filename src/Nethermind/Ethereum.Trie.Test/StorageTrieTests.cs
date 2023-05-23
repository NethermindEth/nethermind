// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Ethereum.Trie.Test
{
    [TestFixture]
    public class StorageTrieTests
    {
        [Test]
        public void Storage_trie_set_reset_with_empty()
        {
            StorageTree tree = new StorageTree(new TrieStore(new MemDb(), LimboLogs.Instance), Keccak.EmptyTreeHash, LimboLogs.Instance);
            Keccak rootBefore = tree.RootHash;
            tree.Set(1, new byte[] { 1 });
            tree.Set(1, new byte[] { });
            tree.UpdateRootHash();
            Keccak rootAfter = tree.RootHash;
            Assert.That(rootAfter, Is.EqualTo(rootBefore));
        }

        [Test]
        public void Storage_trie_set_reset_with_long_zero()
        {
            StorageTree tree = new StorageTree(new TrieStore(new MemDb(), LimboLogs.Instance), Keccak.EmptyTreeHash, LimboLogs.Instance);
            Keccak rootBefore = tree.RootHash;
            tree.Set(1, new byte[] { 1 });
            tree.Set(1, new byte[] { 0, 0, 0, 0, 0 });
            tree.UpdateRootHash();
            Keccak rootAfter = tree.RootHash;
            Assert.That(rootAfter, Is.EqualTo(rootBefore));
        }

        [Test]
        public void Storage_trie_set_reset_with_short_zero()
        {
            StorageTree tree = new StorageTree(new TrieStore(new MemDb(), LimboLogs.Instance), Keccak.EmptyTreeHash, LimboLogs.Instance);
            Keccak rootBefore = tree.RootHash;
            tree.Set(1, new byte[] { 1 });
            tree.Set(1, new byte[] { 0 });
            tree.UpdateRootHash();
            Keccak rootAfter = tree.RootHash;
            Assert.That(rootAfter, Is.EqualTo(rootBefore));
        }
    }
}
