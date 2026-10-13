// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Metric;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Monitoring.Config;
using Nethermind.State.Flat.Rust.Interop;
using Nethermind.Trie;
using NUnit.Framework;
using static Nethermind.State.Flat.Rust.Test.ForkTestChain;

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

    private static ulong[] Calls() => SeamCounters.Read().Calls;

    private static ulong[] Delta(ulong[] before, ulong[] after, params SeamCounter[] counters) =>
        [.. counters.Select(counter => after[(int)counter] - before[(int)counter])];

    private sealed class RecordingObserver(ConcurrentQueue<string> observed) : IMetricObserver
    {
        public void Observe(double value, IMetricLabels? labels = null) => observed.Enqueue(string.Join(",", labels?.Labels ?? []));
    }
}
