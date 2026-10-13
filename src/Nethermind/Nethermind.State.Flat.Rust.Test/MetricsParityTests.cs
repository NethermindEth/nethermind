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
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Monitoring.Config;
using Nethermind.State.Flat.Persistence;
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
/// Under <see cref="FlatDbBackend.Rust"/> the forked bundles label their reads by where the native bundle served them,
/// as the C# bundles label theirs, but a trie node read finds no node object: a hash-only placeholder in a snapshot
/// counts where the read ends, the database, where C# counts both its find in the snapshot and its load.
/// </para>
/// <para>
/// The live pooled resources (<see cref="Metrics.ActivePooledResource"/>) are compared where no bundle is open: those of
/// the compacted contents and the transient resources in the block stream, which builds the block contents outside
/// any scope, and every kind, block contents included, in a stream of world state scopes. The created and cached ones
/// differ by design: native contents are not pooled.
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

    /// <summary>
    /// What the scopes' bundles were gathered over, which depends on whether the background persistence a block
    /// triggers has persisted it and cleared the bundle cache before the next gather, on either backend.
    /// </summary>
    private static readonly string[] GatherOrderGauges =
    [
        nameof(Metrics.SnapshotBundleSize),
        nameof(Metrics.InMemorySlotFilterBuilds),
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
                Assert.That(csharpBeforeFlush.Active.Keys, Does.Contain($"{ResourcePool.Usage.Compact4} SortedSnapshotContent"), "the stream holds compacted contents");
                Assert.That(Render(BuiltOutsideScopes(rustBeforeFlush.Active)), Is.EqualTo(Render(BuiltOutsideScopes(csharpBeforeFlush.Active))), "the live pooled resources before the flush");
                Assert.That(Render(BuiltOutsideScopes(rust.Active)), Is.EqualTo(Render(BuiltOutsideScopes(csharp.Active))), "the live pooled resources");
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

    /// <summary>A plain slot read through a shared bundle, as <c>FlatStateReader</c> reads, builds no slot filter.</summary>
    [Test]
    public async Task A_plain_slot_read_through_a_shared_bundle_builds_no_slot_filter([Values] FlatDbBackend backend)
    {
        if (backend != FlatDbBackend.CSharp) RustNativeLibrary.Require();
        using TempPath path = TempPath.GetTempDirectory();
        await using FlatManagerContainer container = new(FlatManagerContainer.Config(backend), path.Path, new TestFinality());
        for (ulong number = 0; number <= 2; number++) container.AddBlock(number);
        long builds = Metrics.InMemorySlotFilterBuilds;

        using (IBundleView bundle = container.Arm.GatherReadOnly(Block(2), ReaderFlags.None))
        {
            bundle.GetSlot(BlockAddress, 1, bundle.DetermineSelfDestructSnapshotIdx(BlockAddress));
        }

        Publish(container, backend);
        Assert.That(Metrics.InMemorySlotFilterBuilds - builds, Is.Zero);
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

        IBundleView bundle = container.Arm.GatherScope(Block(2), ResourcePool.Usage.ReadOnlyProcessingEnv, filterInMemorySlotReads: true);
        bundle.GetSlot(BlockAddress, 1, bundle.DetermineSelfDestructSnapshotIdx(BlockAddress));
        await (backend == FlatDbBackend.Rust ? container.Resolve<Fork.RustFlatDbManager>() : (IAsyncDisposable)container.Resolve<IFlatDbManager>()).DisposeAsync();
        if (backend == FlatDbBackend.Rust) container.Resolve<Fork.NativeFlatManager>().Dispose();
        long heldAfterTheManager = Metrics.InMemorySlotFilterMemory - memory;
        bundle.Dispose();

        Assert.That(heldAfterTheManager, Is.Positive, "the bundle holds its filter");
        Assert.That(Metrics.InMemorySlotFilterMemory, Is.EqualTo(memory));
    }

    /// <summary>
    /// One seeded stream of blocks through the world state scopes of each backend: block processing, sometimes with a
    /// second commit of nothing, a read-only scope that writes and commits, a slot-filtered query scope and every few
    /// blocks an overridable scope committing two blocks, with persistence after each block and a flush at the end.
    /// Whenever no scope is open, the flat counters and gauges and the live pooled resources must have moved alike.
    /// </summary>
    [Test]
    public async Task Fork_scopes_move_the_flat_metrics_and_live_pooled_resources_the_original_scopes_move()
    {
        RustNativeLibrary.Require();
        List<string> csharp = await RunScopes(FlatDbBackend.CSharp);
        List<string> rust = await RunScopes(FlatDbBackend.Rust);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(csharp, Has.Some.Contains($"{ResourcePool.Usage.MainBlockProcessing} SnapshotContent="), "blocks hold their contents");
            Assert.That(csharp, Has.Some.Contains($"{ResourcePool.Usage.Compact4} SortedSnapshotContent="), "blocks are compacted");
            Assert.That(rust, Is.EqualTo(csharp));
        }
    }

    /// <summary>
    /// The reads of the forked bundles: labelled as the C# ones, but a hash-only placeholder found in a snapshot counts
    /// once, where the read ends, so the node hits of the snapshots are fewer by the placeholders found.
    /// </summary>
    private static void AssertNativeBundleReads(Observed csharp, Observed rust)
    {
        string[] nodeHits = ["state_node_snapshot", "storage_node_snapshot"];
        long Seam(SeamCounter counter) => rust.Seam[(int)counter];
        long Times(Observed observed, string label) => observed.Histograms.GetValueOrDefault((ReadTimes, label)).Count;
        string[] OtherReads(Observed observed) => [.. Only(observed.Histograms, ReadTimes).Where(read => !nodeHits.Any(hit => read.StartsWith($"{hit}:", StringComparison.Ordinal)))];

        Assert.That(OtherReads(rust), Is.EqualTo(OtherReads(csharp)), "every read but the node hits, by label");
        Assert.That(nodeHits.Select(hit => Times(rust, hit)), Is.All.Positive, "the stream hits nodes in snapshots");
        Assert.That(nodeHits.Select(hit => Times(csharp, hit) - Times(rust, hit)), Is.All.GreaterThanOrEqualTo(0), "C# also counts the placeholders it finds");
        Assert.That(rust.LoadedFromDb, Is.EqualTo(csharp.LoadedFromDb), "trie nodes loaded from the database");
        Assert.That(
            new[] { rust.LoadedFromCache, csharp.LoadedFromCache },
            Is.EqualTo(new[] { nodeHits.Sum(hit => Times(rust, hit)), nodeHits.Sum(hit => Times(csharp, hit)) }),
            "trie nodes loaded from cache are the node hits of the snapshots");
        Assert.That(
            Seam(SeamCounter.RobLoadStateRlp) + Seam(SeamCounter.RobLoadStorageRlp),
            Is.EqualTo(Times(rust, "state_rlp") + Times(rust, "storage_rlp") + nodeHits.Sum(hit => Times(rust, hit))),
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

    /// <returns>The flat counters and gauges and the live pooled resources the stream moved, after each block and after the flush.</returns>
    private static async Task<List<string>> RunScopes(FlatDbBackend backend)
    {
        await using WorldStateNode node = new(backend);
        IWorldStateManager manager = node.WorldStateManager;
        IPersistenceManager persistenceManager = node.Resolve<IPersistenceManager>();
        ScopeWorkload workload = new(new Random(20261016));
        (Dictionary<string, long> scalars, Dictionary<string, long> active) before = (Observed.ReadScalars(), Observed.ReadLivePooled());
        List<string> moved = [];

        BlockHeader? head = null;
        for (ulong number = 0; number < BlockCount; number++)
        {
            head = workload.Block(manager.GlobalWorldState, head, number);
            workload.ReadOnly(manager.CreateResettableWorldState(), head);
            workload.Query(manager.CreateReadOnlyQueryWorldState(), head);
            if (number % 5 == 4)
            {
                using IOverridableWorldScope overridable = manager.CreateOverridableWorldScope();
                workload.Overridable(overridable.WorldState, head);
            }

            FlatManagerContainer.PersistUpTo(persistenceManager, new StateId(head));
            // Drops nothing, as every block extends the head, but clears the bundle caches, whose leases on persisted
            // snapshots the background persistence of either backend releases whenever it gets to it.
            manager.DropStateNotReachableFrom(head);
            moved.Add($"after block {number}: {Moved()}");
        }

        manager.FlushCache(CancellationToken.None);
        moved.Add($"after the flush: {Moved()}");
        return moved;

        string Moved()
        {
            if (backend == FlatDbBackend.Rust)
            {
                node.Resolve<RustPersistence>().PublishMetrics();
                node.Resolve<Fork.NativeFlatManager>().PublishMetrics();
            }

            Dictionary<string, long> scalars = Observed.Since(Observed.ReadScalars(), before.scalars);
            foreach (string gauge in GatherOrderGauges) scalars.Remove(gauge);
            return string.Join(", ", [.. NonZeroValues(scalars), .. NonZeroValues(Observed.Since(Observed.ReadLivePooled(), before.active))]);
        }
    }

    /// <summary>Publishes what the native store and manager of a Rust backend gathered.</summary>
    private static void Publish(FlatManagerContainer container, FlatDbBackend backend)
    {
        if (backend != FlatDbBackend.CSharp) container.Resolve<RustPersistence>().PublishMetrics();
        if (backend == FlatDbBackend.Rust) container.Resolve<Fork.NativeFlatManager>().PublishMetrics();
    }

    private static string[] Render(Dictionary<string, long> scalars) =>
        [.. scalars.Select(static scalar => $"{scalar.Key}={scalar.Value}").Order(StringComparer.Ordinal)];

    private static string[] NonZero(Dictionary<string, long> scalars) =>
        [.. scalars.Where(static scalar => scalar.Value != 0).Select(static scalar => scalar.Key).Order(StringComparer.Ordinal)];

    private static string[] NonZeroValues(Dictionary<string, long> values) =>
        Render(values.Where(static value => value.Value != 0).ToDictionary());

    /// <summary>The live pooled resources but the block contents, which the block stream builds outside any scope: as C# objects on the C# backend, as native snapshots of records, not counted, on the Rust one.</summary>
    private static Dictionary<string, long> BuiltOutsideScopes(Dictionary<string, long> active) =>
        active.Where(static resource => !resource.Key.EndsWith(" SnapshotContent", StringComparison.Ordinal)).ToDictionary();

    /// <summary>The observations of every histogram but <paramref name="metric"/>, as <c>metric label: count sum</c>, the sum left out for times.</summary>
    private static string[] Without(Dictionary<(string Metric, string Label), (int Count, double Sum)> histograms, string metric) =>
        [.. histograms.Where(entry => entry.Key.Metric != metric)
            .Select(static entry => $"{entry.Key.Metric} {entry.Key.Label}: {entry.Value.Count}{(TimeHistograms.Contains(entry.Key.Metric) ? "" : $" {entry.Value.Sum}")}")
            .Order(StringComparer.Ordinal)];

    /// <summary>The observations of <paramref name="metric"/> by label, as <c>label: count</c>; times differ from run to run.</summary>
    private static string[] Only(Dictionary<(string Metric, string Label), (int Count, double Sum)> histograms, string metric) =>
        [.. histograms.Where(entry => entry.Key.Metric == metric).Select(static entry => $"{entry.Key.Label}: {entry.Value.Count}").Order(StringComparer.Ordinal)];

    /// <summary>What a run moved: the scalar <see cref="Metrics"/>, the live pooled resources, the observations of each histogram by label, and the related counters.</summary>
    private sealed record Observed(
        Dictionary<string, long> Scalars,
        Dictionary<string, long> Active,
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
                ReadScalars(),
                ReadLivePooled(),
                observers.Summarize(),
                container.Db.GatherMetric().TotalReads,
                TriePruningMetrics.LoadedFromDbNodesCount,
                TriePruningMetrics.LoadedFromCacheNodesCount,
                [.. calls.Select(static count => (long)count)]);
        }

        /// <summary>The scalar <see cref="Metrics"/> by name.</summary>
        public static Dictionary<string, long> ReadScalars() =>
            ScalarProperties.ToDictionary(static property => property.Name, static property => (long)property.GetValue(null)!);

        /// <summary><see cref="Metrics.ActivePooledResource"/>, as <c>category resource type</c>.</summary>
        public static Dictionary<string, long> ReadLivePooled() =>
            Metrics.ActivePooledResource.ToDictionary(static resource => $"{resource.Key.Category} {resource.Key.ResourceType}", static resource => resource.Value);

        /// <summary>The change of each of <paramref name="values"/> since <paramref name="before"/>, but the value of the <see cref="SetGauges"/>.</summary>
        public static Dictionary<string, long> Since(Dictionary<string, long> values, Dictionary<string, long> before) =>
            values.ToDictionary(static value => value.Key, value => SetGauges.Contains(value.Key) ? value.Value : value.Value - before.GetValueOrDefault(value.Key));

        /// <summary>The change since <paramref name="before"/>, but the value of the <see cref="SetGauges"/>; the histograms were cleared at it.</summary>
        public Observed Since(Observed before) => new(
            Since(Scalars, before.Scalars),
            Since(Active, before.Active),
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
                BundleReads.Read(container.Arm, head, _addresses, _slots, _paths, owners);
            }

            StateId persisted = container.PersistenceManager.GetCurrentPersistedStateId();
            Plan(owners).AddTo(container, _canonical[^2], head);
            BlockPlan.Empty.AddTo(container, _canonical[(int)persisted.BlockNumber - 1], new StateId(persisted.BlockNumber, Keccak.Compute("rejected")));
            beforeFlush();
            container.Arm.FlushCache(CancellationToken.None);
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

    /// <summary>The scopes of <see cref="RunScopes"/>: random account and slot writes with storage clears, reads and hints, over a few accounts.</summary>
    private sealed class ScopeWorkload(Random random)
    {
        private readonly Address[] _addresses = [.. Enumerable.Range(1, 6).Select(static index => new Address($"0xbbbbbbbb{index:x32}"))];
        private readonly UInt256[] _slots = [1, 2, 3, 255, UInt256.MaxValue];

        /// <summary>Processes block <paramref name="number"/> on <paramref name="parent"/>, which a second commit of nothing sometimes follows.</summary>
        public BlockHeader Block(IWorldStateScopeProvider provider, BlockHeader? parent, ulong number)
        {
            using IWorldStateScopeProvider.IScope scope = Begin(provider, parent);
            Read(scope);
            scope.HintGet(Pick(_addresses), random.Next(2) == 0 ? null : new Account((ulong)random.Next(4), (UInt256)random.Next(1000)));
            Write(scope);
            Commit(scope, number);
            if (random.Next(3) == 0) Commit(scope, number);
            Read(scope);
            return Build.A.BlockHeader.WithNumber(number).WithStateRoot(scope.RootHash).TestObject;
        }

        /// <summary>Reads, writes and commits the block after <paramref name="head"/>, which a read-only scope keeps to itself.</summary>
        public void ReadOnly(IWorldStateScopeProvider provider, BlockHeader head)
        {
            using IWorldStateScopeProvider.IScope scope = Begin(provider, head);
            Read(scope);
            Write(scope);
            Commit(scope, head.Number + 1);
            Read(scope);
        }

        public void Query(IWorldStateScopeProvider provider, BlockHeader head)
        {
            using IWorldStateScopeProvider.IScope scope = Begin(provider, head);
            Read(scope);
        }

        /// <summary>Commits two blocks on <paramref name="head"/>, the second on the first.</summary>
        public void Overridable(IWorldStateScopeProvider provider, BlockHeader head)
        {
            BlockHeader parent = head;
            for (ulong number = head.Number + 1; number <= head.Number + 2; number++)
            {
                using IWorldStateScopeProvider.IScope scope = Begin(provider, parent);
                Write(scope);
                Commit(scope, number);
                Read(scope);
                parent = Build.A.BlockHeader.WithNumber(number).WithStateRoot(scope.RootHash).TestObject;
            }
        }

        private static IWorldStateScopeProvider.IScope Begin(IWorldStateScopeProvider provider, BlockHeader? parent)
        {
            Assert.That(provider.TryBeginScope(parent, new LocalMetrics(), out IWorldStateScopeProvider.IScope? scope), Is.True, $"scope at {parent?.Number}");
            return scope!;
        }

        private static void Commit(IWorldStateScopeProvider.IScope scope, ulong number)
        {
            scope.UpdateRootHash();
            scope.Commit(number);
        }

        private void Write(IWorldStateScopeProvider.IScope scope)
        {
            Address[] written = [.. _addresses.OrderBy(_ => random.Next()).Take(random.Next(1, 4))];
            using IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(written.Length);
            foreach (Address address in written)
            {
                (UInt256 Slot, UInt256 Value)[] slots = [.. _slots.Where(_ => random.Next(3) == 0).Select(slot => (slot, (UInt256)random.Next(3)))];
                WorldStateNode.Write(address, new Account((ulong)random.Next(4), (UInt256)random.Next(1, 1000)), random.Next(6) == 0, slots)(batch);
            }
        }

        private void Read(IWorldStateScopeProvider.IScope scope)
        {
            Address address = Pick(_addresses);
            scope.Get(address);
            scope.CreateStorageTree(address).Get(Pick(_slots), out _);
        }

        private T Pick<T>(T[] items) => items[random.Next(items.Length)];
    }
}
