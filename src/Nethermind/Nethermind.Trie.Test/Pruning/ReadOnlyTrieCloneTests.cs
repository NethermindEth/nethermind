// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Db;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test.Pruning;

[TestFixture(INodeStorage.KeyScheme.HalfPath)]
[TestFixture(INodeStorage.KeyScheme.Hash)]
[Parallelizable(ParallelScope.All)]
public class ReadOnlyTrieCloneTests(INodeStorage.KeyScheme scheme)
{
    private readonly ILogManager _logManager = LimboLogs.Instance;

    [TestCase(false)]
    [TestCase(true)]
    public void Read_only_clones_own_their_rlp_bytes(bool pruning)
    {
        TrieNode node = new(NodeType.Branch);
        for (int i = 0; i < 16; i++)
        {
            node.SetChild(i, new TrieNode(NodeType.Unknown, TestItem.Keccaks[i]));
        }

        TreePath emptyPath = TreePath.Empty;

        using TrieStore fullTrieStore = CreateTrieStore(pruningStrategy: new TestPruningStrategy(pruning));
        IScopedTrieStore trieStore = fullTrieStore.GetTrieStore(null);
        node.ResolveKey(trieStore, ref emptyPath);

        using (ICommitter? committer = fullTrieStore.BeginStateBlockCommit(0, node))
        {
            committer.CommitNode(ref emptyPath, node);
        }

        TrieNode originalNode = trieStore.FindCachedOrUnknown(TreePath.Empty, node.Keccak);
        IReadOnlyTrieStore readOnlyTrieStore = fullTrieStore.AsReadOnly();
        TrieNode readOnlyNode = readOnlyTrieStore.FindCachedOrUnknown(null, TreePath.Empty, node.Keccak);

        Assert.That(readOnlyNode, Is.Not.SameAs(originalNode));

        CappedArray<byte> originalRlp = originalNode.FullRlp;
        CappedArray<byte> readOnlyRlp = readOnlyNode.FullRlp;

        Assert.Multiple(() =>
        {
            Assert.That(readOnlyRlp.AsSpan().ToArray(), Is.EqualTo(originalRlp.AsSpan().ToArray()));
            Assert.That(readOnlyRlp.UnderlyingArray, Is.Not.SameAs(originalRlp.UnderlyingArray));
        });

        byte firstReadOnlyByte = readOnlyRlp[0];
        originalRlp[0] ^= 1;

        Assert.Multiple(() =>
        {
            Assert.That(readOnlyNode.GetChildHash(0), Is.EqualTo(TestItem.Keccaks[0]));
            Assert.That(readOnlyNode.FullRlp[0], Is.EqualTo(firstReadOnlyByte));
        });
    }

    private TrieStore CreateTrieStore(
        IPruningStrategy? pruningStrategy = null,
        IKeyValueStoreWithBatching? kvStore = null,
        IPersistenceStrategy? persistenceStrategy = null,
        IPruningConfig? pruningConfig = null,
        IFinalizedStateProvider? finalizedStateProvider = null)
    {
        pruningStrategy ??= No.Pruning;
        kvStore ??= new TestMemDb();
        persistenceStrategy ??= No.Persistence;
        pruningConfig ??= new PruningConfig
        {
            TrackPastKeys = false
        };

        finalizedStateProvider ??= new TestFinalizedStateProvider(pruningConfig.PruningBoundary);
        TrieStore trieStore = new(
            new NodeStorage(kvStore, scheme, requirePath: scheme == INodeStorage.KeyScheme.HalfPath),
            pruningStrategy,
            persistenceStrategy,
            finalizedStateProvider,
            pruningConfig,
            _logManager);

        if (finalizedStateProvider is TestFinalizedStateProvider testFinalizedStateProvider)
        {
            testFinalizedStateProvider.TrieStore = trieStore;
        }

        return trieStore;
    }
}
