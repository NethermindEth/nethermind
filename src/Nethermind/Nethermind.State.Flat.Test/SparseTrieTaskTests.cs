// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Nethermind.Trie.Sparse;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// M4 shadow-pipeline tests. The streamed background <see cref="SparseTrieTask"/> must produce
/// exactly the same state root as both the synchronous <see cref="SparseRootComputer"/> (M3) and
/// the authoritative <see cref="PatriciaTree"/>. Until that equivalence holds for every shape,
/// the M4 pipeline stays shadow-only and never affects consensus.
/// </summary>
[TestFixture]
public class SparseTrieTaskTests
{
    private static (Hash256 block1Root, Hash256 block2Root, byte[][] newRlps, MemDb db) BuildTwoBlocks(int total, int changed)
    {
        MemDb trieDb = new();
        PatriciaTree tree = new(new RawTrieStore(trieDb).GetTrieStore(null), LimboLogs.Instance);
        for (int i = 0; i < total; i++)
            tree.Set(TestItem.Keccaks[i].Bytes, TestItem.GenerateIndexedAccountRlp(i));
        tree.UpdateRootHash();
        tree.Commit();
        Hash256 block1Root = tree.RootHash;

        byte[][] newRlps = new byte[changed][];
        for (int i = 0; i < changed; i++)
        {
            newRlps[i] = TestItem.GenerateIndexedAccountRlp(1000 + i);
            tree.Set(TestItem.Keccaks[i].Bytes, newRlps[i]);
        }
        tree.UpdateRootHash();
        tree.Commit();
        return (block1Root, tree.RootHash, newRlps, trieDb);
    }

    [Test]
    public async Task StreamedRoot_MatchesSynchronousAndPatricia()
    {
        (Hash256 block1Root, Hash256 block2Root, byte[][] newRlps, MemDb db) = BuildTwoBlocks(total: 20, changed: 5);
        HalfPathTrieNodeReader reader = new(new NodeStorage(db));

        // Synchronous M3 baseline.
        using SparseRootComputer syncComputer = new(reader, block1Root);
        Dictionary<ValueHash256, LeafUpdate> updates = [];
        for (int i = 0; i < newRlps.Length; i++)
            updates[TestItem.Keccaks[i]] = LeafUpdate.Changed(newRlps[i]);
        syncComputer.SetAccountChanges(updates);
        Hash256 syncRoot = syncComputer.ComputeStateRoot();
        Assert.That(syncRoot, Is.EqualTo(block2Root), "synchronous sparse must match Patricia");

        // M4 streamed path: feed the same updates as a delta batch through the background task.
        using SparseRootComputer streamComputer = new(new HalfPathTrieNodeReader(new NodeStorage(db)), block1Root);
        await using SparseTrieTask task = new(streamComputer, LimboLogs.Instance.GetClassLogger<SparseTrieTaskTests>(), CancellationToken.None);

        List<(ValueHash256, LeafUpdate)> accountUpdates = [];
        for (int i = 0; i < newRlps.Length; i++)
            accountUpdates.Add((TestItem.Keccaks[i].ValueHash256, LeafUpdate.Changed(newRlps[i])));
        task.Enqueue(new SparseTrieTask.HashedDelta(accountUpdates, []));
        task.Finish();

        Hash256 streamedRoot = await task.GetRootAsync();
        Assert.That(streamedRoot, Is.EqualTo(block2Root), "streamed M4 root must match Patricia");
        Assert.That(streamedRoot, Is.EqualTo(syncRoot), "streamed M4 root must match synchronous sparse root");
    }

    [Test]
    public async Task StreamedRoot_MultipleDeltaBatches_MatchesSingleBatch()
    {
        // The same total change set delivered as several commit-phase batches (system pre-tx,
        // per-tx, rewards) must reach the same root as one batch â€” later batches supersede
        // earlier ones for the same key, exactly like the synchronous accumulation.
        (Hash256 block1Root, _, byte[][] _, MemDb db) = BuildTwoBlocks(total: 20, changed: 5);

        byte[] finalRlp = TestItem.GenerateIndexedAccountRlp(9999);

        using SparseRootComputer syncComputer = new(new HalfPathTrieNodeReader(new NodeStorage(db)), block1Root);
        syncComputer.SetAccountChanges(new Dictionary<ValueHash256, LeafUpdate>
        {
            [TestItem.Keccaks[0].ValueHash256] = LeafUpdate.Changed(finalRlp),
        });
        Hash256 syncRoot = syncComputer.ComputeStateRoot();

        using SparseRootComputer streamComputer = new(new HalfPathTrieNodeReader(new NodeStorage(db)), block1Root);
        await using SparseTrieTask task = new(streamComputer, LimboLogs.Instance.GetClassLogger<SparseTrieTaskTests>(), CancellationToken.None);

        // Batch 1: an intermediate value for Keccaks[0].
        task.Enqueue(new SparseTrieTask.HashedDelta(
            [(TestItem.Keccaks[0].ValueHash256, LeafUpdate.Changed(TestItem.GenerateIndexedAccountRlp(1)))], []));
        // Batch 2: the final value supersedes it.
        task.Enqueue(new SparseTrieTask.HashedDelta(
            [(TestItem.Keccaks[0].ValueHash256, LeafUpdate.Changed(finalRlp))], []));
        task.Finish();

        Hash256 streamedRoot = await task.GetRootAsync();
        Assert.That(streamedRoot, Is.EqualTo(syncRoot), "last-writer-wins across batches must match single-batch root");
    }

