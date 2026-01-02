// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

public class RawTrieStoreTests
{
    [Test]
    public void SmokeTest()
    {
        MemDb db = new MemDb();
        PatriciaTree patriciaTree = new PatriciaTree(new RawTrieStore(db).GetTrieStore(null), LimboLogs.Instance);

        patriciaTree.Set(TestItem.KeccakA.Bytes, TestItem.KeccakA.BytesToArray());
        patriciaTree.Set(TestItem.KeccakB.Bytes, TestItem.KeccakB.BytesToArray());
        patriciaTree.Set(TestItem.KeccakC.Bytes, TestItem.KeccakC.BytesToArray());

        patriciaTree.Commit();
        patriciaTree.RootHash.Should().NotBe(Keccak.EmptyTreeHash);

        Hash256 rootHash = patriciaTree.RootHash;

        // Recreate
        patriciaTree = new PatriciaTree(new RawTrieStore(db).GetTrieStore(null), LimboLogs.Instance);
        patriciaTree.RootHash = rootHash;

        patriciaTree.Get(TestItem.KeccakA.Bytes).ToArray().Should().BeEquivalentTo(TestItem.KeccakA.BytesToArray());
        patriciaTree.Get(TestItem.KeccakB.Bytes).ToArray().Should().BeEquivalentTo(TestItem.KeccakB.BytesToArray());
        patriciaTree.Get(TestItem.KeccakC.Bytes).ToArray().Should().BeEquivalentTo(TestItem.KeccakC.BytesToArray());
    }
}
