// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Metric;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Monitoring.Config;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Rust.Interop;
using Nethermind.Trie;
using NUnit.Framework;
using static Nethermind.State.Flat.Rust.Test.ForkTestChain;
using BlockPlan = Nethermind.State.Flat.Rust.Test.RustFlatDbManagerDifferentialTests.BlockPlan;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>
/// The forked <see cref="Fork.ReadOnlySnapshotBundle"/>, a handle on a native bundle: what it answers without a call,
/// its unchecked loads, its leases and disposal, the labels of its reads, and the one native scope a gathered
/// <see cref="Fork.SnapshotBundle"/> shares with its read-only bundle.
/// </summary>
/// <remarks>Not parallel: it counts the library's calls and records a metric, both process-wide.</remarks>
[NonParallelizable]
public class ForkReadOnlySnapshotBundleTests
{
    [Test]
    public async Task Reads_the_native_bundle_and_answers_what_it_holds_without_a_call([Values] bool clearInMemory)
    {
        NativeTestSnapshots.Require();
        using TempPath path = TempPath.GetTempDirectory();
        await using FlatManagerContainer container = new(FlatManagerContainer.Config(FlatDbBackend.Rust), path.Path, new TestFinality());
        StateId[] blocks = Build(container, clearInMemory);
        Fork.IFlatDbManager manager = container.Resolve<Fork.IFlatDbManager>();

        Fork.ReadOnlySnapshotBundle bundle = manager.GatherReadOnlySnapshotBundle(blocks[2]);
        ulong[] before = Calls();
        (bool foundState, bool foundStorage) = (bundle.TryFindStateNodes(InMemoryPath, InMemoryHash, out _), bundle.TryFindStorageNodes(PersistedOwner, PersistedPath, PersistedHash, out _));
        int selfDestructIdx = bundle.DetermineSelfDestructSnapshotIdx(Persisted);
        ulong[] after = Calls();
        // Only the bundle and scope exports: the manager's pump calls the library on its own.
        ulong[] calls = [.. Enumerable.Range((int)SeamCounter.RobGather, SeamCounter.SnapshotRelease - SeamCounter.RobGather + 1).Select(index => after[index] - before[index])];

        bundle.GetSlot(Persisted, 1, selfDestructIdx, out UInt256? slot);
        (string? large, string? persisted, string? shadowed) = (
            bundle.TryLoadStateRlp(InMemoryPath, Keccak.Compute("any"), ReadFlags.None)?.ToHexString(),
            bundle.TryLoadStateRlp(PersistedPath, PersistedHash, ReadFlags.None)?.ToHexString(),
            bundle.TryLoadStateRlp(ShadowedPath, PlaceholderHash, ReadFlags.None)?.ToHexString());
        bool leased = bundle.TryLease();
        bundle.Dispose();
        Account? readWhileLeased = bundle.GetAccount(InMemory);
        bundle.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That((bundle.SnapshotCount, bundle.IsHistorical), Is.EqualTo((1, false)), "blocks 1 and 2 compacted into one snapshot");
            Assert.That((foundState, foundStorage), Is.EqualTo((false, false)), "the native snapshots hold RLP, not nodes");
            Assert.That(selfDestructIdx, Is.EqualTo(clearInMemory ? 0 : -1));
            Assert.That(calls.Sum(static count => (long)count), Is.EqualTo(clearInMemory ? 1 : 0), "only a chain with a clear asks for its index");
            Assert.That(slot, clearInMemory ? Is.Null : Is.EqualTo((UInt256)11));
            Assert.That(large, Is.EqualTo(InMemoryRlp.ToHexString()), "a node larger than the stack buffer, whatever the hash asked");
            Assert.That(persisted, Is.EqualTo(PersistedRlp.ToHexString()));
            Assert.That(shadowed, Is.EqualTo(ShadowedRlp.ToHexString()), "a placeholder reads the persisted node");
            Assert.That((leased, readWhileLeased), Is.EqualTo((true, new Account(2, 200))));
            Assert.That(() => bundle.GetAccount(InMemory), Throws.TypeOf<ObjectDisposedException>());
            Assert.That(bundle.TryLease(), Is.False);
        }
    }

    [Test]
    public async Task Gathers_the_empty_state_and_refuses_a_state_not_retained()
    {
        RustNativeLibrary.Require();
        using TempPath path = TempPath.GetTempDirectory();
        await using FlatManagerContainer container = new(FlatManagerContainer.Config(FlatDbBackend.Rust), path.Path, new TestFinality());
        Fork.IFlatDbManager manager = container.Resolve<Fork.IFlatDbManager>();

        using Fork.ReadOnlySnapshotBundle empty = manager.GatherReadOnlySnapshotBundle(StateId.PreGenesis);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((empty.SnapshotCount, empty.GetAccount(Persisted), empty.TryLoadStateRlp(PersistedPath, PersistedHash, ReadFlags.None)), Is.EqualTo((0, (Account?)null, (byte[]?)null)));
            Assert.That(() => manager.GatherReadOnlySnapshotBundle(new StateId(3, Keccak.Compute("unknown"))), Throws.TypeOf<StateNotRetainedException>());
            Assert.That(() => manager.GatherSnapshotBundle(new StateId(3, Keccak.Compute("unknown")), ResourcePool.Usage.ReadOnlyProcessingEnv), Throws.TypeOf<StateNotRetainedException>());
        }
    }

    /// <summary>A gathered scope bundle opens one native scope, whose base view is its read-only bundle, and releases it once.</summary>
    [Test]
    public async Task A_gathered_scope_bundle_shares_one_native_scope_with_its_read_only_bundle()
    {
        NativeTestSnapshots.Require();
        using TempPath path = TempPath.GetTempDirectory();
        await using FlatManagerContainer container = new(FlatManagerContainer.Config(FlatDbBackend.Rust), path.Path, new TestFinality());
        StateId[] blocks = Build(container, clearInMemory: false);
        Fork.IFlatDbManager manager = container.Resolve<Fork.IFlatDbManager>();

        ulong[] before = Calls();
        Fork.SnapshotBundle bundle = manager.GatherSnapshotBundle(blocks[2], ResourcePool.Usage.ReadOnlyProcessingEnv);
        ulong[] opened = Calls();
        bundle.Dispose();
        ulong[] released = Calls();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Delta(before, opened, SeamCounter.ScopeOpen, SeamCounter.ScopeCreate, SeamCounter.RobGather), Is.EqualTo(new ulong[] { 1, 0, 0 }));
            Assert.That(Delta(opened, released, SeamCounter.ScopeRelease, SeamCounter.RobRelease), Is.EqualTo(new ulong[] { 1, 0 }));
        }
    }

    /// <summary>The reads C# served from its read-only bundle are timed under the labels of where they were served from.</summary>
    [Test]
    public async Task Reads_are_timed_under_the_label_of_their_source()
    {
        NativeTestSnapshots.Require();
        using TempPath path = TempPath.GetTempDirectory();
        await using FlatManagerContainer container = new(
            FlatManagerContainer.Config(FlatDbBackend.Rust), path.Path, new TestFinality(), new() { MetricsConfig = new MetricsConfig { EnableDetailedMetric = true } });
        StateId[] blocks = Build(container, clearInMemory: false);
        IMetricObserver previous = Metrics.ReadOnlySnapshotBundleTimes;
        ConcurrentQueue<string> labels = new();
        Metrics.ReadOnlySnapshotBundleTimes = new RecordingObserver(labels);
        try
        {
            using Fork.ReadOnlySnapshotBundle bundle = container.Resolve<Fork.IFlatDbManager>().GatherReadOnlySnapshotBundle(blocks[2]);
            foreach (Address address in new[] { InMemory, Persisted, Absent })
            {
                bundle.GetAccount(address);
                bundle.GetSlot(address, 1, -1, out _);
            }

            foreach (TreePath nodePath in new[] { InMemoryPath, PersistedPath, ShadowedPath })
            {
                bundle.TryLoadStateRlp(nodePath, Keccak.Zero, ReadFlags.None);
            }

            bundle.TryLoadStorageRlp(PersistedOwner, PersistedPath, Keccak.Zero, ReadFlags.None);
        }
        finally
        {
            Metrics.ReadOnlySnapshotBundleTimes = previous;
        }

        Assert.That(labels, Is.EqualTo(new[]
        {
            "account_snapshot", "storage_snapshot",
            "account_persistence", "storage_persistence",
            "account_persistence_null", "storage_persistence_null",
            "state_node_snapshot", "state_rlp", "state_rlp", "storage_rlp",
        }));
    }

    /// <summary>
    /// A bundle over the chain with blocks 1 and 2 converted into the persisted-snapshot tier, and merged there, and
    /// block 3 in memory: it numbers the tier's snapshots first, asks for a self-destruct index only when a snapshot
    /// clears a storage, reads the tier unchecked under the labels C# gives its persisted-snapshot reads, counts none
    /// of those as a trie node loaded from the database or a cache, and keeps reading the tier once a flush prunes it,
    /// while a state pruned from the tier is no longer retained.
    /// </summary>
    [Test]
    public async Task Reads_the_persisted_snapshot_tier_below_its_in_memory_snapshots([Values] bool clearInTier)
    {
        NativeTestSnapshots.Require();
        (List<string> expectedLabels, _) = await ReadThroughTheTier(FlatDbBackend.CSharp, clearInTier);
        (List<string> labels, TierReads? rustReads) = await ReadThroughTheTier(FlatDbBackend.Rust, clearInTier);
        TierReads reads = rustReads!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(labels, Is.EqualTo(expectedLabels), "the labels of the reads, as C# labels them");
            Assert.That(labels, Does.Contain("account_persisted_snapshot").And.Contain("state_rlp_persisted_snapshot"));
            Assert.That((reads.Info.PersistedCount, reads.Info.SnapshotCount, reads.SnapshotCount), Is.EqualTo((1u, 2u, 2)), "blocks 1 and 2 merged in the tier, 3 in memory");
            Assert.That((reads.SelfDestructIdx, reads.SelfDestructCalls), Is.EqualTo(clearInTier ? (0, 1UL) : (-1, 0UL)), "block 1 is the tier's first snapshot");
            Assert.That(reads.Slot, clearInTier ? Is.Null : Is.EqualTo((UInt256)11), "the clear in the tier hides the persisted slot");
            Assert.That(reads.TierNode, Is.EqualTo(TierRlp.ToHexString()), "a node of the tier, whatever the hash asked");
            Assert.That(reads.Shadowed, Is.EqualTo(ShadowedRlp.ToHexString()), "a placeholder of the tier reads the persisted node");
            Assert.That(reads.LoadedFromDbOrCache, Is.Zero, "tier hits are loaded from neither the database nor a cache");
            Assert.That(reads.ReadAfterThePrune, Is.EqualTo(new Account(2, 200)), "the bundle keeps the tier's pruned snapshots");
            Assert.That(reads.PrunedRetained, Is.False);
            Assert.That(reads.Leased, Is.True);
        }
    }

    /// <summary>A trie node of the tier: as large as a real one at most, which the blob files of the tier read back in one read.</summary>
    private static readonly byte[] TierRlp = Bytes.FromHexString("0xc3820202");

    /// <summary>What <see cref="Reads_the_persisted_snapshot_tier_below_its_in_memory_snapshots"/> reads on the Rust backend.</summary>
    private sealed record TierReads(
        FfiRobInfo Info,
        int SnapshotCount,
        int SelfDestructIdx,
        ulong SelfDestructCalls,
        UInt256? Slot,
        string? TierNode,
        string? Shadowed,
        long LoadedFromDbOrCache,
        bool Leased,
        Account? ReadAfterThePrune,
        bool PrunedRetained);

    /// <returns>The labels the reads of a bundle at block 3 were timed under, and on the Rust backend what it read.</returns>
    private static async Task<(List<string> Labels, TierReads? Reads)> ReadThroughTheTier(FlatDbBackend backend, bool clearInTier)
    {
        using TempPath path = TempPath.GetTempDirectory();
        FlatDbConfig config = FlatManagerContainer.Config(backend, FlatLayout.Flat, carryForward: false, maxInMemorySnapshotBytes: 0, longFinality: true);
        config.MaxInMemoryBaseSnapshotCount = 1;
        await using FlatManagerContainer container = new(config, path.Path, new TestFinality(), new() { MetricsConfig = new MetricsConfig { EnableDetailedMetric = true } });
        StateId[] blocks = [.. Enumerable.Range(0, 4).Select(static number => new StateId((ulong)number, Keccak.Compute($"tier chain {number}")))];
        container.Arm.Add(new BlockPlan(
            [(Persisted, new Account(1, 100))],
            [(Persisted, 1, 11)],
            [],
            [(PersistedPath, PersistedHash, PersistedRlp), (ShadowedPath, ShadowedHash, ShadowedRlp)],
            [(PersistedOwner, PersistedPath, PersistedHash, PersistedRlp)]), StateId.PreGenesis, blocks[0]);
        container.Arm.FlushCache(CancellationToken.None);
        container.Arm.Add(new BlockPlan(
            [(InMemory, new Account(2, 200))],
            [(InMemory, 1, 22)],
            clearInTier ? [(Persisted, false)] : [],
            [(InMemoryPath, InMemoryHash, TierRlp), (ShadowedPath, PlaceholderHash, null)],
            []), blocks[0], blocks[1]);
        container.Arm.Add(BlockPlan.Empty, blocks[1], blocks[2]);
        // An odd block, which no compacted snapshot ends at, so the bundle's snapshots are the bases of the chain.
        StateId head = blocks[3];
        container.Arm.Add(BlockPlan.Empty, blocks[2], head);
        container.PersistUpTo(head);

        IMetricObserver previous = Metrics.ReadOnlySnapshotBundleTimes;
        ConcurrentQueue<string> labels = new();
        Metrics.ReadOnlySnapshotBundleTimes = new RecordingObserver(labels);
        try
        {
            if (backend == FlatDbBackend.CSharp)
            {
                using IBundleView view = container.Arm.GatherReadOnly(head, ReaderFlags.None);
                ReadAll(view);
                return ([.. labels], null);
            }

            // The persisted-snapshot compactor merges the two converted blocks on its own thread.
            Assert.That(() => NativeTier.GatherInfo(container.Resolve<Fork.NativeFlatManager>(), head).PersistedCount, Is.EqualTo(1u).After(10_000, 10), "blocks 1 and 2 merged in the tier");
            FfiRobInfo info = NativeTier.GatherInfo(container.Resolve<Fork.NativeFlatManager>(), head);
            Fork.ReadOnlySnapshotBundle bundle = container.Resolve<Fork.IFlatDbManager>().GatherReadOnlySnapshotBundle(head);
            ulong[] before = Calls();
            int selfDestructIdx = bundle.DetermineSelfDestructSnapshotIdx(Persisted);
            ulong selfDestructCalls = Calls()[(int)SeamCounter.RobSelfDestructIdx] - before[(int)SeamCounter.RobSelfDestructIdx];
            long loaded = Nethermind.Trie.Pruning.Metrics.LoadedFromDbNodesCount + Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount;
            string? tierNode = bundle.TryLoadStateRlp(InMemoryPath, Keccak.Compute("any"), ReadFlags.None)?.ToHexString();
            long loadedFromDbOrCache = Nethermind.Trie.Pruning.Metrics.LoadedFromDbNodesCount + Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount - loaded;
            string? shadowed = bundle.TryLoadStateRlp(ShadowedPath, PlaceholderHash, ReadFlags.None)?.ToHexString();
            bundle.GetSlot(Persisted, 1, selfDestructIdx, out UInt256? slot);
            labels.Clear();
            using (IBundleView view = container.Arm.GatherReadOnly(head, ReaderFlags.None))
            {
                ReadAll(view);
            }

            List<string> readLabels = [.. labels];
            bool leased = bundle.TryLease();
            bundle.Dispose();
            container.Arm.FlushCache(CancellationToken.None);
            Account? readAfterThePrune = bundle.GetAccount(InMemory);
            bundle.Dispose();
            bool prunedRetained = container.Arm.HasStateForBlock(blocks[1]);
            Assert.That(() => container.Arm.GatherReadOnly(blocks[1], ReaderFlags.None), Throws.TypeOf<StateNotRetainedException>(), "block 1 is pruned from the tier");
            return (readLabels, new TierReads(info, bundle.SnapshotCount, selfDestructIdx, selfDestructCalls, slot, tierNode, shadowed, loadedFromDbOrCache, leased, readAfterThePrune, prunedRetained));
        }
        finally
        {
            Metrics.ReadOnlySnapshotBundleTimes = previous;
        }

        static void ReadAll(IBundleView view)
        {
            foreach (Address address in new[] { InMemory, Persisted, Absent })
            {
                view.GetAccount(address);
                view.GetSlot(address, 1, -1);
            }

            foreach (TreePath nodePath in new[] { InMemoryPath, PersistedPath, ShadowedPath })
            {
                view.LoadStateRlp(nodePath);
            }

            view.LoadStorageRlp(PersistedOwner, PersistedPath);
        }
    }

    private static ulong[] Calls() => SeamCounters.Read().Calls;

    private static ulong[] Delta(ulong[] before, ulong[] after, params SeamCounter[] counters) =>
        [.. counters.Select(counter => after[(int)counter] - before[(int)counter])];

    private sealed class RecordingObserver(ConcurrentQueue<string> observed) : IMetricObserver
    {
        public void Observe(double value, IMetricLabels? labels = null) => observed.Enqueue(string.Join(",", labels?.Labels ?? []));
    }
}
