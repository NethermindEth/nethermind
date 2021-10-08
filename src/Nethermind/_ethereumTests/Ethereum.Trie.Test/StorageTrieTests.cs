/*
 * Copyright (c) 2021 Demerzel Solutions Limited
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
            Assert.AreEqual(rootBefore, rootAfter);
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
            Assert.AreEqual(rootBefore, rootAfter);
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
            Assert.AreEqual(rootBefore, rootAfter);
        }
    }
}
