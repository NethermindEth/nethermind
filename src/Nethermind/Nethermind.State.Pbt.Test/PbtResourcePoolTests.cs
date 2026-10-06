// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core.Memory;
using Nethermind.Core.Extensions;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Collections;
using Nethermind.Core.Test.Builders;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtResourcePoolTests
{
    private PbtResourcePool _pool = null!;
    [SetUp] public void SetUp() => _pool = new PbtResourcePool(new PbtConfig(), PooledRefCountingMemoryProvider.Instance);

    [TestCase(false, false, 0xFFFF, 64)]
    [TestCase(false, true, 0x8005, 64)]
    [TestCase(true, false, 0xFFFF, 64)]
    [TestCase(true, true, 0x8005, 64)]
    [TestCase(false, true, 0xFFFF, 600)]
    public void Write_accumulator_drains_and_pool_return_discards_pending_changes(bool storage, bool parallel, int touchedMask, int entriesPerShard)
    {
        if (storage) AssertWriteAccumulator(storage, parallel, touchedMask, entriesPerShard, _pool.GetStorageWriteBatch, _pool.ReturnStorageWriteBatch);
        else AssertWriteAccumulator(storage, parallel, touchedMask, entriesPerShard, _pool.GetWriteBatch, _pool.ReturnWriteBatch);
    }

    private static void AssertWriteAccumulator<TKey>(bool storage, bool parallel, int touchedMask, int entriesPerShard,
        Func<PbtResourcePool.Usage, PbtWriteBatchBuilder<TKey>> rent,
        Action<PbtResourcePool.Usage, PbtWriteBatchBuilder<TKey>> returnBatch) where TKey : struct, IPbtKey<TKey>
    {
        PbtResourcePool.Usage usage = parallel ? PbtResourcePool.Usage.ReadOnlyProcessingEnv : PbtResourcePool.Usage.MainBlockProcessing;
        PbtWriteBatchBuilder<TKey> batch = rent(usage);
        List<TKey> keys = [];
        for (int shard = 15; shard >= 0; shard--)
        {
            if ((touchedMask & (1 << shard)) == 0) continue;
            for (int index = entriesPerShard - 1; index >= 0; index--)
            {
                byte[] bytes = new byte[storage ? 66 : 34];
                bytes[0] = storage ? (byte)0xFF : (byte)0x00;
                bytes[1] = (byte)((shard << 4) | (index % 16));
                bytes[^2] = (byte)(index >> 8);
                bytes[^1] = (byte)index;
                keys.Add(TKey.Create(bytes));
            }
        }
        void Write(int index)
        {
            batch.SetLeaf(keys[index], TestItem.KeccakA.ValueHash256);
            batch.SetLeaf(keys[index], null);
            ValueHash256 value = index % 2 == 0 ? default : TestItem.KeccakB.ValueHash256;
            batch.Set(keys[index], value);
        }
        if (parallel) Parallel.For(0, keys.Count, Write);
        else for (int index = 0; index < keys.Count; index++) Write(index);

        Dictionary<TKey, ValueHash256> expectedValues = [];
        for (int index = 0; index < keys.Count; index++) expectedValues[keys[index]] = index % 2 == 0 ? default : TestItem.KeccakB.ValueHash256;
        using PbtWriteBatch<TKey> prepared = batch.Build();
        prepared.Consume(out ArrayPoolList<PbtWriteOperation<TKey>> operations, out ArrayPoolList<int> table);
        using ArrayPoolList<PbtWriteOperation<TKey>> operationsLease = operations;
        using ArrayPoolList<int> tableLease = table;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(batch.Count, Is.EqualTo(keys.Count));
            Assert.That(operations.Count, Is.EqualTo(keys.Count));
        }
        int[] expectedTable = new int[17];
        expectedTable[0] = touchedMask;
        int compactCount = 0;
        int offset = 0;
        for (int shard = 0; shard < 16; shard++)
        {
            if ((touchedMask & (1 << shard)) == 0) continue;
            expectedTable[1 + compactCount++] = entriesPerShard;
            HashSet<TKey> shardKeys = [];
            foreach (PbtWriteOperation<TKey> operation in operations.AsSpan().Slice(offset, entriesPerShard))
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(operation.Key.Bytes[1] >> 4, Is.EqualTo(shard));
                    Assert.That(shardKeys.Add(operation.Key), Is.True);
                    Assert.That(operation.Value, Is.EqualTo(expectedValues[operation.Key]));
                }
            }
            offset += entriesPerShard;
        }
        Assert.That(table, Is.EqualTo(expectedTable));
        returnBatch(usage, batch);
        PbtWriteBatchBuilder<TKey> rented = rent(usage);
        using PbtWriteBatch<TKey> empty = rented.Build();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rented, Is.SameAs(batch));
            Assert.That(rented.Count, Is.Zero);
            Assert.That(empty.ConsumeOperations(), Is.Empty);
        }
        returnBatch(usage, rented);
    }

    [Test]
    public void Returned_shards_are_empty_and_not_shared_with_the_previous_builder()
    {
        using PbtWriteBatchBuilder<PbtStorageTreeKey> original = new(0);
        PbtStorageTreeKey key = new(Bytes.FromHexString("1234"));
        original.Set(key, TestItem.KeccakA.ValueHash256);
        original.Reset();

        using PbtWriteBatchBuilder<PbtStorageTreeKey> replacement = new(0);
        PbtStorageTreeKey replacementKey = new(Bytes.FromHexString("1235"));
        replacement.Set(replacementKey, TestItem.KeccakB.ValueHash256);
        original.Reset();
        original.Dispose();
        original.Set(key, TestItem.KeccakC.ValueHash256);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(original.Build().ConsumeOperations(), Is.EqualTo(new[]
            {
                new PbtWriteOperation<PbtStorageTreeKey>(key, TestItem.KeccakC.ValueHash256)
            }));
            Assert.That(replacement.Build().ConsumeOperations(), Is.EqualTo(new[]
            {
                new PbtWriteOperation<PbtStorageTreeKey>(replacementKey, TestItem.KeccakB.ValueHash256)
            }));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Write_batch_pool_retains_three_partitions_per_writable_bundle(bool storage)
    {
        if (storage) AssertPoolCapacity(2, _pool.GetStorageWriteBatch, _pool.ReturnStorageWriteBatch);
        else AssertPoolCapacity(4, _pool.GetWriteBatch, _pool.ReturnWriteBatch);
    }

    private static void AssertPoolCapacity<TKey>(int capacity,
        Func<PbtResourcePool.Usage, PbtWriteBatchBuilder<TKey>> rent,
        Action<PbtResourcePool.Usage, PbtWriteBatchBuilder<TKey>> returnBatch) where TKey : struct, IPbtKey<TKey>
    {
        const PbtResourcePool.Usage usage = PbtResourcePool.Usage.MainBlockProcessing;
        PbtWriteBatchBuilder<TKey>[] batches = new PbtWriteBatchBuilder<TKey>[capacity + 1];
        for (int index = 0; index < batches.Length; index++) batches[index] = rent(usage);
        foreach (PbtWriteBatchBuilder<TKey> batch in batches) returnBatch(usage, batch);
        PbtWriteBatchBuilder<TKey>[] rented = new PbtWriteBatchBuilder<TKey>[capacity + 1];
        for (int index = 0; index < rented.Length; index++) rented[index] = rent(usage);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rented.AsSpan(0, capacity).ToArray(), Is.EquivalentTo(batches.AsSpan(0, capacity).ToArray()));
            Assert.That(rented[capacity], Is.Not.SameAs(batches[capacity]));
        }
        foreach (PbtWriteBatchBuilder<TKey> batch in rented) returnBatch(usage, batch);
    }

    [TestCase(PbtResourcePool.Usage.MainBlockProcessing, PbtResourcePool.Usage.ReadOnlyProcessingEnv)]
    [TestCase(PbtResourcePool.Usage.ReadOnlyProcessingEnv, PbtResourcePool.Usage.MainBlockProcessing)]
    public void CachedResourceReturnWaitsForLastLeaseAndResets(PbtResourcePool.Usage usage, PbtResourcePool.Usage otherUsage)
    {
        PbtTransientResource resource = _pool.GetCachedResource(usage);
        try
        {
            resource.NodeGroups.Set(default, new PbtNodePath([2], 8), RefCountingMemory.OwningRocksDb(new ArrayMemoryManager([1])));
            Assert.That(resource.TryAcquireLease(), Is.True);
            resource.ReleaseLease();
            PbtTransientResource concurrentRental = _pool.GetCachedResource(usage);
            try
            {
                Assert.That(concurrentRental, Is.Not.SameAs(resource));
                Assert.That(resource.NodeGroups.Count, Is.EqualTo(1));
            }
            finally
            {
                concurrentRental.ReleaseLease();
            }
        }
        finally
        {
            resource.ReleaseLease();
        }
        PbtTransientResource reused = _pool.GetCachedResource(usage);
        try
        {
            Assert.That(reused, Is.SameAs(resource));
            Assert.That(reused.NodeGroups.Count, Is.Zero);
        }
        finally
        {
            reused.ReleaseLease();
        }
        PbtTransientResource other = _pool.GetCachedResource(otherUsage);
        other.ReleaseLease();
        Assert.That(other, Is.Not.SameAs(resource));
        DrainCachedResources(otherUsage, 1);
        DrainCachedResources(usage, 2);
    }

    [Test]
    public void OverflowRetainsGrownNodeGroupCapacity([Values] bool growNodeGroups)
    {
        PbtTransientResource resource = _pool.GetCachedResource(PbtResourcePool.Usage.Compact2);
        int originalCapacity = resource.NodeGroups.Capacity;
        try
        {
            if (growNodeGroups)
                for (int first = 0; first < 32; first++)
                    for (int second = 0; second < 256; second++)
                        resource.NodeGroups.Set(default, new PbtNodePath([(byte)(first + 2), (byte)second], 16), RefCountingMemory.OwningRocksDb(new ArrayMemoryManager([1])));
        }
        finally
        {
            resource.ReleaseLease();
        }
        Assert.That(resource.NodeGroups.Capacity, growNodeGroups ? Is.GreaterThan(originalCapacity) : Is.EqualTo(originalCapacity));
        Assert.That(resource.NodeGroups.Count, Is.Zero);
        PbtTransientResource replacement = _pool.GetCachedResource(PbtResourcePool.Usage.Compact2);
        try
        {
            Assert.That(replacement, Is.Not.SameAs(resource));
            Assert.That(replacement.NodeGroups.Capacity, Is.EqualTo(resource.NodeGroups.Capacity));
        }
        finally
        {
            replacement.ReleaseLease();
        }
    }

    private void DrainCachedResources(PbtResourcePool.Usage usage, int count)
    {
        // The production pool retains resources for the node lifetime; this fixture owns that lifetime.
        PbtTransientResource[] resources = new PbtTransientResource[count];
        for (int index = 0; index < count; index++) resources[index] = _pool.GetCachedResource(usage);
        foreach (PbtTransientResource resource in resources)
        {
            resource.ReleaseLease();
            resource.Dispose();
        }
    }

    [Test]
    public void ReturnedContent_IsRentedAgainAndReset()
    {
        PbtSnapshotContent content = _pool.GetSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing);
        ValueHash256 addressHash = PbtStateKey.AddressKeyHash(TestItem.AddressA);
        content.Accounts[addressHash] = Build.An.Account.TestObject.ToPbtAccount();
        content.SetSlot(PbtStateKey.Storage(TestItem.AddressA, 1), EvmWordSlot.FromStripped(Bytes.FromHexString("01")));
        content.Codes[TestItem.KeccakA.ValueHash256] = new CodeInfo(Bytes.FromHexString("6001"));
        content.SelfDestructedStorageAddresses[addressHash] = true;
        TrackingMemoryProvider memoryProvider = new();
        PbtNodePath groupKey = new([], 0);
        using (RefCountingMemory payload = CreateGroup(memoryProvider, TestItem.KeccakA.ValueHash256))
            content.SetNodeGroup(groupKey, payload);
        _pool.ReturnSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing, content);
        PbtSnapshotContent rented = _pool.GetSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rented, Is.SameAs(content));
            Assert.That(rented.Accounts, Is.Empty);
            Assert.That(rented.Storages, Is.Empty);
            Assert.That(rented.Codes, Is.Empty);
            Assert.That(rented.SelfDestructedStorageAddresses, Is.Empty);
            Assert.That(rented.GetPayloadSize(), Is.EqualTo(default(PbtSnapshotPayloadSize)));
            Assert.That(rented.TryGetNodeGroup(groupKey, out _), Is.False);
            Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.Zero);
        }
        _pool.ReturnSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing, rented);
    }

    internal static RefCountingMemory CreateGroup(IRefCountingMemoryProvider memoryProvider, ValueHash256 hash)
    {
        PbtNodePath groupKey = new([], 0);
        byte[] encoding = PbtTreeHarness.EncodeBranch([], 0, hash, hash);
        BufferWriter writer = new(memoryProvider);
        try
        {
            PbtNodeGroupEncoder.Encode(ref writer, groupKey, [new PbtNodeRecord(groupKey.ToPath<PbtStorageNodePath>(), encoding)], default);
            return writer.Detach()!;
        }
        finally
        {
            writer.Dispose();
        }
    }

}
