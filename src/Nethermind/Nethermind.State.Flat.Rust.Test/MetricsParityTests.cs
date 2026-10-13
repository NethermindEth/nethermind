// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Metric;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Monitoring.Config;
using Nethermind.State.Flat.Rust.Interop;
using Nethermind.Trie;
using NUnit.Framework;
using BlockPlan = Nethermind.State.Flat.Rust.Test.RustFlatDbManagerDifferentialTests.BlockPlan;
using BundleReads = Nethermind.State.Flat.Rust.Test.RustFlatDbManagerDifferentialTests.BundleReads;
using TriePruningMetrics = Nethermind.Trie.Pruning.Metrics;
using static Nethermind.State.Flat.Rust.Test.FlatManagerContainer;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>
/// One seeded stream of blocks, finality, persistence, reads through the bundles, a duplicate and a rejected snapshot
/// and a flush must move the same <see cref="Metrics"/> on a Rust backend as on the C# one: the same counters and gauges,
/// by the same amounts, the same histograms as often, and the same database reads.
/// </summary>
/// <remarks>
/// <para>
/// The metrics are statics, so the fixture runs alone, with detailed metrics on and every histogram recorded.
/// </para>
/// <para>
/// Under <see cref="FlatDbBackend.Rust"/> the C# bundle wraps the native one, which serves the in-memory snapshots, so
/// the C# bundle labels every read <c>*_persistence</c>, <c>state_rlp</c> or <c>storage_rlp</c> in
/// <see cref="Metrics.ReadOnlySnapshotBundleTimes"/>, observes the slots a self-destruct cut off, which C# answers
/// unobserved, and counts every trie node in <see cref="TriePruningMetrics.LoadedFromDbNodesCount"/>, none in
/// <see cref="TriePruningMetrics.LoadedFromCacheNodesCount"/>; the native <see cref="SeamCounters"/> tell the snapshot
/// hits apart, so the C# split is checked against them. The <see cref="Metrics.ActivePooledResource"/> family counts
/// pooled C# objects, which the native snapshots do not use, so it is not compared. A plain slot read through a shared
/// bundle builds the native slot filter, which C# builds for filtered reads only.
/// </para>
/// </remarks>
[NonParallelizable]
public class MetricsParityTests
{
    private const int BlockCount = 40;
    private const ulong FinalityLag = 8;
    private const string ReadTimes = nameof(Metrics.ReadOnlySnapshotBundleTimes);

    /// <summary>Gauges every update sets rather than adjusts, compared by value rather than by change.</summary>
    private static readonly HashSet<string> SetGauges =
    [
        nameof(Metrics.SnapshotBundleSize),
        nameof(Metrics.SnapshotBundlePersistedSnapshotSize),
        nameof(Metrics.SnapshotBundlePersistedSnapshotMemory),
        nameof(Metrics.CarryForwardAccountCount),
        nameof(Metrics.CarryForwardSlotCount),
    ];

    /// <summary>Histograms of times, which differ from run to run.</summary>
    private static readonly HashSet<string> TimeHistograms =
    [
        nameof(Metrics.FlatPersistenceTime),
        nameof(Metrics.CompactTime),
        nameof(Metrics.CompactorStallTime),
        nameof(Metrics.InMemorySlotFilterBuildTime),
        nameof(Metrics.ReadOnlySnapshotBundleTimes),
    ];

    /// <summary>The gauges of the in-memory snapshots, compared before the flush, which persists them all.</summary>
    private static readonly string[] SnapshotGauges =
    [
        nameof(Metrics.SnapshotCount),
        nameof(Metrics.CompactedSnapshotCount),
        nameof(Metrics.SnapshotMemory),
        nameof(Metrics.CompactedSnapshotMemory),
        nameof(Metrics.TotalSnapshotMemory),
        nameof(Metrics.SnapshotBundleSize),
    ];

