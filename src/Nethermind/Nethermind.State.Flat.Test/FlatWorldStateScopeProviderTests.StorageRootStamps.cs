// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.Intrinsics.X86;
using System.Text.Json;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Threading;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

// Bench-only: the storage-root stamps sr_* of BlockDiag.
public partial class FlatWorldStateScopeProviderTests
{
    private const int StampsLargeSlots = 2_000;
    private const int StampsOverwrites = 400;
    private const int StampsDeletes = 300;
    private const int StampsSmallContracts = 6;
    private static readonly Address StampsLarge = TestItem.AddressA;

    [Test]
    public void StorageRootStamps_FillOnALargeContractBlock_AndKeepTheRoot([Values(0, 1, 2)] int seed)
    {
        if (Core.Cpu.RuntimeInformation.IsSingleProcessor) Assert.Ignore("The storage-root loop needs more than one core.");

        using StampsFixture fixture = new();
        BlockDiag diag = new() { BlockNumber = 1 };
        (Hash256 rootOn, Hash256 storageRootOn, long largeLoadsOn) = fixture.RunBlock(seed, diag);
        (Hash256 rootOff, Hash256 storageRootOff, _) = fixture.RunBlock(seed, null);

        long ownReads = diag.Get(BlockDiag.C.SrItem0ReadsOwn);
        long nestedReads = diag.Get(BlockDiag.C.SrItem0ReadsNested);
        bool onMain = diag.Get(BlockDiag.C.SrItem0Main) == 1;
        string label = $"seed {seed}, item 0 on {(onMain ? "main" : "a helper")}, reads {ownReads} + {nestedReads} of {largeLoadsOn}";
        NUnit.Framework.TestContext.Out.WriteLine(label);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rootOn, Is.EqualTo(rootOff), $"{label}: state root");
            Assert.That(storageRootOn, Is.EqualTo(storageRootOff), $"{label}: storage root");

            // S2: the largest item, wherever it ran.
            Assert.That(diag.Get(BlockDiag.C.ParStorageLoops), Is.EqualTo(1), $"{label}: loops");
            Assert.That(diag.Get(BlockDiag.C.SrItem0Writes), Is.EqualTo(StampsOverwrites), $"{label}: writes");
            Assert.That(diag.Get(BlockDiag.C.SrItem0Deletes), Is.EqualTo(StampsDeletes), $"{label}: deletes");
            Assert.That(diag.Get(BlockDiag.C.SrItem0Est), Is.GreaterThanOrEqualTo(StampsOverwrites + StampsDeletes), $"{label}: estimate");
            Assert.That(diag.Get(BlockDiag.C.SrItem0Writes) + diag.Get(BlockDiag.C.SrItem0Deletes) + diag.Get(BlockDiag.C.SrItem0Skipped),
                Is.LessThanOrEqualTo(diag.Get(BlockDiag.C.SrItem0Est)), $"{label}: applied within the estimate");
            Assert.That(diag.Get(BlockDiag.F.SrItem0).WallTicks, Is.GreaterThan(0), $"{label}: item 0 timed");
            Assert.That(diag.Get(BlockDiag.C.SrItem0Main), Is.InRange(0, 1), $"{label}: main flag");
            Assert.That(diag.Get(BlockDiag.C.SrMainItems), Is.EqualTo(diag.Get(BlockDiag.C.ParStorageMain)), $"{label}: main items");
            if (diag.Get(BlockDiag.C.SrMainItems) > 0)
            {
                Assert.That(diag.Get(BlockDiag.C.SrMainIdxFirst), Is.LessThanOrEqualTo(diag.Get(BlockDiag.C.SrMainIdxLast)), $"{label}: first <= last");
                Assert.That(diag.Get(BlockDiag.C.SrMainIdxLast), Is.LessThan(StampsSmallContracts + 1), $"{label}: last in range");
                Assert.That(diag.Get(BlockDiag.C.SrMainIdxSum), Is.GreaterThanOrEqualTo(diag.Get(BlockDiag.C.SrMainIdxLast)), $"{label}: sum");
            }

