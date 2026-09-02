// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtSnapshotBundleTests
{
    [Test]
    public void LocalCanonicalWrites_OverrideSharedAndPersistedLeaves()
    {
        PbtFullKey key = new([1]);
        ValueHash256 persisted = new([1]);
        ValueHash256 shared = new([2]);
        ValueHash256 local = new([3]);
        PbtResourcePool pool = new(new PbtConfig());
        PbtSnapshotContent sharedContent = new();
        sharedContent.SetLeaf(key, shared);
        PbtSnapshotPooledList sharedSnapshots = new(1)
        {
            new PbtSnapshot(StateId.PreGenesis, new StateId(1, default), default, sharedContent, pool, PbtResourcePool.Usage.MainBlockProcessing)
        };
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0), new PbtReadOnlySnapshotBundle(sharedSnapshots, new Reader(key, persisted)), pool, PbtResourcePool.Usage.MainBlockProcessing);
        Assert.That(bundle.GetLeaf(key), Is.EqualTo(shared));
        bundle.SetLeaf(key, local);
        Assert.That(bundle.GetLeaf(key), Is.EqualTo(local));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SnapshotStore_updater_failure_leaves_leaf_and_node_deltas_unchanged(bool hashMismatch)
    {
        PbtResourcePool pool = new(new PbtConfig());
        PbtFullKey originalLeafKey = new([1]);
        ValueHash256 originalLeafValue = new([2]);
        PbtNodePath originalNodePath = new([0x80], 1);
        byte[] originalNode = [0x7F];
        Reader reader = new(new PbtFullKey([0]), null);
        using PbtSnapshotBundle bundle = new(
            new PbtSnapshotPooledList(0),
            new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), reader),
            pool,
            PbtResourcePool.Usage.MainBlockProcessing);
        bundle.SetLeaf(originalLeafKey, originalLeafValue);
        bundle.ApplyTreeMutations([], [new PbtNodeMutation(originalNodePath, originalNode)]);

        PbtFullKey updateKey = new([3]);
        PbtWriteBatch initial = new();
        initial.Set(updateKey, new ValueHash256([4]));
        using PbtNodeGroupStore validStore = new();
        ValueHash256 validRoot = TrieUpdater.UpdateRoot(validStore, default, initial);
        PbtWriteBatch wrongNodeBatch = new();
        wrongNodeBatch.Set(new PbtFullKey([7]), new ValueHash256([8]));
        using PbtNodeGroupStore wrongNodeStore = new();
        TrieUpdater.UpdateRoot(wrongNodeStore, default, wrongNodeBatch);
        reader.Node = hashMismatch ? wrongNodeStore.GetNode(new PbtNodePath([], 0)) : null;
        PbtWriteBatch changes = new();
        changes.Set(new PbtFullKey([5]), new ValueHash256([6]));

        Assert.Throws<InvalidDataException>(() => TrieUpdater.UpdateRoot(new PbtSnapshotStore(bundle), validRoot, changes));
        using PbtSnapshot snapshot = bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), default);

        bool foundNode = snapshot.Content.TryGetNode(originalNodePath, out byte[]? node);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.Content.Leaves, Has.Count.EqualTo(1));
            Assert.That(snapshot.Content.TryGetLeaf(originalLeafKey, out ValueHash256? leaf) && leaf == originalLeafValue, Is.True);
            Assert.That(snapshot.Content.Nodes, Has.Count.EqualTo(1));
            Assert.That(foundNode, Is.True);
            Assert.That(node, Is.EqualTo(originalNode));
            Assert.That(snapshot.Content.TryGetLeaf(updateKey, out _), Is.False);
        }
    }

    [Test]
    public void CollectedSnapshot_ContainsCanonicalWritesAndRoot()
    {
        PbtFullKey key = new([1]);
        ValueHash256 value = new([2]);
        ValueHash256 root = new([3]);
        PbtResourcePool pool = new(new PbtConfig());
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0), new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), new Reader(new PbtFullKey([0]), null)), pool, PbtResourcePool.Usage.MainBlockProcessing);
        bundle.SetLeaf(key, value);
        using PbtSnapshot snapshot = bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), root);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.TreeRoot, Is.EqualTo(root));
            Assert.That(snapshot.Content.TryGetLeaf(key, out ValueHash256? actual) && actual == value, Is.True);
        }
    }

    private sealed class Reader(PbtFullKey key, ValueHash256? value) : IPbtPersistence.IReader
    {
        public byte[]? Node { get; set; }
        public StateId CurrentState => StateId.PreGenesis;
        public ValueHash256 CurrentRoot => default;
        public ValueHash256? GetLeaf(PbtFullKey requested) => requested == key ? value : null;
        public IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> EnumerateLeaves() => [];
        public IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> EnumerateLeaves(PbtFullKey prefix) => [];
        public byte[]? GetNode(PbtNodePath path) => Node;
        public IEnumerable<KeyValuePair<PbtNodePath, byte[]>> EnumerateNodes() => [];
        public ulong GetCodeReference(in ValueHash256 codeHash) => 0;
        public void Dispose() { }
    }
}