    [Test]
    public async Task Rust_backends_move_the_flat_metrics_the_csharp_backend_moves([Values(FlatDbBackend.RustPersistence, FlatDbBackend.Rust)] FlatDbBackend backend)
    {
        RustNativeLibrary.Require();
        bool detailedMetrics = Nethermind.Db.Metrics.DetailedMetricsEnabled;
        Nethermind.Db.Metrics.DetailedMetricsEnabled = true;
        using RecordingObservers observers = new();
        try
        {
            (Observed csharpBeforeFlush, Observed csharp) = await Run(FlatDbBackend.CSharp, observers);
            (Observed rustBeforeFlush, Observed rust) = await Run(backend, observers);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(csharpBeforeFlush.Scalars[nameof(Metrics.CompactedSnapshotCount)], Is.GreaterThan(0), "the stream keeps compacted snapshots in memory");
                Assert.That(csharp.Histograms.Keys, Does.Contain((nameof(Metrics.FlatPersistenceTime), "")), "the stream persists");
                Assert.That(csharp.Histograms.Keys, Does.Contain((nameof(Metrics.CompactTime), "")), "the stream compacts");
                Assert.That(csharp.Scalars[nameof(Metrics.InMemorySlotFilterBuilds)], Is.GreaterThan(0), "the stream reads through slot filters");
                Assert.That(csharp.Scalars[nameof(Metrics.CarryForwardSlotHits)], Is.GreaterThan(0), "the stream hits the carry-forward cache");

                Assert.That(SnapshotGauges.Select(gauge => rustBeforeFlush.Scalars[gauge]), Is.EqualTo(SnapshotGauges.Select(gauge => csharpBeforeFlush.Scalars[gauge])),
                    $"{string.Join(", ", SnapshotGauges)} before the flush");

                Assert.That(NonZero(rust.Scalars), Is.EqualTo(NonZero(csharp.Scalars)), "the counters and gauges that moved");
                Assert.That(Render(rust.Scalars), Is.EqualTo(Render(csharp.Scalars)), "how far each moved");
                Assert.That(rust.TotalReads, Is.EqualTo(csharp.TotalReads), "the reads of the flat database");
                Assert.That(Without(rust.Histograms, ReadTimes), Is.EqualTo(Without(csharp.Histograms, ReadTimes)), "the histograms: observations, and sums but of times");

                if (backend == FlatDbBackend.RustPersistence)
                {
                    Assert.That(Only(rust.Histograms, ReadTimes), Is.EqualTo(Only(csharp.Histograms, ReadTimes)), "the read times by label");
                    Assert.That((rust.LoadedFromDb, rust.LoadedFromCache), Is.EqualTo((csharp.LoadedFromDb, csharp.LoadedFromCache)));
                }
                else
                {
                    AssertNativeBundleReads(csharp, rust);
                }
            }
        }
        finally
        {
            Nethermind.Db.Metrics.DetailedMetricsEnabled = detailedMetrics;
        }
    }

    /// <summary>
    /// The documented difference of <see cref="FlatDbBackend.Rust"/>: a plain slot read through a shared bundle, as
    /// <c>FlatStateReader</c> reads, builds the slot filter, as the native bundle cannot tell it from a filtered read.
    /// </summary>
    /// <returns>The slot filters built.</returns>
    [TestCase(FlatDbBackend.CSharp, ExpectedResult = 0)]
    [TestCase(FlatDbBackend.RustPersistence, ExpectedResult = 0)]
    [TestCase(FlatDbBackend.Rust, ExpectedResult = 1)]
    public async Task<long> A_plain_slot_read_through_a_shared_bundle_builds_the_slot_filter_only_on_rust(FlatDbBackend backend)
    {
        if (backend != FlatDbBackend.CSharp) RustNativeLibrary.Require();
        using TempPath path = TempPath.GetTempDirectory();
        await using FlatManagerContainer container = new(FlatManagerContainer.Config(backend), path.Path, new TestFinality());
        for (ulong number = 0; number <= 2; number++) container.AddBlock(number);
        long builds = Metrics.InMemorySlotFilterBuilds;

        using (ReadOnlySnapshotBundle bundle = container.Manager.GatherReadOnlySnapshotBundle(Block(2)))
        {
            bundle.GetSlot(BlockAddress, 1, bundle.DetermineSelfDestructSnapshotIdx(BlockAddress), out _);
        }

        Publish(container, backend);
        return Metrics.InMemorySlotFilterBuilds - builds;
    }

    /// <summary>A bundle disposed after its manager gives the memory of its slot filter back, as on the C# backend.</summary>
    [Test]
    public async Task A_bundle_disposed_after_its_manager_gives_back_its_slot_filter_memory([Values(FlatDbBackend.CSharp, FlatDbBackend.Rust)] FlatDbBackend backend)
    {
        if (backend != FlatDbBackend.CSharp) RustNativeLibrary.Require();
        using TempPath path = TempPath.GetTempDirectory();
        await using FlatManagerContainer container = new(FlatManagerContainer.Config(backend), path.Path, new TestFinality());
        for (ulong number = 0; number <= 2; number++) container.AddBlock(number);
        long memory = Metrics.InMemorySlotFilterMemory;

        SnapshotBundle bundle = container.Manager.GatherSnapshotBundle(Block(2), ResourcePool.Usage.ReadOnlyProcessingEnv, filterInMemorySlotReads: true);
        bundle.GetSlot(BlockAddress, 1, bundle.DetermineSelfDestructSnapshotIdx(BlockAddress), out _);
        await ((IAsyncDisposable)container.Manager).DisposeAsync();
        if (backend == FlatDbBackend.Rust) container.Resolve<NativeFlatManager>().Dispose();
        long heldAfterTheManager = Metrics.InMemorySlotFilterMemory - memory;
        bundle.Dispose();

        Assert.That(heldAfterTheManager, Is.Positive, "the bundle holds its filter");
        Assert.That(Metrics.InMemorySlotFilterMemory, Is.EqualTo(memory));
    }

    /// <summary>The documented difference of the native bundles, reconciled through the seam counters.</summary>
    private static void AssertNativeBundleReads(Observed csharp, Observed rust)
    {
        long Seam(SeamCounter counter) => rust.Seam[(int)counter];
        long Times(Observed observed, string label) => observed.Histograms.GetValueOrDefault((ReadTimes, label)).Count;

        long stateNodeHits = Seam(SeamCounter.BundleStateNodeSnapshot);
        long storageNodeHits = Seam(SeamCounter.BundleStorageNodeSnapshot);
        long csharpAccounts = Times(csharp, "account_snapshot") + Times(csharp, "account_persistence") + Times(csharp, "account_persistence_null");
        long csharpSlots = Times(csharp, "storage_snapshot") + Times(csharp, "storage_persistence") + Times(csharp, "storage_persistence_null");

        Assert.That(Only(rust.Histograms, ReadTimes).Where(static label => label.Contains("_snapshot:", StringComparison.Ordinal)), Is.Empty,
            "the C# bundle over the native one never hits a snapshot of its own");
        Assert.That(
            new[] { Seam(SeamCounter.BundleAccountSnapshot), Seam(SeamCounter.BundleAccountPersistence), Seam(SeamCounter.BundleAccountPersistenceNull) },
            Is.EqualTo(new[] { Times(csharp, "account_snapshot"), Times(csharp, "account_persistence"), Times(csharp, "account_persistence_null") }),
            "the native account reads split as the C# labels");
        Assert.That(Times(rust, "account_persistence") + Times(rust, "account_persistence_null"), Is.EqualTo(csharpAccounts), "account reads");
        Assert.That(
            new[] { Seam(SeamCounter.BundleStorageSnapshot), Seam(SeamCounter.BundleStoragePersistence), Seam(SeamCounter.BundleStoragePersistenceNull) },
            Is.EqualTo(new[] { Times(csharp, "storage_snapshot"), Times(csharp, "storage_persistence"), Times(csharp, "storage_persistence_null") }),
            "the native slot reads split as the C# labels");
        Assert.That(Times(rust, "storage_persistence") + Times(rust, "storage_persistence_null"), Is.EqualTo(csharpSlots + Seam(SeamCounter.BundleStorageSelfDestructed)),
            "slot reads, with the cut-off ones C# leaves unobserved");
        Assert.That(
            new[] { Seam(SeamCounter.BundleStateNodePersistence), Seam(SeamCounter.BundleStorageNodePersistence) },
            Is.EqualTo(new[] { Times(csharp, "state_rlp"), Times(csharp, "storage_rlp") }),
            "the native trie node reads from the database are the C# ones");
        Assert.That(
            new[] { Times(rust, "state_rlp"), Times(rust, "storage_rlp") },
            Is.EqualTo(new[] { stateNodeHits + Seam(SeamCounter.BundleStateNodePersistence), storageNodeHits + Seam(SeamCounter.BundleStorageNodePersistence) }),
            "every native trie node read is labelled a database read");
        Assert.That(rust.LoadedFromDb - stateNodeHits - storageNodeHits, Is.EqualTo(csharp.LoadedFromDb), "trie nodes loaded from the database, less the native snapshot hits");
        Assert.That((rust.LoadedFromCache, csharp.LoadedFromCache), Is.EqualTo((0L, Times(csharp, "state_node_snapshot") + Times(csharp, "storage_node_snapshot"))));
        Assert.That(stateNodeHits, Is.GreaterThan(0).And.LessThanOrEqualTo(Times(csharp, "state_node_snapshot")), "the C# hits include the placeholders");
        Assert.That(
            Seam(SeamCounter.ReaderLoadStateRlp) + Seam(SeamCounter.ReaderLoadStorageRlp),
            Is.EqualTo(Times(rust, "state_rlp") + Times(rust, "storage_rlp")),
            "one call into the library per trie node read");
    }

    /// <returns>What the run moved before its final flush and in all.</returns>
    private static async Task<(Observed BeforeFlush, Observed End)> Run(FlatDbBackend backend, RecordingObservers observers)
    {
        using TempPath path = TempPath.GetTempDirectory();
        TestFinality finality = new();
        await using FlatManagerContainer container = new(
            FlatManagerContainer.Config(backend, FlatLayout.Flat, carryForward: true), path.Path, finality, new() { MetricsConfig = new MetricsConfig { EnableDetailedMetric = true } });
        observers.Clear();
        Observed before = Observed.Take(container, observers);

        Observed? beforeFlush = null;
        new Workload(new Random(20261015), finality, container).Run(() => beforeFlush = Take());
        return (beforeFlush!, Take());

        Observed Take()
        {
            Publish(container, backend);
            return Observed.Take(container, observers).Since(before);
        }
    }

    /// <summary>Publishes what the native store and manager of a Rust backend gathered.</summary>
    private static void Publish(FlatManagerContainer container, FlatDbBackend backend)
    {
        if (backend != FlatDbBackend.CSharp) container.Resolve<RustPersistence>().PublishMetrics();
        if (backend == FlatDbBackend.Rust) container.Resolve<NativeFlatManager>().PublishMetrics();
    }

    private static string[] Render(Dictionary<string, long> scalars) =>
        [.. scalars.Select(static scalar => $"{scalar.Key}={scalar.Value}").Order(StringComparer.Ordinal)];

    private static string[] NonZero(Dictionary<string, long> scalars) =>
        [.. scalars.Where(static scalar => scalar.Value != 0).Select(static scalar => scalar.Key).Order(StringComparer.Ordinal)];

    /// <summary>The observations of every histogram but <paramref name="metric"/>, as <c>metric label: count sum</c>, the sum left out for times.</summary>
    private static string[] Without(Dictionary<(string Metric, string Label), (int Count, double Sum)> histograms, string metric) =>
        [.. histograms.Where(entry => entry.Key.Metric != metric)
            .Select(static entry => $"{entry.Key.Metric} {entry.Key.Label}: {entry.Value.Count}{(TimeHistograms.Contains(entry.Key.Metric) ? "" : $" {entry.Value.Sum}")}")
            .Order(StringComparer.Ordinal)];

    /// <summary>The observations of <paramref name="metric"/> by label, as <c>label: count</c>; times differ from run to run.</summary>
    private static string[] Only(Dictionary<(string Metric, string Label), (int Count, double Sum)> histograms, string metric) =>
        [.. histograms.Where(entry => entry.Key.Metric == metric).Select(static entry => $"{entry.Key.Label}: {entry.Value.Count}").Order(StringComparer.Ordinal)];

    /// <summary>What a run moved: the scalar <see cref="Metrics"/>, the observations of each histogram by label, and the related counters.</summary>
    private sealed record Observed(
        Dictionary<string, long> Scalars,
        Dictionary<(string Metric, string Label), (int Count, double Sum)> Histograms,
        long TotalReads,
        long LoadedFromDb,
        long LoadedFromCache,
        long[] Seam)
    {
        private static readonly PropertyInfo[] ScalarProperties =
            [.. typeof(Metrics).GetProperties(BindingFlags.Public | BindingFlags.Static).Where(static property => property.PropertyType == typeof(long))];

        public static Observed Take(FlatManagerContainer container, RecordingObservers observers)
        {
            (ulong[] calls, _) = SeamCounters.Read();
            return new Observed(
                ScalarProperties.ToDictionary(static property => property.Name, static property => (long)property.GetValue(null)!),
                observers.Summarize(),
                container.Db.GatherMetric().TotalReads,
                TriePruningMetrics.LoadedFromDbNodesCount,
                TriePruningMetrics.LoadedFromCacheNodesCount,
                [.. calls.Select(static count => (long)count)]);
        }

        /// <summary>The change since <paramref name="before"/>, but the value of the <see cref="SetGauges"/>; the histograms were cleared at it.</summary>
        public Observed Since(Observed before) => new(
            Scalars.ToDictionary(static scalar => scalar.Key, scalar => SetGauges.Contains(scalar.Key) ? scalar.Value : scalar.Value - before.Scalars[scalar.Key]),
            Histograms,
            TotalReads - before.TotalReads,
            LoadedFromDb - before.LoadedFromDb,
            LoadedFromCache - before.LoadedFromCache,
            [.. Seam.Select((count, index) => count - before.Seam[index])]);
    }

    /// <summary>Records every observation of every histogram of <see cref="Metrics"/> while installed.</summary>
    private sealed class RecordingObservers : IDisposable
    {
        private readonly ConcurrentQueue<(string Metric, string Label, double Value)> _observations = new();
        private readonly List<(PropertyInfo Property, object? Previous)> _replaced = [];

        public RecordingObservers()
        {
            foreach (PropertyInfo property in typeof(Metrics).GetProperties(BindingFlags.Public | BindingFlags.Static)
                .Where(static property => property.PropertyType == typeof(IMetricObserver) && property.CanWrite))
            {
                _replaced.Add((property, property.GetValue(null)));
                property.SetValue(null, new Observer(property.Name, _observations));
            }
        }

        public void Clear() => _observations.Clear();

        public Dictionary<(string Metric, string Label), (int Count, double Sum)> Summarize() =>
            _observations
                .GroupBy(static observation => (observation.Metric, observation.Label))
                .ToDictionary(static group => group.Key, static group => (group.Count(), group.Sum(static observation => observation.Value)));

        public void Dispose()
        {
            foreach ((PropertyInfo property, object? previous) in _replaced) property.SetValue(null, previous);
        }

        private sealed class Observer(string metric, ConcurrentQueue<(string Metric, string Label, double Value)> observations) : IMetricObserver
        {
            public void Observe(double value, IMetricLabels? labels = null) =>
                observations.Enqueue((metric, labels is null ? "" : string.Join(",", labels.Labels), value));
        }
    }

    /// <summary>
    /// Blocks with random writes, hash-only placeholders and self-destructs, finality trailing the head, every block
    /// persisted as far as finality allows and read back through the bundles at the head; then a duplicate and a
    /// rejected snapshot and a flush.
    /// </summary>
    private sealed class Workload(Random random, TestFinality finality, FlatManagerContainer container)
    {
        private readonly Address[] _addresses = [.. Enumerable.Range(1, 6).Select(static index => new Address($"0xaaaaaaaa{index:x32}"))];
        private readonly UInt256[] _slots = [0, 1, 2, 255, UInt256.MaxValue];
        private readonly TreePath[] _paths = [.. new[] { "", "1", "12", "123", "1234", "f", "fe", "fed" }.Select(TreePath.FromHexString)];
        private readonly List<StateId> _canonical = [];

        /// <param name="beforeFlush">Runs before the final flush.</param>
        public void Run(Action beforeFlush)
        {
            Hash256[] owners = [.. _addresses.Take(3).Select(static address => address.ToAccountPath.ToCommitment())];
            StateId head = StateId.PreGenesis;
            for (ulong number = 0; number < BlockCount; number++)
            {
                StateId block = Block(number);
                Plan(owners).AddTo(container, head, block);
                _canonical.Add(block);
                head = block;
                if (number >= FinalityLag && number % 2 == 0) finality.Finalize(number - FinalityLag, canonical => _canonical[(int)canonical]);

                container.PersistUpTo(head);
                BundleReads.Read(container.Manager, head, _addresses, _slots, _paths, owners);
            }

            StateId persisted = container.PersistenceManager.GetCurrentPersistedStateId();
            Plan(owners).AddTo(container, _canonical[^2], head);
            BlockPlan.Empty.AddTo(container, _canonical[(int)persisted.BlockNumber - 1], new StateId(persisted.BlockNumber, Keccak.Compute("rejected")));
            beforeFlush();
            container.Manager.FlushCache(CancellationToken.None);
        }

        private BlockPlan Plan(Hash256[] owners) => new(
            [.. Enumerable.Range(0, random.Next(1, 4)).Select(_ => (Pick(_addresses), random.Next(5) == 0 ? null : (Account?)new Account((ulong)random.Next(4), (UInt256)random.Next(1000))))],
            [.. Enumerable.Range(0, random.Next(0, 7)).Select(_ => (Pick(_addresses), Pick(_slots), random.Next(4) == 0 ? (UInt256?)null : (UInt256)random.Next(3)))],
            [.. Enumerable.Range(0, random.Next(5) == 0 ? 1 : 0).Select(_ => (Pick(_addresses), random.Next(2) == 0))],
            [.. Enumerable.Range(0, random.Next(0, 5)).Select(_ => (Pick(_paths), Keccak.Compute($"state {random.Next()}"), Rlp()))],
            [.. Enumerable.Range(0, random.Next(0, 5)).Select(_ => (Pick(owners), Pick(_paths), Keccak.Compute($"storage {random.Next()}"), Rlp()))]);

        /// <summary>Node RLP, or none for a hash-only placeholder.</summary>
        private byte[]? Rlp()
        {
            if (random.Next(6) == 0) return null;
            byte[] rlp = new byte[1 + random.Next(64)];
            random.NextBytes(rlp);
            return rlp;
        }

        private T Pick<T>(T[] items) => items[random.Next(items.Length)];
    }
}