            if (onMain) Assert.That(diag.Get(BlockDiag.C.SrMainIdxFirst), Is.Zero, $"{label}: main ran item 0 first");
            Assert.That(diag.Get(BlockDiag.C.SrItem0Vcsw), Is.GreaterThanOrEqualTo(0), $"{label}: vcsw");
            Assert.That(diag.Get(BlockDiag.C.SrItem0Ivcsw), Is.GreaterThanOrEqualTo(0), $"{label}: ivcsw");

            // S1: the nested loops, by who started them.
            if (onMain)
            {
                long bsLoops = diag.Get(BlockDiag.C.SrNestedBsLoops);
                Assert.That(bsLoops, Is.GreaterThanOrEqualTo(1), $"{label}: nested BulkSet");
                // A parallel BulkSet runs one item per nibble of a full branch.
                Assert.That(diag.Get(BlockDiag.C.SrNestedBsMain) + diag.Get(BlockDiag.C.SrNestedBsHelper), Is.EqualTo(16 * bsLoops), $"{label}: BulkSet items");
                Assert.That(diag.Get(BlockDiag.F.SrNestedBs).WallTicks, Is.GreaterThan(0), $"{label}: BulkSet timed");
                if (Avx2.IsSupported)
                {
                    long hashLoops = diag.Get(BlockDiag.C.SrNestedHashLoops);
                    Assert.That(hashLoops, Is.GreaterThanOrEqualTo(1), $"{label}: nested hash");
                    Assert.That(diag.Get(BlockDiag.C.SrNestedHashMain) + diag.Get(BlockDiag.C.SrNestedHashHelper), Is.GreaterThanOrEqualTo(2 * hashLoops), $"{label}: hash items");
                }
            }
            else
            {
                Assert.That(diag.Get(BlockDiag.C.SrNestedOffLoops), Is.GreaterThanOrEqualTo(1), $"{label}: nested loops of a helper");
            }

            Assert.That(diag.Get(BlockDiag.F.SrNestedWait).WallTicks,
                Is.LessThanOrEqualTo(diag.Get(BlockDiag.F.SrNestedBs).WallTicks + diag.Get(BlockDiag.F.SrNestedHash).WallTicks), $"{label}: the wait is inside the loops");
            if (diag.Get(BlockDiag.C.SrNestedBsHelper) > 0)
            {
                Assert.That(diag.Get(BlockDiag.F.SrNestedBsFirst).WallTicks, Is.GreaterThan(0), $"{label}: first BulkSet helper timed");
            }

