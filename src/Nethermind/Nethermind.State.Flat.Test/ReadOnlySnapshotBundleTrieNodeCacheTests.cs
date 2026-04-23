// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class ReadOnlySnapshotBundleTrieNodeCacheTests
{
    [Test]
    public void TryFindStateNodes_UsesGlobalTrieNodeCache()
    {
        IPersistence.IPersistenceReader persistenceReader = Substitute.For<IPersistence.IPersistenceReader>();
        ITrieNodeCache trieNodeCache = Substitute.For<ITrieNodeCache>();
        TreePath path = TreePath.FromHexString("1234");
        Hash256 hash = Keccak.Compute([1, 2, 3]);
        TrieNode cachedNode = new(NodeType.Leaf, hash);

        trieNodeCache.TryGet(null, path, hash, out Arg.Any<TrieNode?>())
            .Returns(call =>
            {
                call[3] = cachedNode;
                return true;
            });

        using ReadOnlySnapshotBundle bundle = new(new SnapshotPooledList(0), persistenceReader, false, trieNodeCache);

        bool found = bundle.TryFindStateNodes(path, hash, out TrieNode? node);

        Assert.That(found, Is.True);
        Assert.That(node, Is.SameAs(cachedNode));
    }

    [Test]
    public void TryFindStorageNodes_UsesGlobalTrieNodeCache()
    {
        IPersistence.IPersistenceReader persistenceReader = Substitute.For<IPersistence.IPersistenceReader>();
        ITrieNodeCache trieNodeCache = Substitute.For<ITrieNodeCache>();
        Hash256 address = Keccak.Compute([0x10, 0x20]);
        TreePath path = TreePath.FromHexString("abcd");
        Hash256 hash = Keccak.Compute([4, 5, 6]);
        TrieNode cachedNode = new(NodeType.Extension, hash);

        trieNodeCache.TryGet(address, path, hash, out Arg.Any<TrieNode?>())
            .Returns(call =>
            {
                call[3] = cachedNode;
                return true;
            });

        using ReadOnlySnapshotBundle bundle = new(new SnapshotPooledList(0), persistenceReader, false, trieNodeCache);

        bool found = bundle.TryFindStorageNodes(address, path, hash, out TrieNode? node);

        Assert.That(found, Is.True);
        Assert.That(node, Is.SameAs(cachedNode));
    }
}
