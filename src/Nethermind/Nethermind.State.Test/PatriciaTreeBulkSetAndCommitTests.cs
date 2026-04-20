// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Store.Test;

public class PatriciaTreeBulkSetAndCommitTests
{
    private sealed class CountingCommitter(ICommitter inner) : ICommitter
    {
        public int Count;
        public void Dispose() => inner.Dispose();
        public TrieNode CommitNode(ref TreePath path, TrieNode node)
        {
            Interlocked.Increment(ref Count);
            return inner.CommitNode(ref path, node);
        }
    }

    private sealed class CountingStore(IScopedTrieStore inner) : IScopedTrieStore
    {
        public CountingCommitter LastCommitter;
        public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) => inner.FindCachedOrUnknown(in path, hash);
        public byte[] LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => inner.LoadRlp(in path, hash, flags);
        public byte[] TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => inner.TryLoadRlp(in path, hash, flags);
        public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256 address) => inner.GetStorageTrieNodeResolver(address);
        public INodeStorage.KeyScheme Scheme => inner.Scheme;
        public ICommitter BeginCommit(TrieNode root, WriteFlags writeFlags = WriteFlags.None)
        {
            LastCommitter = new CountingCommitter(inner.BeginCommit(root, writeFlags));
            return LastCommitter;
        }
    }
    private static ArrayPoolListRef<PatriciaTree.BulkSetEntry> BuildEntries(
        List<(Hash256 key, byte[] value)> items, bool sorted = false)
    {
        if (sorted)
            items = items.OrderBy(kv => kv.key).ToList();

        ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = new(items.Count);
        foreach ((Hash256 key, byte[] value) in items)
            entries.Add(new PatriciaTree.BulkSetEntry(key, value));
        return entries;
    }

    private static (Hash256 rootHash, TestMemDb db) RunReference(
        List<(Hash256 key, byte[] value)> existingItems,
        List<(Hash256 key, byte[] value)> items,
        PatriciaTree.Flags flags = PatriciaTree.Flags.None)
    {
        TestMemDb db = new();
        IScopedTrieStore store = new PatriciaTreeBulkSetterTests.StrictRawScopedTrieStore(new RawScopedTrieStore(db));
        PatriciaTree tree = new(store, LimboLogs.Instance);
        foreach ((Hash256 key, byte[] value) in existingItems) tree.Set(key.Bytes, value);
        tree.Commit();

        bool needSort = flags.HasFlag(PatriciaTree.Flags.WasSorted);
        using ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = BuildEntries(items, needSort);
        tree.BulkSet(entries, flags);
        tree.Commit();
        return (tree.RootHash, db);
    }

    private static (Hash256 rootHash, TestMemDb db) RunCandidate(
        List<(Hash256 key, byte[] value)> existingItems,
        List<(Hash256 key, byte[] value)> items,
        PatriciaTree.Flags flags = PatriciaTree.Flags.None,
        bool skipRoot = false)
    {
        TestMemDb db = new();
        IScopedTrieStore store = new PatriciaTreeBulkSetterTests.StrictRawScopedTrieStore(new RawScopedTrieStore(db));
        PatriciaTree tree = new(store, LimboLogs.Instance);
        foreach ((Hash256 key, byte[] value) in existingItems) tree.Set(key.Bytes, value);
        tree.Commit();

        bool needSort = flags.HasFlag(PatriciaTree.Flags.WasSorted);
        using ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = BuildEntries(items, needSort);
        tree.BulkSetAndCommit(entries, flags, skipRoot);
        return (tree.RootHash, db);
    }

    private static void AssertDbEqual(TestMemDb expected, TestMemDb actual)
    {
        Dictionary<string, string> ToHexDict(TestMemDb db) =>
            db.GetAll().ToDictionary(
                kv => Convert.ToHexString(kv.Key),
                kv => kv.Value is null ? "" : Convert.ToHexString(kv.Value));

        actual.WritesCount.Should().Be(expected.WritesCount);
        ToHexDict(actual).Should().BeEquivalentTo(ToHexDict(expected));
    }

    [TestCaseSource(typeof(PatriciaTreeBulkSetterTests), nameof(PatriciaTreeBulkSetterTests.BulkSetTestGen))]
    public void BulkSetAndCommit_MatchesBulkSetPlusCommit(
        List<(Hash256 key, byte[] value)> existingItems,
        List<(Hash256 key, byte[] value)> items)
    {
        (Hash256 refRoot, TestMemDb refDb) = RunReference(existingItems, items);

        foreach (PatriciaTree.Flags flags in new[] { PatriciaTree.Flags.None, PatriciaTree.Flags.DoNotParallelize, PatriciaTree.Flags.WasSorted })
        {
            (Hash256 candidateRoot, TestMemDb candidateDb) = RunCandidate(existingItems, items, flags);
            (Hash256 expectedRoot, TestMemDb expectedDb) = flags == PatriciaTree.Flags.None ? (refRoot, refDb) : RunReference(existingItems, items, flags);
            candidateRoot.Should().Be(expectedRoot, $"flags={flags}");
            AssertDbEqual(expectedDb, candidateDb);
        }
    }

    [TestCaseSource(typeof(PatriciaTreeBulkSetterTests), nameof(PatriciaTreeBulkSetterTests.BulkSetTestGen))]
    public void BulkSetAndCommit_CommitNodeCount_MatchesBulkSetPlusCommit(
        List<(Hash256 key, byte[] value)> existingItems,
        List<(Hash256 key, byte[] value)> items)
    {
        foreach (PatriciaTree.Flags flags in new[] { PatriciaTree.Flags.None, PatriciaTree.Flags.DoNotParallelize, PatriciaTree.Flags.WasSorted })
        {
            // Reference: BulkSet + Commit
            TestMemDb refDb = new();
            CountingStore refStore = new(new PatriciaTreeBulkSetterTests.StrictRawScopedTrieStore(new RawScopedTrieStore(refDb)));
            PatriciaTree refTree = new(refStore, LimboLogs.Instance);
            foreach ((Hash256 key, byte[] value) in existingItems) refTree.Set(key.Bytes, value);
            refTree.Commit();
            bool needSort = flags.HasFlag(PatriciaTree.Flags.WasSorted);
            using (ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = BuildEntries(items, needSort))
                refTree.BulkSet(entries, flags);
            refTree.Commit();
            int refCount = refStore.LastCommitter?.Count ?? 0;

            // Candidate: BulkSetAndCommit
            TestMemDb candDb = new();
            CountingStore candStore = new(new PatriciaTreeBulkSetterTests.StrictRawScopedTrieStore(new RawScopedTrieStore(candDb)));
            PatriciaTree candTree = new(candStore, LimboLogs.Instance);
            foreach ((Hash256 key, byte[] value) in existingItems) candTree.Set(key.Bytes, value);
            candTree.Commit();
            using (ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = BuildEntries(items, needSort))
                candTree.BulkSetAndCommit(entries, flags);
            int candCount = candStore.LastCommitter?.Count ?? 0;

            candCount.Should().Be(refCount, $"CommitNode call count mismatch for flags={flags}");
        }
    }

    [Test]
    public void BulkSetAndCommit_SkipRoot_DoesNotWriteRoot()
    {
        List<(Hash256 key, byte[] value)> items =
        [
            (new Hash256("aaaa000000000000000000000000000000000000000000000000000000000000"), new byte[] { 1, 2, 3, 4 }),
            (new Hash256("bbbb000000000000000000000000000000000000000000000000000000000000"), new byte[] { 5, 6, 7, 8 }),
        ];

        TestMemDb db = new();
        // Use plain RawScopedTrieStore (not strict) because skipRoot=true means the root is not persisted,
        // and StrictRawScopedTrieStore would throw when SetRootHash tries to load the unpersisted root.
        IScopedTrieStore store = new RawScopedTrieStore(db);
        PatriciaTree tree = new(store, LimboLogs.Instance);

        using ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = BuildEntries(items);
        tree.BulkSetAndCommit(entries, skipRoot: true);

        Hash256 rootHash = tree.RootHash;
        rootHash.Should().NotBe(Keccak.EmptyTreeHash);
        rootHash.Should().NotBeNull();

        // Root should NOT be written to DB when skipRoot=true, but interior nodes should be written
        db.WritesCount.Should().BeGreaterThan(0);

        // With skipRoot, root node is not persisted but root hash is still computed
        // Build a reference with skipRoot=false and verify it writes one more node
        TestMemDb dbWithRoot = new();
        PatriciaTree treeWithRoot = new(
            new RawScopedTrieStore(dbWithRoot),
            LimboLogs.Instance);
        using ArrayPoolListRef<PatriciaTree.BulkSetEntry> entriesWithRoot = BuildEntries(items);
        treeWithRoot.BulkSetAndCommit(entriesWithRoot, skipRoot: false);

        treeWithRoot.RootHash.Should().Be(rootHash);
        dbWithRoot.WritesCount.Should().BeGreaterThanOrEqualTo(db.WritesCount);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void BulkSetAndCommit_EmptyEntries(bool preDirty)
    {
        TestMemDb db = new();
        IScopedTrieStore store = new PatriciaTreeBulkSetterTests.StrictRawScopedTrieStore(new RawScopedTrieStore(db));
        PatriciaTree tree = new(store, LimboLogs.Instance);

        tree.Set(new Hash256("aaaa000000000000000000000000000000000000000000000000000000000000").Bytes, new byte[] { 1 });

        if (!preDirty)
        {
            tree.Commit();
            long writesBeforeEmpty = db.WritesCount;

            using ArrayPoolListRef<PatriciaTree.BulkSetEntry> empty = new(0);
            tree.BulkSetAndCommit(empty);
            db.WritesCount.Should().Be(writesBeforeEmpty, "clean tree: empty BulkSetAndCommit should not write");
        }
        else
        {
            // dirty root: empty BulkSetAndCommit should commit the dirty root
            using ArrayPoolListRef<PatriciaTree.BulkSetEntry> empty = new(0);
            tree.BulkSetAndCommit(empty);
            db.WritesCount.Should().BeGreaterThan(0, "dirty root should be committed");
            tree.RootHash.Should().NotBe(Keccak.EmptyTreeHash);
        }
    }

    [Test]
    public void BulkSetAndCommit_ThrowsOnReadOnlyTree()
    {
        IScopedTrieStore store = new RawScopedTrieStore(new TestMemDb());
        PatriciaTree tree = new(store, Keccak.EmptyTreeHash, allowCommits: false, LimboLogs.Instance);

        bool threw = false;
        try
        {
            using ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = new(1);
            entries.Add(new PatriciaTree.BulkSetEntry(
                new Hash256("aaaa000000000000000000000000000000000000000000000000000000000000"),
                new byte[] { 1 }));
            tree.BulkSetAndCommit(entries);
        }
        catch (TrieException) { threw = true; }

        threw.Should().BeTrue();
    }

    [Test]
    public void BulkSetAndCommit_ThrowsOnNonUniqueEntries()
    {
        IScopedTrieStore store = new PatriciaTreeBulkSetterTests.StrictRawScopedTrieStore(new RawScopedTrieStore(new TestMemDb()));
        PatriciaTree tree = new(store, LimboLogs.Instance);

        bool threw = false;
        try
        {
            using ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = new(4);
            entries.Add(new PatriciaTree.BulkSetEntry(new ValueHash256("8818888888888888888888888888888888888888888888888888888888888888"), new byte[] { 1 }));
            entries.Add(new PatriciaTree.BulkSetEntry(new ValueHash256("8828888888888888888888888888888888888888888888888888888888888888"), new byte[] { 2 }));
            entries.Add(new PatriciaTree.BulkSetEntry(new ValueHash256("8848888888888888888888888888888888888888888888888888888888888888"), new byte[] { 3 }));
            entries.Add(new PatriciaTree.BulkSetEntry(new ValueHash256("8848888888888888888888888888888888888888888888888888888888888888"), new byte[] { 4 }));
            tree.BulkSetAndCommit(entries);
        }
        catch (InvalidOperationException) { threw = true; }

        threw.Should().BeTrue();
    }
}