            // S3: every load of the large contract's trie nodes happened inside its item, on its thread or its helpers.
            Assert.That(largeLoadsOn, Is.GreaterThan(0), $"{label}: the block loads trie nodes");
            Assert.That(ownReads + nestedReads, Is.EqualTo(largeLoadsOn), $"{label}: loads attributed");
            Assert.That(diag.Get(BlockDiag.F.SrItem0ReadsOwn).WallTicks > 0, Is.EqualTo(ownReads > 0), $"{label}: own reads timed");
            Assert.That(diag.Get(BlockDiag.F.SrItem0ReadsNested).WallTicks > 0, Is.EqualTo(nestedReads > 0), $"{label}: nested reads timed");
        }

        // No thread keeps a storage-root context after the block: the hook is off everywhere.
        Assert.That(BlockDiag.TrieReadStart(), Is.Zero, "the processing thread's context ended");
        int stale = 0;
        ParallelUnbalancedWork.For(0, 256, ParallelUnbalancedWork.DefaultOptions, i =>
        {
            if (BlockDiag.TrieReadStart() != 0) Interlocked.Increment(ref stale);
        });
        Assert.That(stale, Is.Zero, "no pool thread kept a context");
    }

    [Test]
    public void StorageRootStamps_CountThePrefetch_AndKeepTheRoot([Values(0, 1)] int seed)
    {
        if (Core.Cpu.RuntimeInformation.IsSingleProcessor) Assert.Ignore("The storage-root loop needs more than one core.");

        using StampsFixture fixture = new();
        BlockDiag diag = new() { BlockNumber = 1 };
        // The large contract's 700 changes pass the threshold; the small ones' 5 do not.
        (Hash256 rootOn, Hash256 storageRootOn, long largeLoadsOn) = fixture.RunBlock(seed, diag, prefetchMinChanges: 256);
        (Hash256 rootOff, Hash256 storageRootOff, _) = fixture.RunBlock(seed, null);

        long prefetchLoads = diag.Get(BlockDiag.C.SrItem0PrefetchLoads);
        long ownReads = diag.Get(BlockDiag.C.SrItem0ReadsOwn);
        long nestedReads = diag.Get(BlockDiag.C.SrItem0ReadsNested);
        bool onMain = diag.Get(BlockDiag.C.SrItem0Main) == 1;
        string label = $"seed {seed}, item 0 on {(onMain ? "main" : "a helper")}, prefetch {prefetchLoads} in {diag.Get(BlockDiag.C.SrItem0PrefetchRounds)} rounds, reads {ownReads} + {nestedReads} of {largeLoadsOn}";
        NUnit.Framework.TestContext.Out.WriteLine(label);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rootOn, Is.EqualTo(rootOff), $"{label}: state root");
            Assert.That(storageRootOn, Is.EqualTo(storageRootOff), $"{label}: storage root");

            Assert.That(diag.Get(BlockDiag.C.SrPrefetchContracts), Is.EqualTo(1), $"{label}: only the large contract");
            Assert.That(diag.Get(BlockDiag.C.SrPrefetchLoads), Is.EqualTo(prefetchLoads), $"{label}: its loads");
            Assert.That(diag.Get(BlockDiag.C.SrPrefetchRounds), Is.EqualTo(diag.Get(BlockDiag.C.SrItem0PrefetchRounds)), $"{label}: its rounds");
            Assert.That(prefetchLoads, Is.GreaterThan(0), $"{label}: the prefetch read the cold trie");
            Assert.That(diag.Get(BlockDiag.C.SrItem0PrefetchRounds), Is.GreaterThan(0), $"{label}: rounds");
            Assert.That(diag.Get(BlockDiag.C.SrPrefetchMisses), Is.Zero, $"{label}: no misses from a store holding every node");
            // Every load of the large contract's trie: the prefetch's, and what the commit still read on its own.
            Assert.That(prefetchLoads + ownReads + nestedReads, Is.EqualTo(largeLoadsOn), $"{label}: loads attributed");
            Assert.That(ownReads + nestedReads, Is.LessThanOrEqualTo(4), $"{label}: the commit read almost nothing");
            Assert.That(diag.Get(BlockDiag.F.SrItem0Prefetch).WallTicks, Is.GreaterThan(0), $"{label}: prefetch timed");
            Assert.That(diag.Get(BlockDiag.F.SrItem0AfterPrefetch).WallTicks, Is.GreaterThan(0), $"{label}: the rest of item 0 timed");
            Assert.That(diag.Get(BlockDiag.F.SrItem0Prefetch).WallTicks + diag.Get(BlockDiag.F.SrItem0AfterPrefetch).WallTicks,
                Is.LessThanOrEqualTo(diag.Get(BlockDiag.F.SrItem0).WallTicks), $"{label}: both inside item 0");
            Assert.That(diag.Get(BlockDiag.F.SrPrefetchMain).WallTicks,
                Is.EqualTo(onMain ? diag.Get(BlockDiag.F.SrItem0Prefetch).WallTicks : 0), $"{label}: the processing thread's prefetch time");
        }
    }

    [Test]
    public void StorageRootStamps_LeaveThePrefetchReadsOutOfTheItem()
    {
        BlockDiag diag = new() { BlockNumber = 1 };
        BlockDiag? outer = BlockDiag.Current;
        BlockDiag.Current = diag;
        try
        {
            BlockDiag.BeginPrefetch().End(5, 1, 6);

            using (BlockDiag.EnterStorageItem(diag, 0, firstLoop: true))
            {
                BlockDiag.Prefetch prefetch = BlockDiag.BeginPrefetch();
                Assert.That(BlockDiag.TrieReadStart(), Is.Zero, "a prefetch read is not the item's");
                prefetch.End(7, 2, 1);
                long start = BlockDiag.TrieReadStart();
                Assert.That(start, Is.Not.Zero, "the item's reads count again after its prefetch");
                BlockDiag.TrieReadEnd(start);
            }

            using (BlockDiag.EnterStorageItem(diag, 1, firstLoop: true))
            {
                BlockDiag.BeginPrefetch().End(11, 3, 4);
            }
        }
        finally
        {
            BlockDiag.Current = outer;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diag.Get(BlockDiag.C.SrPrefetchContracts), Is.EqualTo(2), "the two prefetches inside items, not the one outside");
            Assert.That(diag.Get(BlockDiag.C.SrPrefetchLoads), Is.EqualTo(18));
            Assert.That(diag.Get(BlockDiag.C.SrPrefetchRounds), Is.EqualTo(5));
            Assert.That(diag.Get(BlockDiag.C.SrItem0PrefetchLoads), Is.EqualTo(7));
            Assert.That(diag.Get(BlockDiag.C.SrItem0PrefetchRounds), Is.EqualTo(2));
            Assert.That(diag.Get(BlockDiag.C.SrPrefetchMisses), Is.EqualTo(5));
            Assert.That(diag.Get(BlockDiag.C.SrItem0PrefetchMisses), Is.EqualTo(1));
            Assert.That(diag.Get(BlockDiag.C.SrItem0ReadsOwn), Is.EqualTo(1), "only the read after the prefetch");
            Assert.That(diag.Get(BlockDiag.F.SrItem0AfterPrefetch).WallTicks, Is.GreaterThan(0));
            Assert.That(diag.Get(BlockDiag.F.SrPrefetchMain).WallTicks, Is.GreaterThanOrEqualTo(diag.Get(BlockDiag.F.SrItem0Prefetch).WallTicks));
        }
    }

    [Test]
    public void StorageRootStamps_CountOnlyInsideAStorageRootItem()
    {
        BlockDiag diag = new() { BlockNumber = 1 };
        BlockDiag? outer = BlockDiag.Current;
        BlockDiag.Current = diag;
        try
        {
            // The block-processing thread outside a storage-root item, as in the account-trie and state-root steps.
            BlockDiag.NestedLoop outside = BlockDiag.BeginNestedLoop(BlockDiag.NestedKind.BulkSet);
            using (outside.RunItem()) { }
            outside.End();
            Assert.That(BlockDiag.TrieReadStart(), Is.Zero, "no reads timed outside an item");

            using (BlockDiag.EnterStorageItem(diag, 3, firstLoop: true))
            {
                Assert.That(BlockDiag.TrieReadStart(), Is.Zero, "only the largest item times reads");
                BlockDiag.NestedLoop inside = BlockDiag.BeginNestedLoop(BlockDiag.NestedKind.Hash);
                using (inside.RunItem()) { }
                inside.End();
            }

            // A BulkSet loop inside a job of another, as a large batch recurses: both count, only the outer one is timed.
            using (BlockDiag.EnterStorageItem(diag, 1, firstLoop: true))
            {
                BlockDiag.NestedLoop outerLoop = BlockDiag.BeginNestedLoop(BlockDiag.NestedKind.BulkSet);
                using (outerLoop.RunItem())
                {
                    BlockDiag.NestedLoop innerLoop = BlockDiag.BeginNestedLoop(BlockDiag.NestedKind.BulkSet);
                    using (innerLoop.RunItem()) { }
                    innerLoop.End();
                }

                outerLoop.End();
            }

            using (BlockDiag.EnterStorageItem(diag, 0, firstLoop: false))
            {
                Assert.That(BlockDiag.TrieReadStart(), Is.Zero, "item 0 of a later loop is not the largest");
            }

            using (BlockDiag.EnterStorageItem(diag, 0, firstLoop: true))
            {
                long start = BlockDiag.TrieReadStart();
                Assert.That(start, Is.Not.Zero, "the largest item times reads");
                BlockDiag.TrieReadEnd(start);
            }

            using (BlockDiag.EnterStorageItem(null, 0, firstLoop: true))
            {
                Assert.That(BlockDiag.TrieReadStart(), Is.Zero, "a block without diag stamps nothing");
            }

            Assert.That(BlockDiag.TrieReadStart(), Is.Zero, "the context ends with the item");
        }
        finally
        {
            BlockDiag.Current = outer;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diag.Get(BlockDiag.C.SrNestedBsLoops), Is.EqualTo(2), "the two BulkSet loops inside an item, not the one outside");
            Assert.That(diag.Get(BlockDiag.C.SrNestedBsMain), Is.EqualTo(2), "their items, on this thread");
            Assert.That(diag.Get(BlockDiag.C.SrNestedHashLoops), Is.EqualTo(1), "the hash loop inside an item");
            Assert.That(diag.Get(BlockDiag.C.SrNestedHashMain), Is.EqualTo(1), "its item, on this thread");
            Assert.That(diag.Get(BlockDiag.C.SrNestedOffLoops), Is.Zero, "no loop of another thread");
            Assert.That(diag.Get(BlockDiag.C.SrMainItems), Is.EqualTo(3), "items of first loops on this thread");
            Assert.That(diag.Get(BlockDiag.C.SrItem0ReadsOwn), Is.EqualTo(1), "the one read of the largest item");
            Assert.That(diag.Get(BlockDiag.C.SrItem0Main), Is.EqualTo(1), "the largest item ran here");
            Assert.That(diag.Get(BlockDiag.F.SrNestedHash).WallTicks, Is.GreaterThan(0), "the outermost hash loop timed");
            Assert.That(diag.Get(BlockDiag.F.SrNestedBs).WallTicks, Is.GreaterThan(0), "the outer BulkSet loop timed");
            Assert.That(diag.Get(BlockDiag.F.SrNestedWait).WallTicks,
                Is.LessThanOrEqualTo(diag.Get(BlockDiag.F.SrNestedBs).WallTicks + diag.Get(BlockDiag.F.SrNestedHash).WallTicks), "the waits are inside the timed loops");
        }
    }

    [Test]
    public void StorageRootStamps_AreNamedInTheLine()
    {
        BlockDiag diag = new() { BlockNumber = 1 };
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            diag.WriteJson(writer, "diag");
            writer.WriteEndObject();
        }

        using JsonDocument json = JsonDocument.Parse(stream.ToArray());
        JsonElement fin = json.RootElement.GetProperty("diag").GetProperty("fin");
        JsonElement cnt = json.RootElement.GetProperty("diag").GetProperty("cnt");
        using (Assert.EnterMultipleScope())
        {
            foreach (string name in new[] { "sr_nested_bs", "sr_nested_hash", "sr_nested_wait", "sr_nested_bs_first", "sr_nested_hash_first", "sr_item0", "sr_item0_reads_own", "sr_item0_reads_nested",
                         "sr_prefetch_main", "sr_item0_prefetch", "sr_item0_after_prefetch" })
            {
                Assert.That(fin.TryGetProperty(name, out _), Is.True, name);
            }

            foreach (string name in new[]
                     {
                         "sr_nested_bs_loops", "sr_nested_bs_main", "sr_nested_bs_helper", "sr_nested_hash_loops", "sr_nested_hash_main", "sr_nested_hash_helper",
                         "sr_nested_off_loops", "sr_nested_off_items", "sr_main_items", "sr_main_idx_first", "sr_main_idx_last", "sr_main_idx_sum",
                         "sr_item0_est", "sr_item0_writes", "sr_item0_deletes", "sr_item0_skipped", "sr_item0_main",
                         "sr_item0_vcsw", "sr_item0_ivcsw", "sr_item0_majflt", "sr_item0_inblock", "sr_item0_reads_own", "sr_item0_reads_nested",
                         "sr_prefetch_contracts", "sr_prefetch_loads", "sr_prefetch_rounds", "sr_item0_prefetch_loads", "sr_item0_prefetch_rounds",
                         "sr_prefetch_misses", "sr_item0_prefetch_misses",
                     })
            {
                Assert.That(cnt.TryGetProperty(name, out _), Is.True, name);
            }
        }
    }

    private static UInt256 StampsSlot(int index) => new(Keccak.Compute(BitConverter.GetBytes(index)).Bytes, isBigEndian: true);

    private static Address StampsSmall(int index) => Address.FromNumber((UInt256)(100 + index));

    /// <summary>A large contract and a few small ones held in persistence, and a block over them run through the world
    /// state, so its block-end commit takes the parallel storage-root loop with the large contract as item 0.</summary>
    private sealed class StampsFixture : IDisposable
    {
        private readonly FlatDbConfig _config = new() { ApplyStorageWritesOnIdleThread = false };
        private readonly ResourcePool _resourcePool;
        private readonly RocksDbPersistence _persistence = new(new SnapshotableMemColumnsDb<FlatDbColumns>(), LimboLogs.Instance, NullTrieNodeLog.Instance);
        private readonly StateId _baseState;
        private readonly Hash256 _largeHash = StampsLarge.ToAccountPath.ToHash256();

        public StampsFixture()
        {
            _resourcePool = new ResourcePool(_config);
            SnapshotBundle bundle = new(
                new ReadOnlySnapshotBundle(new SnapshotPooledList(0), new NoopPersistenceReader(), recordDetailedMetrics: false, PersistedSnapshotStack.Empty()),
                new TrieNodeCache(_config, LimboLogs.Instance), _resourcePool, ResourcePool.Usage.MainBlockProcessing);
            StampsCommitTarget target = new();
            using FlatWorldStateScope scope = new(StateId.PreGenesis, bundle, new StampsCodeDb(), target, _config, new NoopTrieWarmer(), LimboLogs.Instance);

            using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1 + StampsSmallContracts))
            {
                batch.Set(StampsLarge, new Account(1));
                using (IWorldStateScopeProvider.IStorageWriteBatch storage = batch.CreateStorageWriteBatch(StampsLarge, StampsLargeSlots))
                {
                    for (int i = 0; i < StampsLargeSlots; i++) storage.Set(StampsSlot(i), (UInt256)(i + 1));
                }

                for (int c = 0; c < StampsSmallContracts; c++)
                {
                    batch.Set(StampsSmall(c), new Account(1));
                    using IWorldStateScopeProvider.IStorageWriteBatch storage = batch.CreateStorageWriteBatch(StampsSmall(c), 3);
                    for (int i = 0; i < 3; i++) storage.Set((UInt256)i, (UInt256)(i + 1));
                }
            }

            scope.Commit(0);
            _baseState = new StateId(0, scope.RootHash);
            Persist(target.Snapshot ?? throw new InvalidOperationException("The base commit produced no snapshot"));
        }

        /// <summary>Runs one block on the persisted state, with <paramref name="diag"/> as the block's diagnostics.</summary>
        /// <param name="prefetchMinChanges">The storage-root prefetch threshold; 0 runs the block without it.</param>
        /// <returns>The state root, the large contract's storage root, and the loads of its trie nodes from persistence.</returns>
        public (Hash256 Root, Hash256 StorageRoot, long LargeLoads) RunBlock(int seed, BlockDiag? diag, int prefetchMinChanges = 0)
        {
            StampsReader reader = new(_persistence.CreateReader(), _largeHash);
            SnapshotBundle bundle = new(
                new ReadOnlySnapshotBundle(new SnapshotPooledList(0), reader, recordDetailedMetrics: false, PersistedSnapshotStack.Empty()),
                new TrieNodeCache(_config, LimboLogs.Instance), _resourcePool, ResourcePool.Usage.MainBlockProcessing);
            FlatDbConfig config = new() { ApplyStorageWritesOnIdleThread = false, StorageRootPrefetchMinChanges = prefetchMinChanges };
            using WarmReadPool? pool = prefetchMinChanges > 0 ? new WarmReadPool(16) : null;
            FlatWorldStateScope scope = new(_baseState, bundle, new StampsCodeDb(), new StampsCommitTarget(), config, new NoopTrieWarmer(), LimboLogs.Instance,
                warmReadPool: pool is null ? null : new Lazy<WarmReadPool>(() => pool));
            WorldState state = new(new StampsScopeProvider(scope), LimboLogs.Instance);

            Random random = new(seed);
            int[] picked = [.. Enumerable.Range(0, StampsLargeSlots).OrderBy(_ => random.Next()).Take(StampsOverwrites + StampsDeletes)];
            using (state.BeginScope(IWorldState.PreGenesis))
            {
                for (int i = 0; i < picked.Length; i++)
                {
                    UInt256 value = i < StampsOverwrites ? (UInt256)(1_000_000 + picked[i]) : UInt256.Zero;
                    state.Set(new StorageCell(StampsLarge, StampsSlot(picked[i])), value);
                }

                for (int c = 0; c < StampsSmallContracts; c++)
                {
                    for (int i = 1; i < 6; i++) state.Set(new StorageCell(StampsSmall(c), (UInt256)i), (UInt256)(10 * i + c + random.Next(1, 5)));
                }

                long before = reader.LargeLoads;
                BlockDiag? outer = BlockDiag.Current;
                BlockDiag.Current = diag;
                try
                {
                    state.Commit(Frontier.Instance);
                }
                finally
                {
                    BlockDiag.Current = outer;
                }

                long largeLoads = reader.LargeLoads - before;
                state.RecalculateStateRoot();
                return (state.StateRoot, state.GetStorageRoot(StampsLarge).ToHash256(), largeLoads);
            }
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

    /// <summary>Opens the one scope it was given, as the world state asks.</summary>
    private sealed class StampsScopeProvider(IWorldStateScopeProvider.IScope scope) : IWorldStateScopeProvider
    {
        public bool HasRoot(BlockHeader? baseBlock) => true;

        public bool TryBeginScopeAtTarget(BlockHeader targetBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? opened)
        {
            opened = scope;
            return true;
        }

        public bool HasStateForTargetBlock(BlockHeader targetBlock) => true;

        public bool TryBeginScope(BlockHeader? baseBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? opened)
        {
            opened = scope;
            return true;
        }
    }

    /// <summary>Counts the storage-trie loads of one contract.</summary>
    private sealed class StampsReader(IPersistence.IPersistenceReader inner, Hash256 counted) : IPersistence.IPersistenceReader
    {
        private long _largeLoads;

        public long LargeLoads => Interlocked.Read(ref _largeLoads);

        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags)
        {
            if (address == counted) Interlocked.Increment(ref _largeLoads);
            return inner.TryLoadStorageRlp(address, path, flags);
        }

        public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) => inner.TryLoadStateRlp(path, flags);
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

    private sealed class StampsCommitTarget : IFlatCommitTarget
    {
        public Snapshot? Snapshot { get; private set; }

        public void AddSnapshot(Snapshot snapshot, TransientResource transientResource)
        {
            Snapshot = snapshot;
            transientResource.ReleaseLease();
        }
    }

    private sealed class StampsCodeDb : IWorldStateScopeProvider.ICodeDb
    {
        public ReadOnlyMemory<byte> GetCode(in ValueHash256 codeHash) => default;

        public IWorldStateScopeProvider.ICodeSetter BeginCodeWrite() => new StampsCodeSetter();

        private sealed class StampsCodeSetter : IWorldStateScopeProvider.ICodeSetter
        {
            public void Set(in ValueHash256 codeHash, ReadOnlySpan<byte> code) { }

            public void Dispose() { }
        }
    }
}
