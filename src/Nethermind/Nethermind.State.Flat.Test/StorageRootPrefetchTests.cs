// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// The storage-root prefetch on a large storage trie in persistence: a block changes 1,000 of its 200,000 slots, as a XEN
/// batch-claim block does. Whatever the prefetch and the warm-up did, the roots match a reference trie; with the
/// prefetch, the commit itself reads (almost) nothing, and the prefetch reads about what the commit would have.
/// </summary>
public class StorageRootPrefetchTests
{
    // 200k leaves put the bottom branches at depth 4 with ~3 leaves each, the fan-out of a XEN-sized trie's bottom level.
    private const int TrieSlots = 200_000;
    private const int Changes = 1_000;
    private const int SmallTrieSlots = 5_000;
    private const int SmallChanges = 100;
    private const int MinChanges = 256;

    // Reads the commit may still make with the prefetch on: none are expected, the allowance only keeps the test from
    // depending on a lucky layout.
    private const int CommitLoadAllowance = 4;

    private static readonly Address Contract = TestItem.AddressA;
    private static readonly Address SmallContract = TestItem.AddressB;
    private static Fixture _fixture = null!;

    public enum Kind { Delete, Update, Insert, Mixed, PairedDelete, EmptiedBranch }

    public enum Warmup { None, Partial, Full, InFlight }

    [OneTimeSetUp]
    public void Setup() => _fixture = new Fixture();

    [OneTimeTearDown]
    public void TearDown() => _fixture.Dispose();

