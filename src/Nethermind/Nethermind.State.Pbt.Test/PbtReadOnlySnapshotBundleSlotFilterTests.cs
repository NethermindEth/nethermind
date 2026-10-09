// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Memory;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Monitoring.Config;
using Nethermind.Pbt;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Pbt.Common;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.PersistedSnapshots;
using Nethermind.State.Pbt.ScopeProvider;
using Nethermind.State.Pbt.Snapshot;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtReadOnlySnapshotBundleSlotFilterTests
{
    private const double RealBitsPerKey = 14.0;

    // Header slots (below 64) and storage slots, several of them sharing a run.
    private static readonly UInt256[] Slots = [0, 1, 17, 63, 64, 65, 80, 1000, 1001, 5000];

    [Test]
    public void Random_stacks_read_the_same_through_the_filter([Range(0, 99)] int seed, [Values(1.0, RealBitsPerKey)] double bitsPerKey)
    {
        Random random = new(seed);
        Address[] addresses = [.. TestItem.Addresses.Take(4)];
        PbtSnapshotContent persisted = new();
        foreach (Address address in addresses)
            foreach (UInt256 slot in Slots)
                if (random.Next(3) == 0) persisted.SetSlot(PbtTestLeaves.SlotKey(address, slot), (UInt256)(ulong)random.Next(1, 1000));

        PbtSnapshotContent[] layers = new PbtSnapshotContent[random.Next(0, 7)];
        for (int layer = 0; layer < layers.Length; layer++)
        {
            PbtSnapshotContent content = layers[layer] = new PbtSnapshotContent();
            if (random.Next(4) == 0) content.ClearStorage(PbtStateKey.AddressKeyHash(addresses[random.Next(addresses.Length)]));
            for (int write = random.Next(0, 6); write > 0; write--)
            {
                UInt256 value = random.Next(3) == 0 ? UInt256.Zero : (UInt256)(ulong)random.Next(1, 1000);
                content.SetSlot(PbtTestLeaves.SlotKey(addresses[random.Next(addresses.Length)], Slots[random.Next(Slots.Length)]), value);
            }
        }

        using PbtReadOnlySnapshotBundle bundle = new(PbtSnapshotBundleTestExtensions.Chain(new PbtResourcePool(new PbtConfig()), layers),
            new ContentReader(persisted), recordDetailedMetrics: false, bitsPerKey);

        using (Assert.EnterMultipleScope())
        {
            foreach (Address address in addresses)
                foreach (UInt256 slot in Slots)
                    Assert.That(ReadRun(bundle, address, slot, filtered: true), Is.EqualTo(ReadRun(bundle, address, slot, filtered: false)), $"{address} slot {slot}");
            Assert.That(bundle.SlotFilter is not null, Is.EqualTo(layers.Length >= 2));
        }
    }

    [Test]
    public void Filter_is_built_only_with_bits_per_key_and_two_memory_layers([Values(0.0, RealBitsPerKey)] double bitsPerKey, [Values(1, 2)] int memoryLayers)
    {
        PbtSnapshotContent persisted = new();
        persisted.SetSlot(PbtTestLeaves.SlotKey(TestItem.AddressA, 1000), 7);
        PbtSnapshotContent[] layers = [.. Enumerable.Range(0, memoryLayers).Select(layer => LayerWriting(TestItem.Addresses[layer + 1], 1000, 1))];
        using PbtReadOnlySnapshotBundle bundle = new(PbtSnapshotBundleTestExtensions.Chain(new PbtResourcePool(new PbtConfig()), layers),
            new ContentReader(persisted), recordDetailedMetrics: false, bitsPerKey);
        bool expected = bitsPerKey > 0 && memoryLayers >= 2;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.MayFilterSlots, Is.EqualTo(expected));
            Assert.That(ReadRun(bundle, TestItem.AddressA, 1000, filtered: true)[SlotRun.IndexOf((UInt256)1000)], Is.EqualTo((UInt256)7));
            Assert.That(bundle.SlotFilter is not null, Is.EqualTo(expected));
        }
    }

    [Test]
    public void Definite_miss_honors_in_memory_clears_and_reads_retained_layers_below([Values] bool clearInMemory)
    {
        using PbtRetainedTestStore store = new();
        using PbtSnapshotRepository repository = new(new MetricsConfig());
        PbtResourcePool pool = new(new PbtConfig());
        ValueHash256 addressHash = PbtStateKey.AddressKeyHash(TestItem.AddressA);
        using PbtSnapshot source = new(StateId.PreGenesis, new StateId(0, default), default, LayerWriting(TestItem.AddressA, 1000, 7), pool, PbtResourcePool.Usage.MainBlockProcessing);
        using PbtRetainedSnapshot retained = store.Build(source);
        repository.TryAddRetained(retained);
        PbtSnapshotContent clearing = LayerWriting(TestItem.AddressB, 1000, 1);
        if (clearInMemory) clearing.ClearStorage(addressHash);
        repository.TryAdd(new PbtSnapshot(new StateId(0, default), new StateId(1, default), default, clearing, pool, PbtResourcePool.Usage.MainBlockProcessing));
        repository.TryAdd(new PbtSnapshot(new StateId(1, default), new StateId(2, default), default, LayerWriting(TestItem.AddressC, 1000, 2), pool, PbtResourcePool.Usage.MainBlockProcessing));

        using PbtReadOnlySnapshotBundle bundle = new(repository.TryLeaseReadChain(new StateId(2, default), StateId.PreGenesis)!,
            new ContentReader(new PbtSnapshotContent()), recordDetailedMetrics: false, RealBitsPerKey);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ReadRun(bundle, TestItem.AddressA, 1000, filtered: true)[SlotRun.IndexOf((UInt256)1000)], Is.EqualTo(clearInMemory ? UInt256.Zero : (UInt256)7));
            Assert.That(bundle.SlotFilter, Is.Not.Null);
        }
    }

    [Test]
    [NonParallelizable]
    public void A_filter_that_cannot_be_built_leaves_the_plain_loop_in_charge()
    {
        PbtSnapshotContent persisted = new();
        persisted.SetSlot(PbtTestLeaves.SlotKey(TestItem.AddressA, 5000), 22);
        long builds = Metrics.PbtInMemorySlotFilterBuilds;
        long failures = Metrics.PbtInMemorySlotFilterBuildFailures;

        // BloomFilter rejects a non-finite bits-per-key value, so the build throws for real.
        using PbtReadOnlySnapshotBundle bundle = new(
            PbtSnapshotBundleTestExtensions.Chain(new PbtResourcePool(new PbtConfig()), LayerWriting(TestItem.AddressA, 1000, 1), LayerWriting(TestItem.AddressA, 1000, 2)),
            new ContentReader(persisted), recordDetailedMetrics: false, double.PositiveInfinity);
        UInt256 hit = ReadRun(bundle, TestItem.AddressA, 1000, filtered: true)[SlotRun.IndexOf((UInt256)1000)];
        UInt256 miss = ReadRun(bundle, TestItem.AddressA, 5000, filtered: true)[SlotRun.IndexOf((UInt256)5000)];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hit, Is.EqualTo((UInt256)2));
            Assert.That(miss, Is.EqualTo((UInt256)22));
            Assert.That(bundle.SlotFilter, Is.Null);
            Assert.That(bundle.MayFilterSlots, Is.False);
            Assert.That(Metrics.PbtInMemorySlotFilterBuilds, Is.EqualTo(builds));
            Assert.That(Metrics.PbtInMemorySlotFilterBuildFailures - failures, Is.EqualTo(1), "one failed build, then no retry");
        }
    }

    [Test]
    [NonParallelizable]
    public void Cleanup_frees_the_filter_and_its_memory()
    {
        long memoryBefore = Metrics.PbtInMemorySlotFilterMemory;
        long buildsBefore = Metrics.PbtInMemorySlotFilterBuilds;
        PbtReadOnlySnapshotBundle bundle = new(
            PbtSnapshotBundleTestExtensions.Chain(new PbtResourcePool(new PbtConfig()), LayerWriting(TestItem.AddressA, 1000, 1), LayerWriting(TestItem.AddressB, 1000, 2)),
            new ContentReader(new PbtSnapshotContent()), recordDetailedMetrics: false, RealBitsPerKey);
        Assert.That(bundle.TryLease(), Is.True);
        ReadRun(bundle, TestItem.AddressA, 1000, filtered: true);
        BloomFilter filter = bundle.SlotFilter!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Metrics.PbtInMemorySlotFilterBuilds - buildsBefore, Is.EqualTo(1));
            Assert.That(Metrics.PbtInMemorySlotFilterMemory - memoryBefore, Is.EqualTo(filter.DataBytes));
        }

        bundle.Dispose();
        Assert.DoesNotThrow(() => filter.MightContain(1), "a bundle still leased keeps its filter");

        bundle.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.SlotFilter, Is.Null);
            Assert.That(() => filter.MightContain(1), Throws.TypeOf<ObjectDisposedException>());
            Assert.That(Metrics.PbtInMemorySlotFilterMemory, Is.EqualTo(memoryBefore));
        }
    }

    [Test]
    public void Snapshot_bundle_uses_the_filter_only_when_asked([Values] bool filterInMemorySlotReads)
    {
        PbtResourcePool pool = new(new PbtConfig());
        PbtSnapshotContent persisted = new();
        persisted.SetSlot(PbtTestLeaves.SlotKey(TestItem.AddressA, 5000), 22);
        PbtReadOnlySnapshotBundle readOnly = new(PbtSnapshotBundleTestExtensions.Chain(pool, LayerWriting(TestItem.AddressA, 1000, 1), LayerWriting(TestItem.AddressB, 1000, 2)),
            new ContentReader(persisted), recordDetailedMetrics: false, RealBitsPerKey);
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0), readOnly, pool, PbtResourcePool.Usage.ReadOnlyProcessingEnv, NoopPbtTrieNodeCache.Instance, filterInMemorySlotReads);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.GetSlot(TestItem.AddressA, 1000), Is.EqualTo((UInt256)1));
            Assert.That(bundle.GetSlot(TestItem.AddressA, 5000), Is.EqualTo((UInt256)22));
            Assert.That(readOnly.SlotFilter is not null, Is.EqualTo(filterInMemorySlotReads));
        }
    }

    [Test]
    public async Task Db_manager_hands_the_configured_bits_per_key_to_shared_bundles([Values(0.0, RealBitsPerKey)] double bitsPerKey)
    {
        await using PbtTestContext ctx = new(config: new PbtConfig { InMemorySnapshotBloomBitsPerKey = bitsPerKey });
        Hash256 root;
        using (IWorldStateScopeProvider.IScope scope = ctx.BeginScope(null))
        {
            scope.CommitBlock(1, TestItem.AddressA, 100, 1000);
            scope.CommitBlock(2, TestItem.AddressA, 200, 1000);
            root = scope.CommitBlock(3, TestItem.AddressA, 300, 1000);
        }

        StateId state = new(Build.A.BlockHeader.WithNumber(3).WithStateRoot(root).TestObject);
        using (PbtSnapshotBundle bundle = ctx.Manager.TryGatherBundle(state, new PbtSnapshotPooledList(0), PbtResourcePool.Usage.ReadOnlyProcessingEnv, filterInMemorySlotReads: true)!)
            Assert.That(bundle.GetSlot(TestItem.AddressA, 1000), Is.EqualTo((UInt256)3));

        using PbtReadOnlySnapshotBundle shared = ctx.Manager.GatherReadOnlyBundle(state);
        Assert.That(shared.SlotFilter is not null, Is.EqualTo(bitsPerKey > 0));
    }

    [Test]
    public void Overridable_world_scope_gathers_bundles_that_filter_slot_reads()
    {
        IPbtDbManager manager = Substitute.For<IPbtDbManager>();
        using MemDb codeDb = new();
        using PbtOverridableWorldScope scope = new(codeDb, manager, PooledRefCountingMemoryProvider.Instance, new PbtConfig(), UnavailableStateHeaderProvider.Instance, LimboLogs.Instance);

        Assert.Throws<MissingTrieNodeException>(() => scope.GlobalStateReader.GetStorage(null, TestItem.AddressA, 1, out _));
        manager.Received(1).TryGatherBundle(Arg.Any<StateId>(), Arg.Any<PbtSnapshotPooledList>(), PbtResourcePool.Usage.ReadOnlyProcessingEnv, filterInMemorySlotReads: true);
    }

    private static PbtSnapshotContent LayerWriting(Address address, in UInt256 slot, in UInt256 value)
    {
        PbtSnapshotContent content = new();
        content.SetSlot(PbtTestLeaves.SlotKey(address, slot), value);
        return content;
    }

    private static UInt256[] ReadRun(PbtReadOnlySnapshotBundle bundle, Address address, in UInt256 slot, bool filtered)
    {
        ValueHash256 addressHash = PbtStateKey.AddressKeyHash(address);
        return Eip8297KeyDerivation.IsHeaderSlot(slot)
            ? ReadRun(bundle, Eip8297KeyDerivation.HeaderStorageKey(addressHash, slot), addressHash, filtered)
            : ReadRun(bundle, PbtStateKey.Storage(address, addressHash, slot), addressHash, filtered);
    }

    private static UInt256[] ReadRun<TKey>(PbtReadOnlySnapshotBundle bundle, in TKey slotKey, in ValueHash256 addressHash, bool filtered) where TKey : struct, IPbtKey<TKey>
    {
        HashedKey<TKey> runKey = SlotRun.RunKey(slotKey);
        PackedSlotRun run = filtered ? bundle.RentRunFiltered(runKey, addressHash) : bundle.RentRun(runKey, addressHash);
        try { return [.. Enumerable.Range(0, SlotRun.Width).Select(run.Get)]; }
        finally { SlotRun.Return(run); }
    }

    /// <summary>A persistence reader that serves the slot runs of <paramref name="persisted"/> and nothing else.</summary>
    private sealed class ContentReader(PbtSnapshotContent persisted) : IPbtPersistence.IReader
    {
        public StateId CurrentState => StateId.PreGenesis;
        public ValueHash256 CurrentRoot => default;
        public PbtAccount? GetAccount(in ValueHash256 addressHash) => null;

        public PackedSlotRun GetSlotRun<TKey>(in TKey runKey) where TKey : struct, IPbtKey<TKey> =>
            persisted.TryGetSlotRun(new HashedKey<TKey>(runKey), out PackedSlotRun? run) ? run.Clone() : SlotRun.Empty;

        public CodeInfo? GetCode(in ValueHash256 codeHash) => null;

        public bool TryGetCodeLeaf(in PbtPath key, out ValueHash256 value)
        {
            value = default;
            return false;
        }

        public IEnumerator<KeyValuePair<ValueHash256, PbtAccount>> EnumerateAccounts() => Enumerable.Empty<KeyValuePair<ValueHash256, PbtAccount>>().GetEnumerator();
        public RefCountingMemory? GetNodeGroup<TPath>(TPath groupKey) where TPath : struct, IPbtNodePath<TPath> => null;
        public void Dispose() => persisted.Dispose();
    }
}
