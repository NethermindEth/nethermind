// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

[Parallelizable(ParallelScope.All)]
public class WarmUpPathForDeleteTests
{
    // A and B differ only in the last nibble, so they hang from one branch at depth 63; C sits under another root child.
    private static readonly byte[] KeyA = Bytes.FromHexString("0x1000000000000000000000000000000000000000000000000000000000000001");
    private static readonly byte[] KeyB = Bytes.FromHexString("0x1000000000000000000000000000000000000000000000000000000000000002");
    private static readonly byte[] KeyC = Bytes.FromHexString("0x2000000000000000000000000000000000000000000000000000000000000000");
    private static readonly byte[] KeyD = Bytes.FromHexString("0x1000000000000000000000000000000000000000000000000000000000000003");

    // Over 32 bytes of RLP, so every leaf is referenced by hash and has to be read from the database.
    private static readonly byte[] Value = Bytes.FromHexString("0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

    [Test]
    public void Delete_that_collapses_its_branch_reads_nothing_once_warmed_for_delete()
    {
        (CountingMemDb db, Hash256 root) = Commit(KeyA, KeyB, KeyC);
        PatriciaTree tree = OpenCold(db, root);

        tree.WarmUpPath(KeyA, forDelete: true);
        int readsBeforeDelete = db.Reads;
        tree.Set(KeyA, []);
        tree.UpdateRootHash();

        Assert.That(db.Reads - readsBeforeDelete, Is.Zero, "the delete read a node the warm-up should have loaded");
        Assert.That(tree.RootHash, Is.EqualTo(Commit(KeyB, KeyC).Root));
    }

    [Test]
    public void Delete_that_collapses_its_branch_reads_the_sibling_when_only_the_path_is_warmed()
    {
        (CountingMemDb db, Hash256 root) = Commit(KeyA, KeyB, KeyC);
        PatriciaTree tree = OpenCold(db, root);

        tree.WarmUpPath(KeyA);
        int readsBeforeDelete = db.Reads;
        tree.Set(KeyA, []);
        tree.UpdateRootHash();

        // The branch collapses into B's leaf, which is off A's path.
        Assert.That(db.Reads - readsBeforeDelete, Is.EqualTo(1));
        Assert.That(tree.RootHash, Is.EqualTo(Commit(KeyB, KeyC).Root));
    }

    [Test]
    public void Delete_from_a_branch_that_keeps_two_children_warms_nothing_beyond_the_path()
    {
        (CountingMemDb db, Hash256 root) = Commit(KeyA, KeyB, KeyC, KeyD);

        PatriciaTree pathOnly = OpenCold(db, root);
        int before = db.Reads;
        pathOnly.WarmUpPath(KeyA);
        int pathReads = db.Reads - before;

        PatriciaTree forDelete = OpenCold(db, root);
        before = db.Reads;
        forDelete.WarmUpPath(KeyA, forDelete: true);
        Assert.That(db.Reads - before, Is.EqualTo(pathReads), "no collapse, so nothing past the path is needed");

        before = db.Reads;
        forDelete.Set(KeyA, []);
        forDelete.UpdateRootHash();
        Assert.That(db.Reads - before, Is.Zero);
        Assert.That(forDelete.RootHash, Is.EqualTo(Commit(KeyB, KeyC, KeyD).Root));
    }

    [Test]
    public void Warming_a_missing_key_for_delete_reads_no_sibling()
    {
        (CountingMemDb db, Hash256 root) = Commit(KeyA, KeyB, KeyC);
        byte[] missing = Bytes.FromHexString("0x1000000000000000000000000000000000000000000000000000000000000004");

        PatriciaTree pathOnly = OpenCold(db, root);
        int before = db.Reads;
        pathOnly.WarmUpPath(missing);
        int pathReads = db.Reads - before;

        PatriciaTree forDelete = OpenCold(db, root);
        before = db.Reads;
        Assert.That(() => forDelete.WarmUpPath(missing, forDelete: true), Throws.Nothing);
        Assert.That(db.Reads - before, Is.EqualTo(pathReads));
    }

    private static (CountingMemDb Db, Hash256 Root) Commit(params byte[][] keys)
    {
        CountingMemDb db = new();
        PatriciaTree tree = new(new RawScopedTrieStore(db), NullLogManager.Instance);
        foreach (byte[] key in keys) tree.Set(key, Value);
        tree.Commit();
        return (db, tree.RootHash);
    }

    private static PatriciaTree OpenCold(CountingMemDb db, Hash256 root) =>
        new(new PathCachingTrieStore(db), NullLogManager.Instance) { RootHash = root };

    private sealed class CountingMemDb : MemDb
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public override byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            Interlocked.Increment(ref _reads);
            return base.Get(key, flags);
        }
    }

    /// <summary>Hands out one node object per path and hash, as the real stores do, so a warmed node is the one a later write finds.</summary>
    private sealed class PathCachingTrieStore(IKeyValueStoreWithBatching db) : RawScopedTrieStore(db), IScopedTrieStore
    {
        private readonly ConcurrentDictionary<(TreePath, Hash256), TrieNode> _nodes = new();

        TrieNode ITrieNodeResolver.FindCachedOrUnknown(in TreePath path, Hash256 hash) =>
            _nodes.GetOrAdd((path, hash), static key => new TrieNode(NodeType.Unknown, key.Item2));
    }
}
