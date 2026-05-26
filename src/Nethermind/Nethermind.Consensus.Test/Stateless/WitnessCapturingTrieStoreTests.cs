// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Linq;
using FluentAssertions;
using Nethermind.Consensus.Stateless;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Stateless;

public class WitnessCapturingTrieStoreTests
{
    [Test]
    public void TryGetCachedNode_captures_cached_node_rlp()
    {
        using TrieStore baseStore = TestTrieStoreFactory.Build(new MemDb(), No.Pruning, No.Persistence, LimboLogs.Instance);
        PatriciaTree tree = new(baseStore, LimboLogs.Instance);
        using (baseStore.BeginBlockCommit(0))
        {
            tree.Set(TestItem.Keccaks[0].Bytes, TestItem.Keccaks[0].BytesToArray());
            tree.Commit();
        }

        TreePath path = TreePath.Empty;
        WitnessCapturingTrieStore trieStore = new(baseStore.AsReadOnly());
        ValueHash256 hash = tree.RootHash.ValueHash256;

        bool found = trieStore.TryGetCachedNode(null, in path, in hash, out TrieNode? cachedNode);

        found.Should().BeTrue();
        trieStore.TouchedNodesRlp.Single().Should().Equal(cachedNode!.FullRlp.ToArray());
    }
}