    [Test]
    public async Task CancelledDrain_PoisonsResult_ForcesFallback()
    {
        // A cancelled (or faulted) drain leaves the accumulation incomplete. GetRootAsync must
        // refuse to return a root from it so a future production wiring is forced to fall back to
        // the synchronous path rather than commit a wrong root.
        (Hash256 block1Root, _, byte[][] newRlps, MemDb db) = BuildTwoBlocks(total: 20, changed: 5);
        using CancellationTokenSource cts = new();

        using SparseRootComputer streamComputer = new(new HalfPathTrieNodeReader(new NodeStorage(db)), block1Root);
        await using SparseTrieTask task = new(streamComputer, LimboLogs.Instance.GetClassLogger<SparseTrieTaskTests>(), cts.Token);

        task.Enqueue(new SparseTrieTask.HashedDelta(
            [(TestItem.Keccaks[0].ValueHash256, LeafUpdate.Changed(newRlps[0]))], []));
        cts.Cancel();          // poison the drain
        task.Finish();

        Func<Task> act = async () => await task.GetRootAsync();
        Assert.ThrowsAsync<InvalidOperationException>(
            async () => await act(),
            "a cancelled/poisoned drain must not yield a trusted root");
    }

    [Test]
    public async Task PrefetchOnly_DoesNotChangeRoot()
    {
        // Prefetch targets insert Touched markers, which are no-ops on the root. A block that
        // only prefetches (no real writes) must reproduce the parent root exactly.
        (Hash256 block1Root, _, _, MemDb db) = BuildTwoBlocks(total: 20, changed: 5);

        using SparseRootComputer streamComputer = new(new HalfPathTrieNodeReader(new NodeStorage(db)), block1Root);
        await using SparseTrieTask task = new(streamComputer, LimboLogs.Instance.GetClassLogger<SparseTrieTaskTests>(), CancellationToken.None);

        // Prefetch several account keys, no real updates.
        List<ValueHash256> prefetch = [];
        for (int i = 0; i < 8; i++) prefetch.Add(TestItem.Keccaks[i].ValueHash256);
        task.Enqueue(new SparseTrieTask.HashedDelta([], [], PrefetchAccounts: prefetch));
        task.Finish();

        Hash256 root = await task.GetRootAsync();
        Assert.That(root, Is.EqualTo(block1Root), "prefetch-only (Touched markers) must leave the root unchanged");
    }

    [Test]
    public async Task PrefetchThenUpdate_MatchesUpdateAlone()
    {
        // Prefetching the same keys that are later really written must not change the result vs
        // writing them with no prefetch. Touched is superseded by the real Changed update.
        (Hash256 block1Root, _, byte[][] newRlps, MemDb db) = BuildTwoBlocks(total: 20, changed: 5);

        using SparseRootComputer baseline = new(new HalfPathTrieNodeReader(new NodeStorage(db)), block1Root);
        Dictionary<ValueHash256, LeafUpdate> updates = [];
        for (int i = 0; i < newRlps.Length; i++)
            updates[TestItem.Keccaks[i].ValueHash256] = LeafUpdate.Changed(newRlps[i]);
        baseline.SetAccountChanges(updates);
        Hash256 baselineRoot = baseline.ComputeStateRoot();

        using SparseRootComputer streamComputer = new(new HalfPathTrieNodeReader(new NodeStorage(db)), block1Root);
        await using SparseTrieTask task = new(streamComputer, LimboLogs.Instance.GetClassLogger<SparseTrieTaskTests>(), CancellationToken.None);

        // Batch 1: prefetch the keys (and some extras). Batch 2: the real writes.
        List<ValueHash256> prefetch = [];
        for (int i = 0; i < 10; i++) prefetch.Add(TestItem.Keccaks[i].ValueHash256);
        task.Enqueue(new SparseTrieTask.HashedDelta([], [], PrefetchAccounts: prefetch));

        List<(ValueHash256, LeafUpdate)> realWrites = [];
        for (int i = 0; i < newRlps.Length; i++)
            realWrites.Add((TestItem.Keccaks[i].ValueHash256, LeafUpdate.Changed(newRlps[i])));
        task.Enqueue(new SparseTrieTask.HashedDelta(realWrites, []));
        task.Finish();

        Hash256 streamedRoot = await task.GetRootAsync();
        Assert.That(streamedRoot, Is.EqualTo(baselineRoot), "prefetch must not alter the root vs writing the same keys directly");
    }
}
