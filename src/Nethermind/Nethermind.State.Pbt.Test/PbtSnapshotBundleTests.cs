// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
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
        PbtSnapshotContent sharedContent = new(); sharedContent.SetLeaf(key, shared);
        PbtSnapshotPooledList sharedSnapshots = new(1);
        sharedSnapshots.Add(new PbtSnapshot(StateId.PreGenesis, new StateId(1, default), default, sharedContent, pool, PbtResourcePool.Usage.MainBlockProcessing));
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0), new PbtReadOnlySnapshotBundle(sharedSnapshots, new Reader(key, persisted)), pool, PbtResourcePool.Usage.MainBlockProcessing);
        Assert.That(bundle.GetLeaf(key), Is.EqualTo(shared));
        bundle.SetLeaf(key, local);
        Assert.That(bundle.GetLeaf(key), Is.EqualTo(local));
    }

    [Test]
    public void CollectedSnapshot_ContainsCanonicalWritesAndRoot()
    {
        PbtFullKey key = new([1]);
        ValueHash256 value = new([2]);
        ValueHash256 root = new([3]);
        PbtResourcePool pool = new(new PbtConfig());
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0), new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), new Reader(default, null)), pool, PbtResourcePool.Usage.MainBlockProcessing);
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
        public StateId CurrentState => StateId.PreGenesis;
        public ValueHash256 CurrentRoot => default;
        public ValueHash256? GetLeaf(PbtFullKey requested) => requested == key ? value : null;
        public IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> EnumerateLeaves() => [];
        public IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> EnumerateLeaves(PbtFullKey prefix) => [];
        public byte[]? GetNode(PbtFullKey locator) => null;
        public IEnumerable<KeyValuePair<PbtFullKey, byte[]>> EnumerateNodes() => [];
        public ulong GetCodeReference(in ValueHash256 codeHash) => 0;
        public void Dispose() { }
    }
}
