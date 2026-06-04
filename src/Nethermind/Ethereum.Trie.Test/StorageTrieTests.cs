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
        private StorageTree CreateStorageTrie() =>
            new(TestTrieStoreFactory.Build(new MemDb(), LimboLogs.Instance).GetTrieStore(TestItem.KeccakA), Keccak.EmptyTreeHash, LimboLogs.Instance);

        [TestCase(new byte[0], TestName = "Storage_trie_set_reset_with_empty")]
        [TestCase(new byte[] { 0, 0, 0, 0, 0 }, TestName = "Storage_trie_set_reset_with_long_zero")]
        [TestCase(new byte[] { 0 }, TestName = "Storage_trie_set_reset_with_short_zero")]
        public void Storage_trie_set_reset(byte[] resetValue)
        {
            StorageTree tree = CreateStorageTrie();
            Hash256 rootBefore = tree.RootHash;
            tree.Set(1, [1]);
            tree.Set(1, resetValue);
            tree.UpdateRootHash();
            Assert.That(tree.RootHash, Is.EqualTo(rootBefore));
        }
    }
}
