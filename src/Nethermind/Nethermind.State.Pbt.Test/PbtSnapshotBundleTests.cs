// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Memory;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Threading;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.Pbt.NodeBuffer;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Pbt.Common;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.PersistedSnapshots;
using Nethermind.Monitoring.Config;
using Nethermind.State.Pbt.Snapshot;
using Nethermind.State.Pbt.ScopeProvider;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.State.Pbt.Test.PbtStoreTestExtensions;

namespace Nethermind.State.Pbt.Test;

public class PbtSnapshotBundleTests
{
    [Test]
    public void Mixed_persisted_and_memory_bundle_survives_pruning_and_honors_clears([Values] bool clear, [Values] bool rewrite)
    {
        using PbtPersistedTestStore store = new();
        using PbtSnapshotRepository repository = new(new MetricsConfig(), NullSnapshotCatalog.Instance, new PbtPersistedPublicationGate());
        PbtResourcePool pool = new(new PbtConfig());
        ValueHash256 address = Eip8297KeyDerivation.AddressHash(TestItem.AddressA.ToHash().Bytes);
        PbtSnapshotContent persistedContent = new();
        persistedContent.Accounts[address] = PbtAccount.From(new Account(1, 100), null);
        persistedContent.Codes[TestItem.KeccakA.ValueHash256] = new CodeInfo(new byte[] { 1, 2, 3 });
        PbtPath key = Eip8297KeyDerivation.HeaderStorageKey(address, 5);
        persistedContent.SetRun(new HashedKey<PbtPath>(SlotRun.RunKey(key)), SlotRun.Empty.With(SlotRun.IndexOf(key), (UInt256)7));
        using PbtSnapshot source = new(StateId.PreGenesis, new StateId(0, default), TestItem.KeccakA.ValueHash256, persistedContent, pool, PbtResourcePool.Usage.MainBlockProcessing);
        using PbtPersistedSnapshot persisted = store.Build(source);
        repository.TryAddPersisted(persisted);
        PbtSnapshotContent memoryContent = new();
        if (clear) memoryContent.ClearStorage(address);
        if (rewrite) memoryContent.SetRun(new HashedKey<PbtPath>(SlotRun.RunKey(key)), SlotRun.Empty.With(SlotRun.IndexOf(key), (UInt256)9));
        memoryContent.Accounts[address] = null;
        PbtSnapshot memory = new(new StateId(0, default), new StateId(1, default), TestItem.KeccakB.ValueHash256, memoryContent, pool, PbtResourcePool.Usage.MainBlockProcessing);
        repository.TryAdd(memory);
        Reader reader = new(default, null);
        using PbtReadOnlySnapshotBundle bundle = PbtSnapshotBundleTestExtensions.ReadOnlyBundle(repository.TryLeaseReadChain(new StateId(1, default), StateId.PreGenesis), reader, slotFilterBitsPerKey: 0);
        Assert.That(bundle.TryLease(), Is.True);
        repository.RemoveMemoryState(new StateId(1, default));
        repository.RemovePersistedStatesBefore(1);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.TreeRoot, Is.EqualTo(TestItem.KeccakB.ValueHash256));
            Assert.That(bundle.GetAccount(TestItem.AddressA), Is.Null);
            Assert.That(bundle.GetCode(TestItem.KeccakA.ValueHash256)!.CodeSpan.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(bundle.GetSlot(TestItem.AddressA, 5), Is.EqualTo((UInt256)(rewrite ? 9 : clear ? 0 : 7)));
            Assert.That(repository.HasState(memory.To), Is.False);
        }
        PackedSlotRun run = bundle.RentRun(new HashedKey<PbtPath>(SlotRun.RunKey(key)), address);
        try { Assert.That(run.Get(SlotRun.IndexOf(key)), Is.EqualTo(bundle.GetSlot(TestItem.AddressA, 5))); }
        finally { SlotRun.Return(run); }
        bundle.Dispose();
        Assert.That(reader.DisposeCount, Is.Zero, "independent bundle pointer still owns the reader");
    }

    [Test]
    public void First_write_to_a_run_seeds_the_whole_run_from_the_newest_layer_holding_it([Values] bool heldByLayer)
    {
        // Slots 3, 5 and 6 share one run.
        PbtVariableTreeKey persistedKey = PbtTestLeaves.SlotKey(TestItem.AddressA, 5);
        PbtVariableTreeKey layerKey = PbtTestLeaves.SlotKey(TestItem.AddressA, 6);
        UInt256 persisted = new(Value(1), isBigEndian: true);
        UInt256 layer = new(Value(2), isBigEndian: true);
        UInt256 local = new(Value(3), isBigEndian: true);
        PbtResourcePool pool = new(new PbtConfig());
        PbtSnapshotContent sharedContent = new();
        if (heldByLayer) sharedContent.SetSlot(layerKey, layer);
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0), new PbtReadOnlySnapshotBundle(PbtSnapshotBundleTestExtensions.Chain(pool, sharedContent), new Reader(persistedKey, new ValueHash256(Value(1))), recordDetailedMetrics: false, new PbtPersistedSnapshotList(0), PbtSnapshotBundleTestExtensions.NoNodeGroupPin, slotFilterBitsPerKey: 0), PbtSnapshotTestExtensions.DefaultGroupRecordBuffer, pool, PbtResourcePool.Usage.MainBlockProcessing, filterInMemorySlotReads: false);
        bundle.SetSlot(TestItem.AddressA, 3, local);
        UInt256[] afterWrite = [bundle.GetSlot(TestItem.AddressA, 3), bundle.GetSlot(TestItem.AddressA, 5), bundle.GetSlot(TestItem.AddressA, 6)];
        bundle.SetSlot(TestItem.AddressA, heldByLayer ? 6u : 5u, default);
        UInt256[] afterClear = [bundle.GetSlot(TestItem.AddressA, 3), bundle.GetSlot(TestItem.AddressA, 5), bundle.GetSlot(TestItem.AddressA, 6)];
        bundle.SelfDestruct(Eip8297KeyDerivation.AddressHash(TestItem.AddressA.ToHash().Bytes));
        bundle.SetSlot(TestItem.AddressA, 5, local);
        using (Assert.EnterMultipleScope())
        {
            // A layer holding the run answers for all of it, so the persisted slot is masked when a layer holds the run.
            Assert.That(afterWrite, Is.EqualTo(new[] { local, heldByLayer ? default : persisted, heldByLayer ? layer : default }));
            Assert.That(afterClear, Is.EqualTo(new[] { local, default, default }));
            Assert.That(new[] { bundle.GetSlot(TestItem.AddressA, 3), bundle.GetSlot(TestItem.AddressA, 5), bundle.GetSlot(TestItem.AddressA, 6) }, Is.EqualTo(new[] { default, local, default }));
        }
    }

    [Test]
    public void Log_misses_read_memory_tiers_before_persisted_layers_which_keep_their_precedence(
        [Values(0, 1, 2)] int memoryTier, [Values(0, 1, 2)] int persistedTier)
    {
        using PbtPersistedTestStore persistedStore = new();
        PbtResourcePool pool = new(new PbtConfig());
        PbtStorageNodePath path = default;
        byte[] database = Encoding(1);
        byte[] older = Encoding(2);
        byte[] newer = Encoding(3);
        byte[] memory = Encoding(4);
        PbtPersistedSnapshotList persisted = new(2);
        if (persistedTier != 0)
        {
            using PbtSnapshot first = new(StateId.PreGenesis, new StateId(0, default), default,
                Content(path, older), pool, PbtResourcePool.Usage.MainBlockProcessing);
            persisted.Add(persistedStore.Build(first));
            using PbtSnapshot second = new(first.To, new StateId(1, default), default,
                Content(path, persistedTier == 2 ? null : newer), pool, PbtResourcePool.Usage.MainBlockProcessing);
            persisted.Add(persistedStore.Build(second));
        }
        PbtSnapshotContent shared = pool.GetSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing);
        PbtSnapshotContent local = pool.GetSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing);
        if (memoryTier == 2) shared.SetTree((path, memory));
        if (memoryTier == 1) local.SetTree((path, memory));
        Reader reader = new(default, null) { GroupPayload = database };
        PbtReadOnlySnapshotBundle readOnly = new(PbtSnapshotBundleTestExtensions.Chain(pool, shared), reader, false, persisted, PbtSnapshotBundleTestExtensions.NoNodeGroupPin, slotFilterBitsPerKey: 0);
        using PbtSnapshotBundle bundle = new(PbtSnapshotBundleTestExtensions.Chain(pool, local), readOnly, PbtSnapshotTestExtensions.DefaultGroupRecordBuffer, pool,
            PbtResourcePool.Usage.MainBlockProcessing, filterInMemorySlotReads: false);
        if (memoryTier == 0) PublishRoot(bundle, memory);
        using PbtSnapshotStore store = new(bundle);
        PbtTraversalPath cursor = new(Span<byte>.Empty);
        Assert.That(store.TryGetNodeGroupRecord(0, cursor, out _), Is.False);
        // Nothing records a removed group, so a newer persisted snapshot without the group leaves the older one visible.
        byte[] expected = persistedTier switch { 0 => database, 1 => newer, _ => older };
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using RefCountingMemory? actual = store.GetNodeGroup(cursor);
            Assert.That(actual?.GetSpan().ToArray(), Is.EqualTo(memory), "a fold's miss reads the branch's memory tiers first");
        }
        using RefCountingMemory? persistedOnly = bundle.GetPersistedNodeGroup(path);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(persistedOnly?.GetSpan().ToArray(), Is.EqualTo(expected));
            Assert.That(reader.GroupReadCount, Is.EqualTo(persistedTier == 0 ? 1 : 0), "persisted entries stop the captured-reader fallback");
        }

        byte[] Encoding(byte seed) => PbtNodeGroupEncoder.Encode(path, [new PbtNodeRecord(path, BranchEncoding(seed))], default);
    }

    [Test]
    public async Task No_op_commits_and_compaction_preserve_the_exact_warm_root_hint([Values(1, 3, 128)] int noOpBlocks)
    {
        await using PbtTestContext context = new(config: new PbtConfig { CompactSize = 1, CompactionOffset = 0, EnableLongFinality = false });
        Hash256 rootHash;
        using (PbtWorldStateScope initial = context.BeginScope(null))
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = initial.StartWriteBatch(3))
            {
                batch.Set(TestItem.AddressA, new Account(1, 100));
                batch.Set(TestItem.AddressB, new Account(1, 200));
                batch.Set(TestItem.AddressC, new Account(1, 300));
            }
            initial.Commit(1);
            rootHash = initial.RootHash;
        }
        BlockHeader parent = Build.A.BlockHeader.WithNumber(1).WithStateRoot(rootHash).TestObject;
        for (ulong number = 2; number <= (ulong)noOpBlocks + 1; number++)
        {
            using PbtWorldStateScope unchanged = context.BeginScope(parent);
            unchanged.Commit(number);
            Assert.That(unchanged.RootHash, Is.EqualTo(rootHash));
            parent = Build.A.BlockHeader.WithNumber(number).WithStateRoot(rootHash).TestObject;
        }
        if (noOpBlocks > 1)
        {
            ICompactionSchedule schedule = Substitute.For<ICompactionSchedule>();
            schedule.GetCompactSize((ulong)parent.Number).Returns(2UL);
            PbtSnapshotCompactor compactor = new(context.ResourcePool, schedule, context.Repository, context.Config, LimboLogs.Instance);
            Assert.That(compactor.DoCompactSnapshot(new StateId(parent)), Is.True);
        }
        using PbtWorldStateScope next = context.BeginScope(parent, out PbtSnapshotBundle bundle);
        Assert.That(bundle.RootGroupPointer != 0, Is.True);
        bundle.SetAccount(TestItem.AddressA, new Account(2, 100));
        using PbtPartitionBatches changes = bundle.PrepareLeafChanges();
        using PbtSnapshotStore store = new(bundle);
        CountingStore counted = new(store);
        ValueHash256 updated = TrieUpdater.UpdateRoot(counted, bundle.TreeRoot, changes, PbtTreeHarness.FoldQuota(), PbtTreeHarness.DefaultFanOut, null, context.NodeGroupMemory);
        bundle.CompleteLeafChanges();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(updated, Is.Not.EqualTo(bundle.TreeRoot));
            Assert.That(counted.DirectReads, Is.GreaterThan(0));
            Assert.That(counted.SnapshotReads, Is.Zero);
        }
    }

    [Test]
    public async Task Persisted_only_base_starts_from_its_indexed_root_or_copies_the_root_group_back([Values] bool indexed)
    {
        // Production retains the newest unpinned generation, which keeps the persisted root record resident.
        using PbtGroupRecordBuffer buffer = new(PooledRefCountingMemoryProvider.Instance, compressedMaxMemory: 0, liveGenerations: indexed ? 1 : 0);
        await using PbtTestContext context = new(groupRecordBuffer: buffer);
        Hash256 rootHash;
        using (PbtWorldStateScope initial = context.BeginScope(null))
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = initial.StartWriteBatch(2))
            {
                batch.Set(TestItem.AddressA, new Account(1, 100));
                batch.Set(TestItem.AddressB, new Account(1, 200));
            }
            initial.Commit(1);
            rootHash = initial.RootHash;
        }
        context.Manager.FlushCache(CancellationToken.None);
        BlockHeader parent = Build.A.BlockHeader.WithNumber(1).WithStateRoot(rootHash).TestObject;
        using PbtWorldStateScope next = context.BeginScope(parent, out PbtSnapshotBundle bundle);
        Assert.That(bundle.RootGroupPointer != 0, Is.EqualTo(indexed));
        using PbtSnapshot unchanged = bundle.CollectSnapshot(new StateId(parent), new StateId(2, rootHash), bundle.TreeRoot);
        Assert.That(buffer.TryLocate(unchanged.RootGroupPointer, out PbtGroupRecord root), Is.True, "an unchanged non-empty tree always publishes a resident root group");
        using (RefCountingMemory persisted = bundle.GetPersistedNodeGroup(default)!)
            Assert.That(root.Payload.ToArray(), Is.EqualTo(persisted.GetSpan().ToArray()));
        bundle.SetAccount(TestItem.AddressA, new Account(2, 100));
        using PbtPartitionBatches changes = bundle.PrepareLeafChanges();
        using PbtSnapshotStore store = new(bundle);
        CountingStore counted = new(store);
        TrieUpdater.UpdateRoot(counted, bundle.TreeRoot, changes, PbtTreeHarness.FoldQuota(), PbtTreeHarness.DefaultFanOut, null, context.NodeGroupMemory);
        bundle.CompleteLeafChanges();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(counted.DirectReads, Is.GreaterThan(0), "the fold reads its root group from the buffer");
            // A copied root group links to no descendants, which the fold then reads from persistence.
            Assert.That(counted.SnapshotReads, indexed ? Is.Zero : Is.GreaterThan(0));
        }
    }

    [Test]
    public void Changed_roots_without_a_published_root_are_rejected()
    {
        using PbtGroupRecordBuffer sourceBuffer = new(PooledRefCountingMemoryProvider.Instance, compressedMaxMemory: 0, liveGenerations: 0);
        PbtResourcePool pool = new(new PbtConfig());
        PbtSnapshotContent content = pool.GetSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing);
        content.BeginNodeGroupWrites(sourceBuffer);
        PbtStorageNodePath path = default;
        byte[] encoding = PbtNodeGroupEncoder.Encode(path, [new PbtNodeRecord(path, BranchEncoding(1))], default);
        content.SetTree((path, encoding));
        PbtSnapshot source = new(StateId.PreGenesis, new StateId(1, default), TestItem.KeccakA.ValueHash256, content, pool, PbtResourcePool.Usage.MainBlockProcessing);
        PbtSnapshotPooledList chain = new(1) { source };
        PbtReadOnlySnapshotBundle readOnly = new(chain, new Reader(default, null), false, new PbtPersistedSnapshotList(0), PbtSnapshotBundleTestExtensions.NoNodeGroupPin, slotFilterBitsPerKey: 0);
        using PbtSnapshotBundle branch = new(new PbtSnapshotPooledList(0), readOnly, sourceBuffer, pool, PbtResourcePool.Usage.MainBlockProcessing, filterInMemorySlotReads: false);
        Assert.Throws<InvalidOperationException>(() => branch.CollectSnapshot(source.To, new StateId(2, default), TestItem.KeccakB.ValueHash256));
    }

    [Test]
    public void Log_backed_snapshot_readers_survive_pruning_and_payloads_survive_pool_return([Values] bool native)
    {
        using NativeBucketedMemoryPool nativePool = PbtNodeGroupMemory.CreateNativePool();
        TrackingMemoryProvider memory = new(native ? nativePool : PooledRefCountingMemoryProvider.Instance);
        using PbtGroupRecordBuffer buffer = new(memory, compressedMaxMemory: 0, generationBytes: 256, liveGenerations: 0);
        PbtResourcePool pool = new(new PbtConfig(), memory);
        using PbtSnapshotRepository repository = new(new MetricsConfig(), NullSnapshotCatalog.Instance, new PbtPersistedPublicationGate());
        PbtStorageNodePath path = default;
        byte[] encoding = PbtNodeGroupEncoder.Encode(path, [new PbtNodeRecord(path, BranchEncoding(1))], default);
        PbtSnapshotContent content = pool.GetSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing);
        content.BeginNodeGroupWrites(buffer);
        content.SetTree((path, encoding));
        PbtSnapshot source = new(StateId.PreGenesis, new StateId(1, default), TestItem.KeccakA.ValueHash256, content, pool, PbtResourcePool.Usage.MainBlockProcessing);
        ulong rootPointer = source.RootGroupPointer;
        buffer.SetRoot(source.TreeRoot, rootPointer);
        Assert.That(repository.TryAdd(source), Is.True);
        PbtReadOnlySnapshotBundle readOnly = PbtSnapshotBundleTestExtensions.ReadOnlyBundle(repository.TryLeaseReadChain(source.To, StateId.PreGenesis), new Reader(default, null), slotFilterBitsPerKey: 0);
        using PbtSnapshotBundle branch = new(new PbtSnapshotPooledList(0), readOnly, buffer, pool, PbtResourcePool.Usage.MainBlockProcessing, filterInMemorySlotReads: false);
        repository.RemoveMemoryState(source.To);
        using RefCountingMemory? independentlyOwned = branch.GetNodeGroup(path);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rootPointer != 0, Is.True);
            Assert.That(branch.RootGroupPointer, Is.EqualTo(rootPointer));
            Assert.That(independentlyOwned!.GetSpan().ToArray(), Is.EqualTo(encoding));
            Assert.That(repository.HasState(source.To), Is.False);
        }
        Assert.That(buffer.TryLocate(rootPointer, out _), Is.True);
        branch.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(buffer.ResidentBytes, Is.Zero);
            Assert.That(source.RootGroupPointer, Is.EqualTo(rootPointer), "the snapshot root hint is not read back from recycled content");
            Assert.That(independentlyOwned!.GetSpan().ToArray(), Is.EqualTo(encoding));
            Assert.That(TrackingMemoryProvider.CountUnreleased(memory.Rented), Is.EqualTo(1), "only the independent caller payload remains");
        }
    }

    [Test]
    public void SnapshotStore_ConcurrentWritersPublishTheRootOnlyOnSuccessfulDispose([Values] bool emptied, [Values] bool complete)
    {
        using PbtSnapshotBundle bundle = CreateBundle(new Reader(default, null));
        PbtStorageNodePath groupPath = new([], 0);
        byte[] encoding = PbtNodeGroupEncoder.Encode(groupPath, [new PbtNodeRecord(groupPath, BranchEncoding(1))], default);

        using (PbtSnapshotStore store = new(bundle))
        {
            System.Threading.Tasks.Parallel.For(0, 1000, iteration =>
            {
                using IPbtConcurrentWriter writer = store.CreateWriter();
                PbtTraversalPath path = new(Span<byte>.Empty);
                for (int write = 0; write < 2; write++)
                {
                    writer.TryReserveNodeGroup(path, encoding.Length, default, out _, out Span<byte> payload);
                    encoding.CopyTo(payload);
                }
                if (emptied) writer.TryReserveNodeGroup(path, 0, default, out _, out _);
            });

            using RefCountingMemory? pending = bundle.GetNodeGroup(groupPath);
            Assert.That(pending, Is.Null, "handed-off groups stay invisible until the store is disposed");
            if (complete) store.CompleteFold();
        }

        using PbtSnapshot snapshot = bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), default);
        bool found = snapshot.Content.TryFindNodeGroup(snapshot.RootGroupPointer, groupPath, out RefCountingMemory? current);
        using (current)
        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.RootGroupPointer != 0, Is.EqualTo(complete && !emptied));
            Assert.That(found, Is.EqualTo(complete && !emptied));
            Assert.That(current?.GetSpan().ToArray(), Is.EqualTo(emptied || !complete ? null : encoding));
            Assert.That(snapshot.Content.NodeGroupCount, Is.EqualTo(complete ? 2000 : 0), "every reserved group counts towards the layer's size bound");
        }
    }

    [Test]
    public void SnapshotContent_ReplacedTreesAndResetPreserveReadLeases([Values(1, 8)] int groupCount, [Values] bool removed)
    {
        TrackingMemoryProvider memoryProvider = new();
        using PbtGroupRecordBuffer buffer = new(memoryProvider, compressedMaxMemory: 0, generationBytes: 256, liveGenerations: 0);
        using PbtSnapshotContent content = new(memoryProvider);
        for (int round = 0; round < 2; round++)
        {
            content.BeginNodeGroupWrites(buffer);
            PbtStorageNodePath[] paths = [.. Enumerable.Range(0, groupCount).Select(static index => new PbtStorageNodePath([(byte)(index << 5)], 3))];
            byte[][] originals = [.. paths.Select(path => Group(path, (byte)(round + 1)))];
            byte[][] replacements = [.. paths.Select(path => Group(path, (byte)(round + 3)))];
            content.SetTree([.. paths.Select((path, index) => (path, (byte[]?)originals[index]))]);
            ulong originalRoot = content.RootGroupPointer;
            RefCountingMemory?[] read = new RefCountingMemory?[groupCount];
            try
            {
                for (int index = 0; index < groupCount; index++) Assert.That(content.TryFindNodeGroup(originalRoot, paths[index], out read[index]), Is.True);
                content.SetTree([.. paths.Select((path, index) => (path, removed ? null : (byte[]?)replacements[index]))]);
                using (Assert.EnterMultipleScope())
                {
                    for (int index = 0; index < groupCount; index++)
                    {
                        Assert.That(content.TryFindNodeGroup(content.RootGroupPointer, paths[index], out RefCountingMemory? current), Is.EqualTo(!removed));
                        using (current) Assert.That(current?.GetSpan().ToArray(), Is.EqualTo(removed ? null : replacements[index]));
                        Assert.That(content.TryFindNodeGroup(originalRoot, paths[index], out RefCountingMemory? replaced), Is.True);
                        using (replaced) Assert.That(replaced!.GetSpan().ToArray(), Is.EqualTo(originals[index]), "the pin keeps a replaced tree readable until reset");
                        Assert.That(read[index]!.GetSpan().ToArray(), Is.EqualTo(originals[index]));
                    }
                    Assert.That(buffer.GenerationCount, Is.GreaterThan(1));
                }

                content.Reset();
                content.Reset();
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(content.RootGroupPointer, Is.Zero);
                    Assert.That(content.NodeGroupCount, Is.Zero);
                    Assert.That(content.GetPayloadSize(), Is.EqualTo(default(PbtSnapshotPayloadSize)));
                    Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.EqualTo(groupCount), "reset releases every generation but not caller-owned reads");
                    for (int index = 0; index < groupCount; index++)
                        Assert.That(read[index]!.GetSpan().ToArray(), Is.EqualTo(originals[index]));
                }
            }
            finally
            {
                foreach (RefCountingMemory? payload in read) ((IDisposable?)payload)?.Dispose();
            }
            Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.Zero);
        }

        static byte[] Group(PbtStorageNodePath path, byte marker) =>
            PbtNodeGroupEncoder.Encode(path, [new PbtNodeRecord(PbtTestPaths.PathOf(path, 0), BranchEncoding(marker))], default);
    }

    [Test]
    public void Dispose_ReturnsBuildersOnceEvenWhenOtherCleanupThrows()
    {
        TrackingBuilderPool pool = new() { ThrowOnBuilderReturn = true };
        PbtSnapshotBundle bundle = PbtSnapshotBundleTestExtensions.CreateBundle(pool, new Reader(default, null));
        Assert.Throws<IOException>(bundle.Dispose);
        Assert.DoesNotThrow(bundle.Dispose);
        Assert.That(pool.ReturnCount, Is.EqualTo(3));
    }

    private static PbtSnapshotBundle CreateBundle(Reader reader) =>
        PbtSnapshotBundleTestExtensions.CreateBundle(new PbtResourcePool(new PbtConfig()), reader);

    private sealed class TrackingBuilderPool : IPbtResourcePool
    {
        public int ReturnCount { get; private set; }
        public bool ThrowOnBuilderReturn { get; init; }

        public PbtSnapshotContent GetSnapshotContent(PbtResourcePool.Usage usage) => new();
        public void ReturnSnapshotContent(PbtResourcePool.Usage usage, PbtSnapshotContent content) => content.Dispose();
        public PbtWriteBatchBuilder<PbtPath> GetWriteBatch(PbtResourcePool.Usage usage) => new();
        public void ReturnWriteBatch(PbtResourcePool.Usage usage, PbtWriteBatchBuilder<PbtPath> builder)
        {
            builder.Dispose();
            ReturnCount++;
            if (ThrowOnBuilderReturn) throw new IOException("Builder return failed");
        }

        public PbtWriteBatchBuilder<PbtStoragePath> GetStorageWriteBatch(PbtResourcePool.Usage usage) => new();
        public void ReturnStorageWriteBatch(PbtResourcePool.Usage usage, PbtWriteBatchBuilder<PbtStoragePath> builder)
        {
            builder.Dispose();
            ReturnCount++;
            if (ThrowOnBuilderReturn) throw new IOException("Builder return failed");
        }
    }

    [Test]
    public void Storage_mutations_use_small_header_and_wide_storage_partitions([Values(0u, 63u, 64u, 256u, uint.MaxValue)] uint slotValue)
    {
        UInt256 slot = slotValue == uint.MaxValue ? UInt256.MaxValue : new UInt256(slotValue);
        PbtVariableTreeKey key = PbtTestLeaves.SlotKey(TestItem.AddressA, slot);
        using PbtSnapshotBundle bundle = CreateBundle(new Reader(key, null));
        UInt256 value = new(Value(9), isBigEndian: true);
        foreach (bool delete in new[] { false, true })
        {
            bundle.SetSlot(TestItem.AddressA, slot, delete ? default : value);
            using PbtPartitionBatches changes = bundle.PrepareLeafChanges();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(bundle.GetSlot(TestItem.AddressA, slot), Is.EqualTo(delete ? default : value));
                Assert.That(changes.Account?.ConsumeOperations().Length ?? 0, Is.EqualTo(slot < 64 ? 1 : 0));
                Assert.That(changes.Storage?.ConsumeOperations().Length ?? 0, Is.EqualTo(slot < 64 ? 0 : 1));
                Assert.That(changes.Code?.ConsumeOperations().Length ?? 0, Is.Zero);
            }
            bundle.CompleteLeafChanges();
        }
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void Trie_updates_leave_independently_staged_flat_entries_unchanged(bool leafExists, bool delete)
    {
        PbtVariableTreeKey key = PbtTestLeaves.SlotKey(TestItem.AddressA, 1);
        ValueHash256 flatValue = new(Value(9));
        using PbtSnapshotBundle bundle = CreateBundle(new Reader(key, null));
        ValueHash256 root;
        using (PbtSnapshotStore store = new(bundle)) root = store.Fold(default, leafExists ? [(key.Bytes.ToArray(), Value(1))] : []);
        bundle.SetSlot(TestItem.AddressA, 1, new UInt256(flatValue.Bytes, isBigEndian: true));

        ValueHash256 updatedRoot;
        using (PbtSnapshotStore store = new(bundle)) updatedRoot = store.Fold(root, [(key.Bytes.ToArray(), delete ? null : Value(2))]);

        using PbtSnapshotStore reader = new(bundle);
        PbtTraversalPath rootPath = new(Span<byte>.Empty);
        byte[]? encoding = null;
        if (reader.TryGetNodeGroupRecord(reader.RootGroupPointer, rootPath, out PbtGroupRecord record))
        {
            using GroupFrameReader<PbtVariableTreeKey, PbtStorageNodePath> frame = new(record);
            encoding = frame.GetEncoding(PbtThreeLevelGroupGeometry.RootPosition).ToArray();
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.GetSlot(TestItem.AddressA, 1), Is.EqualTo(new UInt256(flatValue.Bytes, isBigEndian: true)));
            Assert.That(updatedRoot, Is.EqualTo(delete ? default : PbtTreeHarness.HashLeaf(key.Bytes, Value(2))));
            Assert.That(encoding, Is.EqualTo(delete ? null : PbtTreeHarness.EncodeLeaf(key)));
        }
    }

    [TestCase(0, false)]
    [TestCase(1, false)]
    [TestCase(2, false)]
    [TestCase(3, false)]
    [TestCase(1, true)]
    [TestCase(2, true)]
    [TestCase(3, true)]
    public void Node_group_newest_tree_replacement_stops_fallback_and_an_emptied_tree_falls_through(int newestTier, bool tombstone)
    {
        TrackingMemoryProvider memoryProvider = new();
        PbtNodePath groupKey = new([], 0);
        PbtStorageNodePath wideGroupKey = new([], 0);
        PbtResourcePool pool = new(new PbtConfig(), nodeGroupMemory: memoryProvider);
        byte[] persisted = PbtNodeGroupEncoder.Encode(groupKey, [new PbtNodeRecord(groupKey.ToPath<PbtStorageNodePath>(), BranchEncoding(1)),
            new PbtNodeRecord(PbtTestPaths.PathOf(groupKey, 6).ToPath<PbtStorageNodePath>(), BranchEncoding(2))], default);
        byte[] shared = PbtNodeGroupEncoder.Encode(groupKey, [new PbtNodeRecord(groupKey.ToPath<PbtStorageNodePath>(), BranchEncoding(3))], default);
        byte[] local = PbtNodeGroupEncoder.Encode(groupKey, [new PbtNodeRecord(groupKey.ToPath<PbtStorageNodePath>(), BranchEncoding(4))], default);
        byte[] write = PbtNodeGroupEncoder.Encode(groupKey, [new PbtNodeRecord(groupKey.ToPath<PbtStorageNodePath>(), BranchEncoding(5))], default);
        // A non-empty persisted root that the buffer does not index, so only the newest in-memory tree can answer.
        Reader reader = new(new PbtVariableTreeKey([0]), null) { GroupPayload = persisted, CurrentRoot = TestItem.KeccakA.ValueHash256 };
        PbtSnapshotPooledList sharedSnapshots = newestTier >= 1
            ? PbtSnapshotBundleTestExtensions.Chain(pool, Content(groupKey, persisted, pool), Content(wideGroupKey, newestTier == 1 && tombstone ? null : shared, pool))
            : new(0);
        PbtSnapshotPooledList localSnapshots = newestTier >= 2
            ? PbtSnapshotBundleTestExtensions.Chain(pool, Content(groupKey, shared, pool), Content(wideGroupKey, newestTier == 2 && tombstone ? null : local, pool))
            : new(0);
        using PbtSnapshotBundle bundle = new(localSnapshots, new PbtReadOnlySnapshotBundle(sharedSnapshots, reader, recordDetailedMetrics: false, new PbtPersistedSnapshotList(0), PbtSnapshotBundleTestExtensions.NoNodeGroupPin, slotFilterBitsPerKey: 0), PbtSnapshotTestExtensions.DefaultGroupRecordBuffer, pool, PbtResourcePool.Usage.MainBlockProcessing, filterInMemorySlotReads: false);
        if (newestTier == 3) PublishRoot(bundle, tombstone ? null : write);

        int answeringTier = tombstone ? newestTier - 1 : newestTier;
        bool snapshotFound = bundle.TryGetSnapshotNodeGroup(wideGroupKey, out RefCountingMemory? snapshotPayload);
        using (snapshotPayload)
        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshotFound, Is.EqualTo(answeringTier > 0));
            byte[]? expectedSnapshot = answeringTier switch { 0 => null, 1 => shared, 2 => local, _ => write };
            Assert.That(snapshotPayload?.Memory.ToArray(), Is.EqualTo(expectedSnapshot));
            Assert.That(reader.GroupReadCount, Is.Zero);
        }

        using (RefCountingMemory? actual = bundle.GetNodeGroup(wideGroupKey))
        using (Assert.EnterMultipleScope())
        {
            byte[]? expected = answeringTier switch { 0 => persisted, 1 => shared, 2 => local, _ => write };
            Assert.That(actual?.Memory.ToArray(), Is.EqualTo(expected));
            Assert.That(reader.GroupReadCount, Is.EqualTo(answeringTier == 0 ? 1 : 0));
        }
        bundle.Dispose();
        Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.Zero);
    }

    [Test]
    public void Snapshot_group_resolution_honors_persisted_and_newer_memory_trees([Values] bool tombstone, [Values] bool newerMemory)
    {
        using PbtPersistedTestStore store = new();
        using PbtSnapshotRepository repository = new(new MetricsConfig(), NullSnapshotCatalog.Instance, new PbtPersistedPublicationGate());
        PbtResourcePool pool = new(new PbtConfig());
        PbtStorageNodePath path = new([], 0);
        byte[] persistedEncoding = PbtNodeGroupEncoder.Encode(path, [new PbtNodeRecord(path, BranchEncoding(1))], default);
        byte[] memoryEncoding = PbtNodeGroupEncoder.Encode(path, [new PbtNodeRecord(path, BranchEncoding(2))], default);
        using PbtSnapshot source = new(StateId.PreGenesis, new StateId(0, default), TestItem.KeccakA.ValueHash256,
            Content(path, !newerMemory && tombstone ? null : persistedEncoding), pool, PbtResourcePool.Usage.MainBlockProcessing);
        using PbtPersistedSnapshot persisted = store.Build(source);
        repository.TryAddPersisted(persisted);
        StateId head = persisted.To;
        if (newerMemory)
        {
            head = new StateId(1, default);
            repository.TryAdd(new PbtSnapshot(persisted.To, head, TestItem.KeccakB.ValueHash256,
                Content(path, tombstone ? null : memoryEncoding), pool, PbtResourcePool.Usage.MainBlockProcessing));
        }
        Reader reader = new(default, null) { GroupPayload = persistedEncoding };
        using PbtReadOnlySnapshotBundle bundle = PbtSnapshotBundleTestExtensions.ReadOnlyBundle(repository.TryLeaseReadChain(head, StateId.PreGenesis), reader, slotFilterBitsPerKey: 0);
        bool found = bundle.TryGetSnapshotNodeGroup(path, out RefCountingMemory? payload);
        using (payload)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(found, Is.EqualTo(newerMemory || !tombstone));
                Assert.That(payload?.Memory.ToArray(), Is.EqualTo(!tombstone && newerMemory ? memoryEncoding : newerMemory || !tombstone ? persistedEncoding : null));
                Assert.That(reader.GroupReadCount, Is.Zero);
            }
        }
        PbtStorageNodePath missing = new([0, 0], 9);
        bool missingFound = bundle.TryGetSnapshotNodeGroup(missing, out RefCountingMemory? missingPayload);
        using (missingPayload)
        using (Assert.EnterMultipleScope())
        {
            Assert.That(missingFound, Is.False);
            Assert.That(missingPayload, Is.Null);
            Assert.That(reader.GroupReadCount, Is.Zero);
        }
    }

    [Test]
    public void Failed_group_read_during_fold_leaves_the_write_buffer_unchanged([Values] bool malformed)
    {
        TrackingMemoryProvider memoryProvider = new();
        PbtVariableTreeKey originalLeafKey = PbtTestLeaves.SlotKey(TestItem.AddressA, 1);
        ValueHash256 originalLeafValue = new(Value(2));
        Reader reader = new(new PbtVariableTreeKey([0]), null)
        {
            GroupPayload = malformed ? Bytes.FromHexString("01") : null,
            GroupReadException = malformed ? null : new InvalidDataException("Configured group read failure."),
            MemoryProvider = memoryProvider,
            CurrentRoot = new ValueHash256(Value(5)),
        };
        using PbtSnapshotBundle bundle = CreateBundle(reader);
        bundle.SetSlot(TestItem.AddressA, 1, new UInt256(originalLeafValue.Bytes, isBigEndian: true));

        CountingStore store;
        using (PbtSnapshotStore snapshotStore = new(bundle))
        {
            store = new(snapshotStore);
            Action fold = () => store.Fold(new ValueHash256(Value(5)), [(PbtStoreTestExtensions.ZoneKey("0003"), Value(4))]);
            if (malformed) Assert.Catch(fold);
            else Assert.Throws<InvalidDataException>(fold);
        }

        bundle.CompleteLeafChanges();
        using PbtSnapshot snapshot = bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), default);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.GroupReadCount, Is.EqualTo(1));
            Assert.That(store.ApplyCount, Is.Zero);
            Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.Zero);
            Assert.That(snapshot.Content.HeaderStorages, Has.Count.EqualTo(1));
            Assert.That(snapshot.Content.GetSlot(originalLeafKey), Is.EqualTo(new UInt256(originalLeafValue.Bytes, isBigEndian: true)));
            Assert.That(snapshot.Content.NodeGroupCount, Is.Zero, "a failed fold publishes no groups");
            Assert.That(snapshot.RootGroupPointer, Is.Zero);
        }
    }

    [Test]
    public void Code_bearing_account_matches_pinned_eip_root()
    {
        using PbtSnapshotBundle bundle = CreateBundle(new Reader(default, null));
        Address address = new("0x0000000000000000000000000000000000000001");
        byte[] bytes = Bytes.FromHexString("6001");
        Account account = Build.An.Account.WithNonce(1).WithBalance(2).WithCode(bytes).TestObject;
        SetAccountWithCode(bundle, address, account, new CodeInfo(bytes), codeFirst: false);
        // EIP-8297 at d2a64c2d: literal Python mapping and merkelization, using BLAKE3.
        ValueHash256 expected = new("0x1d6376e73eb20356030d5335b0297c6d29b1222e9522450c33406126f6f9f5ee");
        Assert.That(bundle.Fold(default), Is.EqualTo(expected));
    }

    [Test]
    public void Whole_account_and_code_survive_fold_and_seal_in_either_write_order([Values] bool codeFirst, [Values(0ul, 9ul)] ulong nonce)
    {
        using PbtSnapshotBundle bundle = CreateBundle(new Reader(default, null));
        byte[] bytes = new byte[160];
        Bytes.FromHexString("6001600055").CopyTo(bytes, 0);
        CodeInfo code = new(bytes);
        Account account = Build.An.Account.WithNonce(nonce).WithBalance(nonce).WithStorageRoot(TestItem.KeccakB).WithCode(bytes).TestObject;
        // PBT keeps no storage root, so the account reads back over the empty tree.
        Account stored = account.WithChangedStorageRoot(Keccak.EmptyTreeHash);
        SetAccountWithCode(bundle, TestItem.AddressA, account, code, codeFirst);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.GetAccount(TestItem.AddressA), Is.EqualTo(stored));
            Assert.That(bundle.GetCode(account.CodeHash.ValueHash256), Is.SameAs(code));
        }
        ValueHash256 root = bundle.Fold(default);
        Dictionary<string, byte[]> model = [];
        PbtReferenceModel.SetAccount(model, TestItem.AddressA, nonce, nonce, bytes);
        Assert.That(root, Is.EqualTo(PbtReferenceModel.Root(model)));
        Assert.That(bundle.Fold(root), Is.EqualTo(root));
        using PbtSnapshot snapshot = bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), root);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.GetAccount(TestItem.AddressA), Is.EqualTo(stored));
            Assert.That(bundle.GetCode(account.CodeHash.ValueHash256), Is.SameAs(code));
            Assert.That(snapshot.Content.Accounts[Eip8297KeyDerivation.AddressHash(TestItem.AddressA.ToHash().Bytes)]?.ToAccount(), Is.EqualTo(stored));
            Assert.That(snapshot.Content.Codes[account.CodeHash.ValueHash256], Is.SameAs(code));
        }
        Account rebalanced = account.WithChangedBalance(nonce + 1);
        bundle.SetAccount(TestItem.AddressA, rebalanced);
        int pendingAfterBalanceChange = bundle.PendingMutationCount;
        root = bundle.Fold(root);
        PbtReferenceModel.SetAccount(model, TestItem.AddressA, nonce, nonce + 1, bytes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pendingAfterBalanceChange, Is.EqualTo(3), "a balance change stages header leaves only, never code chunks");
            Assert.That(root, Is.EqualTo(PbtReferenceModel.Root(model)));
        }
    }

    [Test]
    public void Concurrent_slot_account_and_code_writes_are_all_kept()
    {
        const int WritesPerSlot = 2000;
        const int AccountCount = 64;
        using PbtSnapshotBundle bundle = CreateBundle(new Reader(default, null));
        static byte[] CodeOf(int index) => [0x60, (byte)index];
        static Account AccountOf(int index) => Build.An.Account.WithNonce((ulong)index + 1).WithCode(CodeOf(index)).TestObject;

        System.Threading.Tasks.Parallel.Invoke(
            // Every slot of one run has its own writer, so a replacement built from a stale run would undo another slot's write.
            () => System.Threading.Tasks.Parallel.For(0, SlotRun.Width, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = SlotRun.Width }, slot =>
            {
                for (uint write = 1; write <= WritesPerSlot; write++)
                {
                    UInt256 value = write;
                    bundle.SetSlot(TestItem.AddressA, (UInt256)(uint)slot, value);
                }
            }),
            () => System.Threading.Tasks.Parallel.For(0, AccountCount, index => bundle.SetAccount(TestItem.Addresses[index], AccountOf(index))),
            () => System.Threading.Tasks.Parallel.For(0, AccountCount, index =>
                bundle.SetCode(AccountOf(index).CodeHash.ValueHash256, new CodeInfo(CodeOf(index)))));

        Dictionary<string, byte[]> model = [];
        for (uint slot = 0; slot < SlotRun.Width; slot++) PbtReferenceModel.SetSlot(model, TestItem.AddressA, slot, WritesPerSlot);
        for (int index = 0; index < AccountCount; index++) PbtReferenceModel.SetAccount(model, TestItem.Addresses[index], (ulong)index + 1, 0, CodeOf(index));
        UInt256 lastWrite = WritesPerSlot;
        using (Assert.EnterMultipleScope())
        {
            for (uint slot = 0; slot < SlotRun.Width; slot++)
                Assert.That(bundle.GetSlot(TestItem.AddressA, slot), Is.EqualTo(lastWrite), $"slot {slot}");
            Assert.That(bundle.Fold(default), Is.EqualTo(PbtReferenceModel.Root(model)));
        }
    }

    [Test]
    public void Slot_writes_applied_together_keep_the_last_write_of_each_slot([Values] bool interleaveRuns)
    {
        // Slots 3, 5 and 6 share a header run and 64 and 70 a storage run; 3 is written twice and 70 is set, then cleared.
        (uint Slot, uint Value)[] writes = interleaveRuns
            ? [(3, 1), (64, 5), (5, 2), (70, 6), (3, 3), (1000, 7), (6, 4), (70, 0)]
            : [(3, 1), (3, 3), (5, 2), (6, 4), (64, 5), (70, 6), (70, 0), (1000, 7)];
        (uint Slot, uint Value)[] expected = [(3, 3), (5, 2), (6, 4), (64, 5), (70, 0), (1000, 7)];
        using PbtSnapshotBundle bundle = CreateBundle(new Reader(default, null));
        (UInt256 Slot, UInt256 Value)[] slotWrites = Array.ConvertAll(writes, write => ((UInt256)write.Slot, (UInt256)write.Value));
        bundle.SetSlots(TestItem.AddressA, Eip8297KeyDerivation.AddressHash(TestItem.AddressA.ToHash().Bytes), slotWrites);

        Dictionary<string, byte[]> model = [];
        foreach ((uint slot, uint value) in expected)
            if (value != 0) PbtReferenceModel.SetSlot(model, TestItem.AddressA, slot, value);
        using (Assert.EnterMultipleScope())
        {
            foreach ((uint slot, uint value) in expected)
                Assert.That(bundle.GetSlot(TestItem.AddressA, slot), Is.EqualTo((UInt256)value), $"slot {slot}");
            Assert.That(bundle.Fold(default), Is.EqualTo(PbtReferenceModel.Root(model)));
        }
    }

    [Test]
    public void Shared_code_is_reapplied_for_each_holder_across_folds([Values] bool reverseOrder, [Values(1, 129, 258)] int chunkCount)
    {
        using PbtSnapshotBundle bundle = CreateBundle(new Reader(default, null));
        byte[] bytes = new byte[chunkCount * 31];
        bytes.AsSpan().Fill(0x5b);
        CodeInfo code = new(bytes);
        Account account = Build.An.Account.WithCode(bytes).TestObject;
        SetAccountWithCode(bundle, TestItem.AddressA, account, code, codeFirst: !reverseOrder);
        ValueHash256 root = bundle.Fold(default);
        using PbtSnapshot original = bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), root);
        bundle.SetAccount(TestItem.AddressB, account);
        root = bundle.Fold(root);
        Dictionary<string, byte[]> model = [];
        PbtReferenceModel.SetAccount(model, TestItem.AddressA, account.Nonce, account.Balance, bytes);
        PbtReferenceModel.SetAccount(model, TestItem.AddressB, account.Nonce, account.Balance, bytes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root, Is.EqualTo(PbtReferenceModel.Root(model)));
            Assert.That(original.Content.Codes[account.CodeHash.ValueHash256], Is.SameAs(code));
            Assert.That(bundle.GetCode(account.CodeHash.ValueHash256), Is.SameAs(code));
        }
    }

    private static readonly byte[] RealCode = Bytes.FromHexString("6001");
    private static readonly byte[] OtherRealCode = Bytes.FromHexString("6002");
    private static readonly byte[] Delegation = Bytes.FromHexString("ef01000000000000000000000000000000000000000001");

    public static IEnumerable<TestCaseData> CodeHashTransitionCases()
    {
        yield return new TestCaseData(RealCode, OtherRealCode, typeof(InvalidOperationException)).SetName("Real_to_other_real_throws");
        yield return new TestCaseData(RealCode, null, typeof(InvalidOperationException)).SetName("Real_to_deleted_throws");
        yield return new TestCaseData(RealCode, Delegation, typeof(InvalidOperationException)).SetName("Real_to_delegation_throws");
        yield return new TestCaseData(RealCode, RealCode, null).SetName("Real_to_same_real_is_allowed");
        yield return new TestCaseData(Array.Empty<byte>(), RealCode, null).SetName("Empty_to_real_is_allowed");
    }

    [TestCaseSource(nameof(CodeHashTransitionCases))]
    public void Code_hash_transitions_without_a_reference_count(byte[] previousCode, byte[]? nextCode, Type? expectedException)
    {
        using PbtSnapshotBundle bundle = CreateBundle(new Reader(default, null));
        Account previous = Build.An.Account.WithNonce(1).WithCode(previousCode).TestObject;
        Account? next = nextCode is null ? null : Build.An.Account.WithNonce(2).WithCode(nextCode).TestObject;
        Set(previous, previousCode);
        ValueHash256 root = bundle.Fold(default);
        if (expectedException is not null)
        {
            Assert.Throws(expectedException, () => Set(next, nextCode));
            return;
        }
        Set(next, nextCode);
        Dictionary<string, byte[]> model = [];
        if (next is not null) PbtReferenceModel.SetAccount(model, TestItem.AddressA, next.Nonce, next.Balance, nextCode);
        Assert.That(bundle.Fold(root), Is.EqualTo(PbtReferenceModel.Root(model)));

        void Set(Account? account, byte[]? code) =>
            SetAccountWithCode(bundle, TestItem.AddressA, account, code is null ? null : new CodeInfo(code), codeFirst: true);
    }

    [Test]
    public void Replacing_unknown_previous_code_throws()
    {
        using PbtSnapshotBundle bundle = CreateBundle(new Reader(default, null));
        bundle.SetAccount(TestItem.AddressA, Build.An.Account.WithCode(RealCode).TestObject);
        Assert.Throws<InvalidDataException>(() => bundle.SetAccount(TestItem.AddressA, Build.An.Account.WithCode(OtherRealCode).TestObject));
    }

    [Test]
    public void Delegation_header_matches_eip_preimages()
    {
        Account account = Build.An.Account.WithCode(Delegation).TestObject;
        ValueHash256 addressHash = Eip8297KeyDerivation.AddressHash(TestItem.AddressA.ToHash().Bytes);
        Dictionary<PbtPath, ValueHash256> expected = new()
        {
            [new PbtPath([0, .. addressHash.Bytes, 0])] = new(Bytes.FromHexString("0000000000000017000000000000000000000000000000000000000000000000")),
            [new PbtPath([0, .. addressHash.Bytes, 2])] = new([.. Delegation, .. new byte[9]]),
        };
        using ArrayPoolList<KeyValuePair<PbtPath, ValueHash256>> accountLeaves = PbtStateKey.AccountLeaves(addressHash, account, new CodeInfo(Delegation));
        Assert.That(accountLeaves, Is.EquivalentTo(expected));
    }

    [Test]
    public void Delegation_transitions_match_reference_leaves([Values] bool codeFirst, [Values] bool foldEachChange)
    {
        using PbtSnapshotBundle bundle = CreateBundle(new Reader(default, null));
        byte[] secondDelegation = Bytes.FromHexString("ef01000000000000000000000000000000000000000002");
        ValueHash256 root = default;
        Dictionary<Address, byte[]> accounts = [];
        Set(TestItem.AddressA, Delegation);
        Set(TestItem.AddressB, Delegation);
        Set(TestItem.AddressA, secondDelegation);
        Set(TestItem.AddressA, []);
        Set(TestItem.AddressA, Delegation);
        Set(TestItem.AddressA, null);
        Set(TestItem.AddressB, null);
        Assert.That(bundle.GetAccount(TestItem.AddressB), Is.Null);
        Assert.That(bundle.Fold(root), Is.EqualTo(default(ValueHash256)));

        void Set(Address address, byte[]? bytes)
        {
            Account? account = bytes is null ? null : Build.An.Account.WithNonce(1).WithCode(bytes).TestObject;
            SetAccountWithCode(bundle, address, account, bytes is null ? null : new CodeInfo(bytes), codeFirst);
            if (bytes is null) accounts.Remove(address);
            else accounts[address] = bytes;
            Dictionary<string, byte[]> model = [];
            foreach ((Address owner, byte[] ownerCode) in accounts) PbtReferenceModel.SetAccount(model, owner, 1, 0, ownerCode);
            if (foldEachChange)
            {
                root = bundle.Fold(root);
                Assert.That(root, Is.EqualTo(PbtReferenceModel.Root(model)), "incremental root");
            }
        }
    }

    [Test]
    public void Code_resolution_preserves_other_pending_accounts_and_survives_scratch_reuse([Values(0, 1, 40)] int accountCount)
    {
        TrackingBuilderPool pool = new();
        using PbtSnapshotBundle bundle = PbtSnapshotBundleTestExtensions.CreateBundle(pool, new Reader(default, null));
        byte[] bytes = Bytes.FromHexString("6001600055");
        byte[] otherBytes = Bytes.FromHexString("6002600055");
        Account account = Build.An.Account.WithCode(bytes).TestObject;
        Account otherAccount = Build.An.Account.WithCode(otherBytes).TestObject;
        bundle.SetAccount(TestItem.AddressA, otherAccount);
        Dictionary<string, byte[]> model = [];
        PbtReferenceModel.SetAccount(model, TestItem.AddressA, 0, 0, otherBytes);
        for (int index = 0; index < accountCount; index++)
        {
            byte[] addressBytes = new byte[Address.Size];
            addressBytes[^1] = (byte)index;
            Address address = new(addressBytes);
            bundle.SetAccount(address, account);
            PbtReferenceModel.SetAccount(model, address, 0, 0, bytes);
        }

        bundle.SetCode(account.CodeHash.ValueHash256, new CodeInfo(bytes));
        Assert.Throws<InvalidDataException>(() => bundle.Fold(default));
        bundle.SetCode(otherAccount.CodeHash.ValueHash256, new CodeInfo(otherBytes));
        ValueHash256 root = bundle.Fold(default);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root, Is.EqualTo(PbtReferenceModel.Root(model)));
            Assert.That(bundle.PendingMutationCount, Is.Zero);
        }
    }

    [Test]
    public void Persisted_code_is_read_once_per_bundle_and_never_snapshotted()
    {
        Reader reader = new(default, null);
        using PbtSnapshotBundle bundle = CreateBundle(reader);
        byte[] bytes = Bytes.FromHexString("6001600055");
        Account account = Build.An.Account.WithBalance(1).WithCode(bytes).TestObject;
        ValueHash256 codeHash = account.CodeHash.ValueHash256;
        CodeInfo persisted = new(bytes);
        reader.Codes[codeHash] = persisted;
        bundle.SetAccount(TestItem.AddressA, account);
        ValueHash256 root = bundle.Fold(default);
        using PbtSnapshot first = bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), root);
        Account updated = account.WithChangedBalance(2);
        bundle.SetAccount(TestItem.AddressA, updated);
        int pendingAfterBalanceChange = bundle.PendingMutationCount;
        root = bundle.Fold(root);
        using PbtSnapshot second = bundle.CollectSnapshot(new StateId(1, default), new StateId(2, default), root);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pendingAfterBalanceChange, Is.EqualTo(3), "an account write stages header leaves only, never code chunks");
            Assert.That(reader.CodeReadCount, Is.EqualTo(1), "persisted code is memoized per bundle");
            Assert.That(bundle.GetCode(codeHash), Is.SameAs(persisted));
            Assert.That(first.Content.Codes, Is.Empty, "memoized code must not be snapshotted");
            Assert.That(second.Content.Codes, Is.Empty, "memoized code must not be snapshotted");
        }
    }

    [Test]
    public void Hinted_account_serves_the_write_path_without_a_persistence_read_and_never_shadows_a_write()
    {
        Account persisted = Build.An.Account.WithBalance(1).TestObject;
        Reader reader = new(default, null) { Accounts = [new(Eip8297KeyDerivation.AddressHash(TestItem.AddressA.ToHash().Bytes), persisted)] };
        using PbtSnapshotBundle bundle = CreateBundle(reader);
        bundle.HintAccount(TestItem.AddressA, persisted);
        Account written = persisted.WithChangedBalance(2);
        bundle.SetAccount(TestItem.AddressA, written);
        bundle.HintAccount(TestItem.AddressA, persisted);
        bundle.HintAccount(TestItem.AddressB, null);
        Account? hintedA = bundle.GetAccount(TestItem.AddressA);
        Account? hintedB = bundle.GetAccount(TestItem.AddressB);
        int readsBeforeCollect = reader.AccountReadCount;
        using PbtSnapshot snapshot = bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), bundle.Fold(default));
        bundle.GetAccount(TestItem.AddressB);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(readsBeforeCollect, Is.Zero, "hinted accounts are not read from persistence");
            Assert.That(hintedA, Is.EqualTo(written), "a later hint never shadows a write");
            Assert.That(hintedB, Is.Null, "a null hint is served as an absent account");
            Assert.That(reader.AccountReadCount, Is.EqualTo(1), "hints are dropped with the write buffer");
        }
    }

    [Test]
    public void Failed_partition_fold_keeps_mutations_pending_without_completing_snapshot([Values(0x00, 0x01, 0xFF)] int failedZone)
    {
        using PbtGroupRecordBuffer buffer = new(PooledRefCountingMemoryProvider.Instance, compressedMaxMemory: 0, liveGenerations: 0);
        using PbtSnapshotBundle bundle = PbtSnapshotBundleTestExtensions.CreateBundle(new PbtResourcePool(new PbtConfig()), new Reader(default, null), buffer);
        byte[] bytes = new byte[(256 + 2) * 31];
        bytes.AsSpan().Fill(0x5b);
        Account account = Build.An.Account.WithBalance(3).WithCode(bytes).TestObject;
        SetAccountWithCode(bundle, TestItem.AddressA, account, new CodeInfo(bytes), codeFirst: true);
        bundle.SetSlot(TestItem.AddressA, 1000, (UInt256)0x01);
        // Slots of other accounts store a group below the storage zone boundary, so the failing zone publishes one.
        bundle.SetSlot(TestItem.AddressA, 2000, (UInt256)0x01);
        bundle.SetSlot(TestItem.AddressC, 1000, (UInt256)0x01);
        bundle.SetSlot(TestItem.AddressD, 1000, (UInt256)0x01);
        ValueHash256 root = bundle.Fold(default);
        using PbtSnapshot original = bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), root);

        Account replacement = account.WithChangedBalance(4);
        bundle.SetAccount(TestItem.AddressA, replacement);
        bundle.SetSlot(TestItem.AddressA, 1000, (UInt256)0x02);
        // A balance change stages no code chunk, so a new contract keeps the code partition in the batch.
        byte[] otherBytes = Bytes.FromHexString("6002600055");
        Account other = Build.An.Account.WithCode(otherBytes).TestObject;
        SetAccountWithCode(bundle, TestItem.AddressB, other, new CodeInfo(otherBytes), codeFirst: true);
        CountingStore store;
        long initialBufferBytes = buffer.UsedBytes;
        using (PbtSnapshotStore snapshotStore = new(bundle))
        {
            store = new(snapshotStore) { FailedZone = failedZone };
            Assert.Throws<InvalidDataException>(() => TrieUpdater.UpdateRoot(store, root, bundle.PrepareLeafChanges(), new ConcurrencyController(1), PbtTreeHarness.DefaultFanOut, null, PooledRefCountingMemoryProvider.Instance));
            if (failedZone == 0xFF)
            {
                Assert.That(store.ApplyCount, Is.GreaterThan(0));
                Assert.That(buffer.UsedBytes, Is.GreaterThan(initialBufferBytes), "the failure followed private buffer writes");
            }
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.TreeRoot, Is.EqualTo(root));
            Assert.That(bundle.RootGroupPointer, Is.EqualTo(original.RootGroupPointer), "failed folds publish no pointers");
            Assert.That(bundle.GetAccount(TestItem.AddressA), Is.EqualTo(replacement));
            Assert.Throws<InvalidOperationException>(() => bundle.CollectSnapshot(new StateId(1, default), new StateId(2, default), root));
            Assert.That(original.TreeRoot, Is.EqualTo(root));
            Assert.That(original.Content.Accounts[Eip8297KeyDerivation.AddressHash(TestItem.AddressA.ToHash().Bytes)]?.ToAccount(), Is.EqualTo(account));
        }
    }

    private static void SetAccountWithCode(PbtSnapshotBundle bundle, Address address, Account? account, CodeInfo? code, bool codeFirst)
    {
        bool hasCode = code is not null && code.Code.Length > 0;
        if (codeFirst && hasCode) bundle.SetCode(account!.CodeHash.ValueHash256, code!);
        bundle.SetAccount(address, account);
        if (!codeFirst && hasCode) bundle.SetCode(account!.CodeHash.ValueHash256, code!);
    }

    /// <summary>A layer whose tree holds only <paramref name="encoding"/> at <paramref name="groupKey"/>, from <paramref name="pool"/>'s buffer when one is given.</summary>
    private static PbtSnapshotContent Content<TPath>(TPath groupKey, byte[]? encoding, PbtResourcePool? pool = null)
        where TPath : struct, IPbtNodePath<TPath>
    {
        PbtSnapshotContent content = pool?.GetSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing) ?? new();
        content.SetTree((groupKey.ToPath<PbtStorageNodePath>(), encoding));
        return content;
    }

    /// <summary>Publishes a root group holding <paramref name="encoding"/>, or an emptied tree, as the bundle's current fold result.</summary>
    private static void PublishRoot(PbtSnapshotBundle bundle, byte[]? encoding)
    {
        ulong root = encoding is null ? 0 : bundle.GroupRecordBuffer.Append(default, encoding, default);
        bundle.PublishNodeGroups(root, encoding is null ? 0 : 1, encoding?.Length ?? 0);
    }

    private static RefCountingMemory Memory(byte[] encoding, IRefCountingMemoryProvider? memoryProvider = null)
    {
        RefCountingMemory payload = (memoryProvider ?? PooledRefCountingMemoryProvider.Instance).Rent(encoding.Length);
        encoding.CopyTo(payload.GetSpan());
        return payload;
    }

    private static byte[] BranchEncoding(byte marker)
    {
        ValueHash256 left = new(Value(marker));
        ValueHash256 right = new(Value((byte)(marker + 32)));
        return PbtTreeHarness.EncodeBranch([], 0, left, right);
    }

    private sealed class Reader(PbtVariableTreeKey key, ValueHash256? value) : IPbtPersistence.IReader
    {
        public IEnumerable<KeyValuePair<ValueHash256, Account>> Accounts { get; init; } = [];
        public PbtNodePath GroupKey { get; set; } = new([], 0);
        public byte[]? GroupPayload { get; set; }
        public IRefCountingMemoryProvider MemoryProvider { get; set; } = PooledRefCountingMemoryProvider.Instance;
        public Exception? GroupReadException { get; set; }
        public int GroupReadCount { get; private set; }
        public Dictionary<ValueHash256, CodeInfo> Codes { get; } = [];
        public int CodeReadCount { get; private set; }
        public int AccountReadCount { get; private set; }
        public StateId CurrentState => StateId.PreGenesis;
        public ValueHash256 CurrentRoot { get; set; }
        public PbtAccount? GetAccount(in ValueHash256 addressHash)
        {
            AccountReadCount++;
            foreach ((ValueHash256 hash, Account account) in Accounts)
                if (hash == addressHash) return ToPbtAccount(account);
            return null;
        }
        private PbtAccount ToPbtAccount(Account account) => PbtAccount.From(account, account.HasCode ? Codes[account.CodeHash.ValueHash256] : null);
        public PackedSlotRun GetSlotRun<TKey>(in TKey runKey) where TKey : struct, IPbtKey<TKey>
        {
            PackedSlotRun run = SlotRun.Empty;
            if (value is { } word && SlotRun.RunKey(key).Bytes.SequenceEqual(runKey.Bytes))
            {
                PackedSlotRun previous = run;
                run = run.With(SlotRun.IndexOf(key), new UInt256(word.Bytes, isBigEndian: true));
                SlotRun.Return(previous);
            }
            return run;
        }
        public CodeInfo? GetCode(in ValueHash256 codeHash)
        {
            CodeReadCount++;
            return Codes.GetValueOrDefault(codeHash);
        }
        public RefCountingMemory? GetNodeGroup<TPath>(TPath groupKey) where TPath : struct, IPbtNodePath<TPath>
        {
            GroupReadCount++;
            if (GroupReadException is not null) throw GroupReadException;
            if (!PbtNodePathOperations.Equal(groupKey, GroupKey) || GroupPayload is null) return null;
            RefCountingMemory memory = MemoryProvider.Rent(GroupPayload.Length);
            GroupPayload.CopyTo(memory.GetSpan());
            return memory;
        }
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }

    private sealed class CountingStore(IPbtStore store) : IPbtStore
    {
        private int _directReads;
        private int _snapshotReads;
        private int _applyCount;
        public int DirectReads => _directReads;
        public int SnapshotReads => _snapshotReads;
        public int ApplyCount => _applyCount;
        public int? FailedZone { get; init; }
        public ulong RootGroupPointer => store.RootGroupPointer;
        public IPbtConcurrentWriter CreateWriter() => new Writer(this, store.CreateWriter());
        public void CompleteFold() => store.CompleteFold();
        public bool TryGetNodeGroupRecord(ulong pointer, scoped in PbtTraversalPath path, out PbtGroupRecord record)
        {
            bool found = store.TryGetNodeGroupRecord(pointer, path, out record);
            if (found) System.Threading.Interlocked.Increment(ref _directReads);
            return found;
        }
        public RefCountingMemory? GetNodeGroup(scoped in PbtTraversalPath path)
        {
            System.Threading.Interlocked.Increment(ref _snapshotReads);
            return store.GetNodeGroup(path);
        }

        private sealed class Writer(CountingStore owner, IPbtConcurrentWriter writer) : IPbtConcurrentWriter
        {
            public bool TryReserveNodeGroup(scoped in PbtTraversalPath path, int length, in PbtGroupLinks links, out ulong pointer, out Span<byte> payload)
            {
                if (path.BitDepth >= 9 && path.ToPath<PbtStorageNodePath>().GetByte(0) == owner.FailedZone)
                    throw new InvalidDataException("Configured partition write failure.");
                bool reserved = writer.TryReserveNodeGroup(path, length, links, out pointer, out payload);
                System.Threading.Interlocked.Increment(ref owner._applyCount);
                return reserved;
            }
            public void SetNodeGroup(scoped in PbtTraversalPath path, RefCountingMemory? payload) => writer.SetNodeGroup(path, payload);
            public void Dispose() => writer.Dispose();
        }
    }
}
