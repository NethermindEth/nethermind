// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

public class RawTrieStoreTests
{
    [TestCase(null)]
    [TestCase("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    public void CanWriteAndReadBack_PatriciaTrie(string? addressStr)
    {
        Hash256? address = null;
        if (addressStr is not null)
            address = new Hash256(addressStr);

        TestMemDb testDb = new TestMemDb();
        PatriciaTree tree = new PatriciaTree(new RawTrieStore(testDb, address), LimboLogs.Instance);

        tree.Set(TestItem.KeccakA.Bytes, TestItem.KeccakB.Bytes.ToArray());
        tree.Set(TestItem.KeccakC.Bytes, TestItem.KeccakD.Bytes.ToArray());
        tree.Commit();

        Hash256 root = tree.RootHash;
        testDb.Count.Should().Be(4);
        testDb[NodeStorage.GetHalfPathNodeStoragePath(address, TreePath.Empty, root)].Should().NotBeNull();

        tree = new PatriciaTree(new RawTrieStore(testDb, address), LimboLogs.Instance);
        tree.RootHash = root;
        tree.Get(TestItem.KeccakA.Bytes).ToArray().Should().BeEquivalentTo(TestItem.KeccakB.Bytes.ToArray());
        tree.Get(TestItem.KeccakC.Bytes).ToArray().Should().BeEquivalentTo( TestItem.KeccakD.Bytes.ToArray());
    }

}
