// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtResourcePoolTests
{
    private PbtResourcePool _pool = null!;
    [SetUp] public void SetUp() => _pool = new PbtResourcePool(new PbtConfig());

    [TestCase(PbtResourcePool.Usage.MainBlockProcessing, false)]
    [TestCase(PbtResourcePool.Usage.ReadOnlyProcessingEnv, true)]
    public void Write_accumulator_drains_and_pool_return_discards_pending_changes(PbtResourcePool.Usage usage, bool parallel)
    {
        PbtWriteBatchBuilder builder = _pool.GetWriteBatchBuilder(usage);
        PbtFullKey[] keys = new PbtFullKey[1536];
        for (int index = 0; index < keys.Length; index++)
        {
            int partition = index / 512;
            byte[] bytes = new byte[partition == 2 ? 66 : 34];
            bytes[0] = partition == 2 ? (byte)0xFF : (byte)partition;
            bytes[1] = (byte)(index / 2);
            bytes[^1] = (byte)(index % 2);
            keys[index] = new(bytes);
        }
        void Write(int index)
        {
            builder.SetLeaf(keys[index], TestItem.KeccakA.ValueHash256);
            builder.SetLeaf(keys[index], null);
            ValueHash256 value = index % 2 == 0 ? default : TestItem.KeccakB.ValueHash256;
            builder.SetLeaf(keys[index], value);
        }
        if (parallel) Parallel.For(0, keys.Length, Write);
        else for (int index = 0; index < keys.Length; index++) Write(index);

        Dictionary<PbtFullKey, ValueHash256?> leaves = new(builder.Leaves);
        PbtWriteBatchSet prepared = builder.PrepareDrain();
        PbtWriteBatchSet generic = PbtWriteBatchSet.Create(leaves);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(prepared.Count, Is.EqualTo(keys.Length));
            Assert.That(prepared.Entries.ToArray(), Is.EqualTo(generic.Entries.ToArray()));
            Assert.That(prepared.Precalculated.ToArray(), Is.EqualTo(generic.Precalculated.ToArray()));
        }
        prepared.Consume(out PbtWriteOperation[] operations, out _);
        Array.Clear(operations);
        Assert.That(builder.Leaves, Is.EquivalentTo(leaves), "fold scratch must not own the publication values");
        for (int index = 0; index < keys.Length; index++)
        {
            Assert.That(builder.TryGetLeaf(keys[index], out ValueHash256? value), Is.True);
            Assert.That(value, Is.EqualTo(index % 2 == 0 ? null : (ValueHash256?)TestItem.KeccakB.ValueHash256));
        }
        builder.CompleteDrain();
        Assert.That(builder.PrepareDrain().Count, Is.Zero);
        builder.SetLeaf(keys[0], TestItem.KeccakA.ValueHash256);
        Assert.That(builder.PrepareDrain().Count, Is.EqualTo(1));
        _pool.ReturnWriteBatchBuilder(usage, builder);
        PbtWriteBatchBuilder rented = _pool.GetWriteBatchBuilder(usage);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rented, Is.SameAs(builder));
            Assert.That(rented.PrepareDrain().Count, Is.Zero);
            Assert.That(rented.TryGetLeaf(keys[0], out _), Is.False);
        }
        _pool.ReturnWriteBatchBuilder(usage, rented);
    }

    [TestCase(null)]
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(42)]
    public void PrewarmKeysAndOverloadsMatch(int? slotNumber)
    {
        Address address = TestItem.AddressA;
        ValueAddress valueAddress = new(address.Bytes);
        UInt256? slot = slotNumber is null ? null : (UInt256)slotNumber.Value;
        ulong expected = slot is null
            ? (ulong)((AddressAsKey)address).GetHashCode64()
            : (ulong)new StorageCell(address, slot.Value).GetHashCode64();
        using PbtTransientResource resource = new();
        Assert.That(PbtTransientResource.PrewarmKey(address.Bytes, slot), Is.EqualTo(expected));
        Assert.That(resource.ShouldPrewarm(address, slot), Is.True);
        Assert.That(resource.ShouldPrewarm(valueAddress, slot), Is.False);
        resource.Reset();
        Assert.That(resource.ShouldPrewarm(valueAddress, slot), Is.True);
        Assert.That(resource.ShouldPrewarm(address, slot), Is.False);
        if (slot is not null)
            Assert.That(PbtTransientResource.PrewarmKey(address.Bytes, slot), Is.Not.EqualTo(PbtTransientResource.PrewarmKey(address.Bytes, null)));
    }

    [TestCase(PbtResourcePool.Usage.MainBlockProcessing)]
    [TestCase(PbtResourcePool.Usage.ReadOnlyProcessingEnv)]
    public void CachedResourceReturnWaitsForLastLeaseAndResets(PbtResourcePool.Usage usage)
    {
        PbtTransientResource resource = _pool.GetCachedResource(usage);
        try
        {
            Assert.That(resource.ShouldPrewarm(TestItem.AddressA), Is.True);
            Assert.That(resource.TryAcquireLease(), Is.True);
            resource.ReleaseLease();
            PbtTransientResource concurrentRental = _pool.GetCachedResource(usage);
            try
            {
                Assert.That(concurrentRental, Is.Not.SameAs(resource));
                Assert.That(resource.ShouldPrewarm(TestItem.AddressA), Is.False);
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
            Assert.That(reused.ShouldPrewarm(TestItem.AddressA), Is.True);
        }
        finally
        {
            reused.ReleaseLease();
            DrainCachedResources(usage, 2);
        }
    }

    [Test]
    public void CachedResourceCategoriesAreIsolated()
    {
        PbtTransientResource main = _pool.GetCachedResource(PbtResourcePool.Usage.MainBlockProcessing);
        main.ReleaseLease();
        PbtTransientResource readOnly = _pool.GetCachedResource(PbtResourcePool.Usage.ReadOnlyProcessingEnv);
        try
        {
            Assert.That(readOnly, Is.Not.SameAs(main));
        }
        finally
        {
            readOnly.ReleaseLease();
            DrainCachedResources(PbtResourcePool.Usage.MainBlockProcessing, 1);
            DrainCachedResources(PbtResourcePool.Usage.ReadOnlyProcessingEnv, 1);
        }
    }

    [Test]
    public void OverflowDisposesBloomAndRetainsGrownCapacity()
    {
        PbtTransientResource resource = _pool.GetCachedResource(PbtResourcePool.Usage.Compact2);
        long originalCapacity = resource.Capacity;
        try
        {
            for (int slot = 0; slot < 2048; slot++) resource.ShouldPrewarm(TestItem.AddressA, (UInt256)slot);
        }
        finally
        {
            resource.ReleaseLease();
        }
        Assert.That(resource.Capacity, Is.GreaterThan(originalCapacity));
        Assert.Throws<ObjectDisposedException>(() => resource.ShouldPrewarm(TestItem.AddressA));
        PbtTransientResource replacement = _pool.GetCachedResource(PbtResourcePool.Usage.Compact2);
        try
        {
            Assert.That(replacement, Is.Not.SameAs(resource));
            Assert.That(replacement.Capacity, Is.EqualTo(resource.Capacity));
            Assert.That(replacement.ShouldPrewarm(TestItem.AddressA), Is.True);
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
        ValueHash256 addressHash = PbtKeyDerivation.AddressKeyHash(TestItem.AddressA);
        content.Accounts[addressHash] = Build.An.Account.TestObject;
        content.Storages[PbtStateKey.Storage(TestItem.AddressA, 1)] = EvmWordSlot.FromStripped(Bytes.FromHexString("01"));
        content.Codes[TestItem.KeccakA.ValueHash256] = new CodeInfo(Bytes.FromHexString("6001"));
        content.SelfDestructedStorageAddresses[addressHash] = true;
        content.SetCodeReference(TestItem.KeccakA.ValueHash256, 1);
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
            Assert.That(rented.CodeReferences, Is.Empty);
            Assert.That(rented.GetPayloadSize(), Is.EqualTo(default(PbtSnapshotPayloadSize)));
            Assert.That(rented.TryGetNodeGroup(groupKey, out _), Is.False);
            Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.Zero);
        }
        _pool.ReturnSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing, rented);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Group_read_lease_survives_replacement_and_reset(bool tombstone)
    {
        TrackingMemoryProvider memoryProvider = new();
        PbtNodePath groupKey = new([], 0);
        using PbtSnapshotContent content = new();
        RefCountingMemory readLease;
        byte[] expected;
        using (RefCountingMemory original = CreateGroup(memoryProvider, TestItem.KeccakA.ValueHash256))
        {
            expected = original.GetSpan().ToArray();
            content.SetNodeGroup(groupKey, original);
            content.SetNodeGroup(groupKey, original);
            Assert.That(content.TryGetNodeGroup(groupKey, out RefCountingMemory? leased), Is.True);
            readLease = leased!;
        }
        using (readLease)
        {
            using (RefCountingMemory replacement = CreateGroup(memoryProvider, TestItem.KeccakB.ValueHash256))
                content.SetNodeGroup(groupKey, tombstone ? null : replacement);
            bool found = content.TryGetNodeGroup(groupKey, out RefCountingMemory? current);
            using (current)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(found, Is.True);
                    Assert.That(current is null, Is.EqualTo(tombstone));
                    Assert.That(content.GetPayloadSize().Node, Is.EqualTo(groupKey.Encode().Length + (current?.Memory.Length ?? 0)));
                }
            }
            content.Reset();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(readLease.GetSpan().ToArray(), Is.EqualTo(expected));
                Assert.That(content.TryGetNodeGroup(groupKey, out _), Is.False);
                Assert.That(content.GetPayloadSize().Node, Is.Zero);
                Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.EqualTo(1));
            }
        }
        Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.Zero);
    }

    internal static RefCountingMemory CreateGroup(IRefCountingMemoryProvider memoryProvider, ValueHash256 hash)
    {
        PbtNodePath groupKey = new([], 0);
        byte[] encoding = PbtNodeCodec.EncodeBranch([], 0, hash, hash);
        BufferWriter writer = new(memoryProvider);
        try
        {
            PbtNodeGroupCodec.Encode(ref writer, groupKey, [new PbtNodeRecord(groupKey, encoding)]);
            return writer.Detach()!;
        }
        finally
        {
            writer.Dispose();
        }
    }

}