    [Test]
    public void Roots_match_the_reference([Values] Kind kind, [Values] Warmup warmup, [Values] bool prefetch)
    {
        (UInt256 Slot, UInt256 Value)[] changes = _fixture.PickChanges(kind);
        BlockResult result = _fixture.Run(new BlockRun(changes, prefetch ? MinChanges : 0, warmup));
        Report($"roots kind={kind} warmup={warmup} prefetch={prefetch}", changes.Length, result);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Root, Is.EqualTo(_fixture.ExpectedRoot(kind, changes)));
            Assert.That(result.Contracts, Is.EqualTo(prefetch ? 1 : 0));
            // A warmer still loading reads alongside the commit, so its reads cannot be told apart from the commit's.
            if (prefetch && warmup != Warmup.InFlight) Assert.That(result.CommitLoads, Is.LessThanOrEqualTo(CommitLoadAllowance));
        }
    }

    [Test]
    public void Prefetch_reads_what_the_commit_would_and_the_commit_reads_nothing([Values] Kind kind)
    {
        (UInt256 Slot, UInt256 Value)[] changes = _fixture.PickChanges(kind);
        BlockResult off = _fixture.Run(new BlockRun(changes, MinChanges: 0));
        BlockResult on = _fixture.Run(new BlockRun(changes, MinChanges));
        Report($"loads kind={kind} prefetch=False", changes.Length, off);
        Report($"loads kind={kind} prefetch=True", changes.Length, on);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(on.Root, Is.EqualTo(off.Root));
            Assert.That(on.CommitLoads, Is.LessThanOrEqualTo(CommitLoadAllowance), "the commit read nodes the prefetch should have loaded");
            Assert.That(on.PrefetchLoads, Is.InRange(off.CommitLoads * 9 / 10, off.CommitLoads * 11 / 10 + 16), "the prefetch should read about what the commit reads without it");
        }
    }

    [Test]
    public void Roots_match_without_a_reader_pool([Values(Kind.Mixed, Kind.PairedDelete, Kind.EmptiedBranch)] Kind kind)
    {
        (UInt256 Slot, UInt256 Value)[] changes = _fixture.PickChanges(kind);
        BlockResult result = _fixture.Run(new BlockRun(changes, MinChanges, Readers: 0));
        Report($"no-pool kind={kind}", changes.Length, result);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Root, Is.EqualTo(_fixture.ExpectedRoot(kind, changes)));
            Assert.That(result.Contracts, Is.EqualTo(1));
            Assert.That(result.CommitLoads, Is.LessThanOrEqualTo(CommitLoadAllowance));
        }
    }

    /// <summary>
    /// Every trie-node read takes ~1 ms, and the shared node cache starts empty, so the whole path of each change is cold.
    /// Prints the time of the contract's storage commit with the prefetch off and on; asserts only the roots and loads.
    /// </summary>
    [Test]
    public void Commit_time_with_a_slow_disk([Values(Kind.Delete, Kind.Update)] Kind kind, [Values(-1, 0, 16, 64)] int readers)
    {
        ThreadPool.GetMinThreads(out int minWorker, out int minIo);
        // As RegisterRpcModules sets it on a node: processor count added on top of the default.
        ThreadPool.SetMinThreads(Math.Max(minWorker, Environment.ProcessorCount + 16), minIo);
        try
        {
            (UInt256 Slot, UInt256 Value)[] changes = _fixture.PickChanges(kind);
            // -1: prefetch off; 0: prefetch on the calling thread's worker budget; else on that many dedicated readers.
            BlockResult result = _fixture.Run(new BlockRun(changes, readers < 0 ? 0 : MinChanges, DelayLoads: true, Readers: Math.Max(readers, 0)));
            Report($"slow-disk kind={kind} readers={readers} processors={Environment.ProcessorCount}", changes.Length, result);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Root, Is.EqualTo(_fixture.ExpectedRoot(kind, changes)));
                if (readers >= 0) Assert.That(result.CommitLoads, Is.LessThanOrEqualTo(CommitLoadAllowance));
            }
        }
        finally
        {
            ThreadPool.SetMinThreads(minWorker, minIo);
        }
    }

    [Test]
    public void Contracts_below_the_threshold_are_not_prefetched([Values(0, MinChanges, 2 * Changes)] int minChanges)
    {
        (UInt256 Slot, UInt256 Value)[] changes = _fixture.PickChanges(Kind.Mixed);
        (UInt256 Slot, UInt256 Value)[] smallChanges = _fixture.PickSmallChanges();
        BlockResult result = _fixture.Run(new BlockRun(changes, minChanges, SmallChanges: smallChanges));
        Report($"threshold min={minChanges}", changes.Length, result);

        bool largePrefetched = minChanges is > 0 and <= Changes;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Root, Is.EqualTo(_fixture.ExpectedRoot(Kind.Mixed, changes)));
            Assert.That(result.SmallRoot, Is.EqualTo(_fixture.ExpectedSmallRoot(smallChanges)));
            Assert.That(result.Contracts, Is.EqualTo(largePrefetched ? 1 : 0));
            if (!largePrefetched) Assert.That(result.PrefetchLoads + result.Rounds + result.PrefetchTicks, Is.Zero);
        }
    }

    [Test]
    public void A_batch_estimated_above_the_threshold_but_writing_less_is_not_prefetched()
    {
        // The estimate counts slots the block changed and then restored, which the commit does not write.
        (UInt256 Slot, UInt256 Value)[] changes = _fixture.PickChanges(Kind.Update)[..(MinChanges - 1)];
        BlockResult result = _fixture.Run(new BlockRun(changes, MinChanges, EstimatedEntries: Changes));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Root, Is.EqualTo(_fixture.ExpectedRoot(null, changes)));
            Assert.That(result.Contracts, Is.Zero);
        }
    }

    /// <summary>
    /// Early apply ran on the idle thread before the block-end batch: with <paramref name="abandon"/> its pass is still
    /// running when the batch starts, so the batch writes every slot into the pre-block tree; otherwise the batch adopts
    /// the early tree and writes only the slots it holds differently, plus the early writes the block reverted.
    /// </summary>
    [Test]
    public void Roots_match_with_early_apply([Values] bool abandon, [Values] bool prefetch)
    {
        (UInt256 Slot, UInt256 Value)[] changes = _fixture.PickChanges(Kind.Mixed);
        // The first 300 early writes match the block's final values, the next 300 differ, the last 400 come only at the
        // block end, and 50 more slots are written early and reverted by the block.
        List<(UInt256 Slot, UInt256 Value)> early = [.. changes[..300]];
        early.AddRange(changes[300..600].Select(c => (c.Slot, (UInt256)777)));
        early.AddRange(_fixture.PickChanges(Kind.Update, seed: 11).Where(c => !changes.Any(x => x.Slot == c.Slot)).Take(50).Select(c => (c.Slot, (UInt256)42)));

        BlockResult result = _fixture.Run(new BlockRun(changes, prefetch ? MinChanges : 0, EarlyWrites: [.. early], AbandonEarlyPass: abandon));
        Report($"early abandon={abandon} prefetch={prefetch} reused={result.EarlyCounts.Reused} restored={result.EarlyCounts.Restored}", changes.Length, result);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Root, Is.EqualTo(_fixture.ExpectedRoot(Kind.Mixed, changes)));
            Assert.That(result.EarlyCounts.Abandoned, Is.EqualTo(abandon ? 1 : 0));
            Assert.That(result.EarlyCounts.Reused, Is.EqualTo(abandon ? 0 : 300));
            Assert.That(result.EarlyCounts.Restored, Is.EqualTo(abandon ? 0 : 50));
            Assert.That(result.Contracts, Is.EqualTo(prefetch ? 1 : 0));
            if (prefetch && !abandon) Assert.That(result.CommitLoads, Is.LessThanOrEqualTo(CommitLoadAllowance));
        }
    }

    private static void Report(string label, int changes, BlockResult r) =>
        TestContext.Out.WriteLine($"XP1 {label} changes={changes} batch_loads={r.BatchLoads} prefetch_loads={r.PrefetchLoads} " +
                                  $"commit_loads={r.CommitLoads} rounds={r.Rounds} contracts={r.Contracts} warmup_loads={r.WarmupLoads} " +
                                  $"commit_ms={r.CommitMs:F1} prefetch_ms={r.PrefetchTicks * 1000.0 / Stopwatch.Frequency:F1}");

    private static UInt256 Slot(int index) => new(Keccak.Compute(BitConverter.GetBytes(index)).Bytes, isBigEndian: true);

    private static UInt256 ValueOf(int index) => (UInt256)(ulong)(index + 1);

    /// <param name="MinChanges">The prefetch threshold; 0 turns it off.</param>
    /// <param name="Readers">Dedicated reader threads of the scope; 0 gives the scope none.</param>
    private sealed record BlockRun(
        (UInt256 Slot, UInt256 Value)[] Changes,
        int MinChanges,
        Warmup Warmup = Warmup.None,
        (UInt256 Slot, UInt256 Value)[]? SmallChanges = null,
        bool DelayLoads = false,
        int Readers = 64,
        int EstimatedEntries = 0,
        (UInt256 Slot, UInt256 Value)[]? EarlyWrites = null,
        bool AbandonEarlyPass = false);

    private readonly record struct BlockResult(
        Hash256 Root,
        Hash256? SmallRoot,
        long WarmupLoads,
        long BatchLoads,
        long PrefetchLoads,
        long CommitLoads,
        int Contracts,
        int Rounds,
        long PrefetchTicks,
        double CommitMs,
        (int Applied, int Reused, int Restored, int Abandoned) EarlyCounts);

    private sealed class Fixture : IDisposable
    {
        private readonly FlatDbConfig _config = new() { ApplyStorageWritesOnIdleThread = false };
        private readonly ResourcePool _resourcePool;
        private readonly RocksDbPersistence _persistence = new(new SnapshotableMemColumnsDb<FlatDbColumns>(), LimboLogs.Instance, NullTrieNodeLog.Instance);
        private readonly StateId _baseState;
        private readonly Dictionary<UInt256, UInt256> _base = new(TrieSlots);
        private readonly Dictionary<UInt256, UInt256> _smallBase = new(SmallTrieSlots);
        private readonly ConcurrentDictionary<Kind, Hash256> _expectedRoots = new();

        public Fixture()
        {
            _resourcePool = new ResourcePool(_config);
            SnapshotBundle bundle = new(
                new ReadOnlySnapshotBundle(new SnapshotPooledList(0), new NoopPersistenceReader(), recordDetailedMetrics: false, PersistedSnapshotStack.Empty()),
                new TrieNodeCache(_config, LimboLogs.Instance), _resourcePool, ResourcePool.Usage.MainBlockProcessing);
            CapturingCommitTarget target = new();
            using FlatWorldStateScope scope = new(StateId.PreGenesis, bundle, new NullCodeDb(), target, _config, new NoopTrieWarmer(), LimboLogs.Instance);

            using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(2))
            {
                batch.Set(Contract, new Account(1));
                batch.Set(SmallContract, new Account(1));
                using (IWorldStateScopeProvider.IStorageWriteBatch storageBatch = batch.CreateStorageWriteBatch(Contract, TrieSlots))
                {
                    for (int i = 0; i < TrieSlots; i++)
                    {
                        storageBatch.Set(Slot(i), ValueOf(i));
                        _base[Slot(i)] = ValueOf(i);
                    }
                }

                using IWorldStateScopeProvider.IStorageWriteBatch smallBatch = batch.CreateStorageWriteBatch(SmallContract, SmallTrieSlots);
                for (int i = 0; i < SmallTrieSlots; i++)
                {
                    UInt256 slot = Slot(TrieSlots + i);
                    smallBatch.Set(slot, ValueOf(i));
                    _smallBase[slot] = ValueOf(i);
                }
            }

            scope.Commit(0);
            _baseState = new StateId(0, scope.RootHash);
            Persist(target.Snapshot ?? throw new InvalidOperationException("The base commit produced no snapshot"));

            // Size the pooled per-block node cache as blocks of this size would on a running node.
            for (int block = 0; block < 3; block++) Run(new BlockRun(PickChanges(Kind.Update, seed: 100 + block), MinChanges: 0, Warmup.Full));
        }

        public (UInt256 Slot, UInt256 Value)[] PickChanges(Kind kind, int seed = 7)
        {
            Random random = new(seed);
            switch (kind)
            {
                case Kind.Delete:
                    return RandomExisting(random).Select(s => (s, UInt256.Zero)).ToArray();
                case Kind.Update:
                    return RandomExisting(random).Select(s => (s, (UInt256)123_456_789)).ToArray();
                case Kind.Insert:
                    return NewSlots(seed).Select(s => (s, (UInt256)987_654_321)).ToArray();
                case Kind.Mixed:
                    UInt256[] existing = RandomExisting(random).ToArray();
                    return existing[..(Changes / 3)].Select(s => (s, UInt256.Zero))
                        .Concat(existing[(Changes / 3)..(2 * Changes / 3)].Select(s => (s, (UInt256)55_555)))
                        .Concat(NewSlots(seed).Take(Changes - 2 * Changes / 3).Select(s => (s, (UInt256)66_666)))
                        .ToArray();
                case Kind.PairedDelete:
                    return PairedDeletes(random);
                default:
                    return EmptiedBranches(random);
            }
        }

        public (UInt256 Slot, UInt256 Value)[] PickSmallChanges() =>
            _smallBase.Keys.OrderBy(s => s).Take(SmallChanges).Select(s => (s, (UInt256)31_337)).ToArray();

        private static IEnumerable<UInt256> RandomExisting(Random random) =>
            Enumerable.Range(0, TrieSlots).OrderBy(_ => random.Next()).Take(Changes).Select(Slot);

        // Past both contracts' slots, so a new slot is new to either.
        private static IEnumerable<UInt256> NewSlots(int seed) =>
            Enumerable.Range(TrieSlots + SmallTrieSlots + seed * Changes, Changes).Select(Slot);

        private static Dictionary<int, List<(UInt256 Slot, ValueHash256 Key)>> ByPrefix(int nibbles)
        {
            Dictionary<int, List<(UInt256, ValueHash256)>> byPrefix = [];
            for (int i = 0; i < TrieSlots; i++)
            {
                UInt256 slot = Slot(i);
                ValueHash256 key = default;
                StorageTree.ComputeKeyWithLookup(slot, ref key);
                int prefix = 0;
                for (int n = 0; n < nibbles; n++) prefix = (prefix << 4) | Nibble(key, n);
                if (!byPrefix.TryGetValue(prefix, out List<(UInt256, ValueHash256)>? list)) byPrefix[prefix] = list = [];
                list.Add((slot, key));
            }

            return byPrefix;
        }

        private static int Nibble(in ValueHash256 key, int index)
        {
            byte b = key.Bytes[index / 2];
            return index % 2 == 0 ? b >> 4 : b & 0xf;
        }

        // Two of the three leaves of a depth-4 branch: each delete alone leaves two, so neither predicts the collapse.
        private static (UInt256, UInt256)[] PairedDeletes(Random random) =>
            ByPrefix(4).Values
                .Where(l => l.Count == 3 && l.Select(e => Nibble(e.Key, 4)).Distinct().Count() == 3)
                .OrderBy(_ => random.Next())
                .Take(Changes / 2)
                .SelectMany(l => l.Take(2).Select(e => (e.Slot, UInt256.Zero)))
                .ToArray();

        // Every leaf under a depth-3 branch except those of one child: the other children are emptied, and the branch
        // collapses into the one child no delete touches.
        private static (UInt256, UInt256)[] EmptiedBranches(Random random)
        {
            List<(UInt256, UInt256)> deletes = [];
            foreach (List<(UInt256 Slot, ValueHash256 Key)> group in ByPrefix(3).Values.OrderBy(_ => random.Next()))
            {
                int survivor = Nibble(group[0].Key, 3);
                deletes.AddRange(group.Where(e => Nibble(e.Key, 3) != survivor).Select(e => (e.Slot, UInt256.Zero)));
                if (deletes.Count >= Changes) break;
            }

            return [.. deletes];
        }

        /// <summary>The root after <paramref name="changes"/>; cached by <paramref name="kind"/> when it is given.</summary>
        public Hash256 ExpectedRoot(Kind? kind, (UInt256 Slot, UInt256 Value)[] changes) =>
            kind is { } k ? _expectedRoots.GetOrAdd(k, _ => Compute(_base, changes)) : Compute(_base, changes);

        public Hash256 ExpectedSmallRoot((UInt256 Slot, UInt256 Value)[] changes) => Compute(_smallBase, changes);

        private static Hash256 Compute(Dictionary<UInt256, UInt256> baseState, (UInt256 Slot, UInt256 Value)[] changes)
        {
            Dictionary<UInt256, UInt256> state = new(baseState);
            foreach ((UInt256 slot, UInt256 value) in changes) state[slot] = value;
            StorageTree expected = new(new RawScopedTrieStore(new TestMemDb()), LimboLogs.Instance);
            foreach ((UInt256 slot, UInt256 value) in state)
            {
                if (!value.IsZero) expected.Set(slot, value.ToMinimalBigEndian());
            }

            expected.UpdateRootHash();
            return expected.RootHash;
        }

        public BlockResult Run(BlockRun run)
        {
            FlatDbConfig config = new()
            {
                ApplyStorageWritesOnIdleThread = run.EarlyWrites is not null,
                StorageRootPrefetchMinChanges = run.MinChanges,
            };
            bool inFlight = run.Warmup == Warmup.InFlight;
            CountingReader reader = new(_persistence.CreateReader(), delayLoads: run.DelayLoads || inFlight);
            TrieWarmer? trieWarmer = inFlight ? new TrieWarmer(LimboLogs.Instance, config) : null;
            WarmReadPool? pool = run.Readers > 0 ? new WarmReadPool(run.Readers) : null;
            using IDisposable? gap = run.EarlyWrites is null ? null : SetMinIdleGap(TimeSpan.Zero);
            SnapshotBundle bundle = new(
                new ReadOnlySnapshotBundle(new SnapshotPooledList(0), reader, recordDetailedMetrics: false, PersistedSnapshotStack.Empty()),
                new TrieNodeCache(config, LimboLogs.Instance), _resourcePool, ResourcePool.Usage.MainBlockProcessing);
            FlatWorldStateScope scope = new(_baseState, bundle, new NullCodeDb(), new CapturingCommitTarget(), config,
                (ITrieWarmer?)trieWarmer ?? new InlineTrieWarmer(), LimboLogs.Instance,
                warmReadPool: pool is null ? null : new Lazy<WarmReadPool>(() => pool));
            using ManualResetEventSlim stalled = new();
            using ManualResetEventSlim resume = new();
            try
            {
                FlatStorageTree storageTree = (FlatStorageTree)scope.CreateStorageTree(Contract);
                if (run.EarlyWrites is not null)
                {
                    if (run.AbandonEarlyPass)
                    {
                        storageTree.OnEarlyPassDrained = () =>
                        {
                            stalled.Set();
                            resume.Wait();
                        };
                    }

                    // The block thread's commit-time hints: each also queues the write for the idle thread.
                    foreach ((UInt256 slot, UInt256 value) in run.EarlyWrites) storageTree.HintSet(slot, value);
                    if (run.AbandonEarlyPass) Assert.That(stalled.Wait(10_000), Is.True, "the early pass did not start");
                    else Assert.That(() => storageTree.EarlyWritesDrained, Is.True.After(10_000, 5));
                }

                int hinted = run.Warmup switch
                {
                    Warmup.Partial => run.Changes.Length / 2,
                    Warmup.Full or Warmup.InFlight => run.Changes.Length,
                    _ => 0,
                };
                foreach ((UInt256 slot, UInt256 value) in run.Changes[..hinted]) storageTree.HintSet(slot, value);

                long beforeBatch = reader.Loads;
                double commitMs;
                using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(2))
                {
                    long start = Stopwatch.GetTimestamp();
                    Write(batch, Contract, run.Changes, run.EstimatedEntries);
                    commitMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    if (run.SmallChanges is not null) Write(batch, SmallContract, run.SmallChanges, 0);

                    if (run.AbandonEarlyPass)
                    {
                        resume.Set();
                        Assert.That(() => scope.EarlyApplyCounts.Applied, Is.GreaterThan(0).After(10_000, 5));
                    }
                }

                long batchLoads = reader.Loads - beforeBatch;
                (int contracts, int prefetchLoads, int rounds, long ticks) = scope.StorageRootPrefetchCounts;
                (int Applied, int Reused, int Restored, int Abandoned) early = scope.EarlyApplyCounts;
                scope.Commit(1);

                return new BlockResult(
                    scope.Get(Contract)!.StorageRoot,
                    run.SmallChanges is null ? null : scope.Get(SmallContract)!.StorageRoot,
                    beforeBatch,
                    batchLoads,
                    prefetchLoads,
                    batchLoads - prefetchLoads,
                    contracts,
                    rounds,
                    ticks,
                    commitMs,
                    early);
            }
            finally
            {
                resume.Set();
                scope.Dispose();
                trieWarmer?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                pool?.Dispose();
            }
        }

        // As PersistentStorageProvider writes them: non-zero values first, deletes after.
        private static void Write(IWorldStateScopeProvider.IWorldStateWriteBatch batch, Address address, (UInt256 Slot, UInt256 Value)[] changes, int estimatedEntries)
        {
            using IWorldStateScopeProvider.IStorageWriteBatch storageBatch = batch.CreateStorageWriteBatch(address, Math.Max(estimatedEntries, changes.Length));
            foreach ((UInt256 slot, UInt256 value) in changes.Where(c => !c.Value.IsZero)) storageBatch.Set(slot, value);
            foreach ((UInt256 slot, UInt256 value) in changes.Where(c => c.Value.IsZero)) storageBatch.Set(slot, value);
        }

        private void Persist(Snapshot snapshot)
        {
            using IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(snapshot.From, snapshot.To, WriteFlags.None);
            foreach (KeyValuePair<HashedKey<Address>, Account?> kv in snapshot.Accounts) batch.SetAccount(kv.Key.Key, kv.Value);
            foreach (KeyValuePair<HashedKey<(Address, UInt256)>, UInt256?> kv in snapshot.Storages) batch.SetStorage(kv.Key.Key.Item1, kv.Key.Key.Item2, kv.Value);
            foreach (KeyValuePair<HashedKey<TreePath>, TrieNode> kv in snapshot.StateNodes) batch.SetStateTrieNode(kv.Key.Key, kv.Value.FullRlp.AsSpan());
            foreach (KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode> kv in snapshot.StorageNodes) batch.SetStorageTrieNode(kv.Key.Key.Item1, kv.Key.Key.Item2, kv.Value.FullRlp.AsSpan());
        }

        public void Dispose() => _persistence.Clear();
    }

    // Process-wide, so restore the previous value.
    private static IDisposable SetMinIdleGap(TimeSpan gap)
    {
        TimeSpan previous = IdleStorageApplier.MinIdleGap;
        IdleStorageApplier.MinIdleGap = gap;
        return new Reactive.AnonymousDisposable(() => IdleStorageApplier.MinIdleGap = previous);
    }

    /// <summary>Runs each job inside its push, so every hinted path is warm before the commit.</summary>
    private sealed class InlineTrieWarmer : ITrieWarmer
    {
        public bool PushSlotJob(ITrieWarmer.IStorageWarmer storageTree, in UInt256 index, int sequenceId, bool isDelete)
            => PushSlotJobMpmc(storageTree, index, sequenceId, isDelete);

        public bool PushSlotJobMpmc(ITrieWarmer.IStorageWarmer storageTree, in UInt256 index, int sequenceId, bool isDelete)
        {
            storageTree.WarmUpStorageTrie(index, sequenceId, isDelete);
            return true;
        }

        public bool PushAddressJob(ITrieWarmer.IAddressWarmer scope, Address? path, int sequenceId)
        {
            if (path is not null) scope.WarmUpStateTrie(path, sequenceId);
            return true;
        }

        public void OnEnterScope() { }

        public void OnExitScope() { }
    }

    /// <summary>Counts storage-trie node loads; with <c>delayLoads</c> each takes ~1 ms, as a cold read does on a slow disk.</summary>
    private sealed class CountingReader(IPersistence.IPersistenceReader inner, bool delayLoads) : IPersistence.IPersistenceReader
    {
        private long _loads;

        public long Loads => Interlocked.Read(ref _loads);

        public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) => inner.TryLoadStateRlp(path, flags);

        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags)
        {
            Interlocked.Increment(ref _loads);
            if (delayLoads) Thread.Sleep(1);
            return inner.TryLoadStorageRlp(address, path, flags);
        }

        public Account? GetAccount(Address address) => inner.GetAccount(address);
        public bool TryGetSlot(Address address, in UInt256 slot, ref UInt256 outValue) => inner.TryGetSlot(address, slot, ref outValue);
        public StateId CurrentState => inner.CurrentState;
        public byte[]? GetAccountRaw(in ValueHash256 addrHash) => inner.GetAccountRaw(addrHash);
        public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref UInt256 value) => inner.TryGetStorageRaw(addrHash, slotHash, ref value);
        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) => inner.CreateAccountIterator(startKey, endKey);
        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) => inner.CreateStorageIterator(accountKey, startSlotKey, endSlotKey);
        public bool IsPreimageMode => inner.IsPreimageMode;
        public void Dispose() => inner.Dispose();
    }

    private sealed class CapturingCommitTarget : IFlatCommitTarget
    {
        public Snapshot? Snapshot { get; private set; }

        public void AddSnapshot(Snapshot snapshot, TransientResource transientResource)
        {
            Snapshot = snapshot;
            transientResource.ReleaseLease();
        }
    }

    private sealed class NullCodeDb : IWorldStateScopeProvider.ICodeDb
    {
        public ReadOnlyMemory<byte> GetCode(in ValueHash256 codeHash) => default;

        public IWorldStateScopeProvider.ICodeSetter BeginCodeWrite() => new NullCodeSetter();

        private sealed class NullCodeSetter : IWorldStateScopeProvider.ICodeSetter
        {
            public void Set(in ValueHash256 codeHash, ReadOnlySpan<byte> code) { }

            public void Dispose() { }
        }
    }
}
