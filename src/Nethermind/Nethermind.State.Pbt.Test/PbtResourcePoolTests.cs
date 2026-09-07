// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtResourcePoolTests
{
    private PbtResourcePool _pool = null!;
    [SetUp] public void SetUp() => _pool = new PbtResourcePool(new PbtConfig());

    [Test]
    public void ReturnedContent_IsRentedAgainAndReset()
    {
        PbtSnapshotContent content = _pool.GetSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing);
        PbtFullKey key = PbtStateKey.Account(TestItem.AddressA, PbtKeyDerivation.BasicDataLeafKey);
        content.SetLeaf(key, TestItem.KeccakA.ValueHash256);
        TrackingMemoryProvider memoryProvider = new();
        PbtNodePath groupKey = new([], 0);
        using (RefCountingMemory payload = CreateGroup(memoryProvider, TestItem.KeccakA.ValueHash256))
            content.SetNodeGroup(groupKey, payload);
        _pool.ReturnSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing, content);
        PbtSnapshotContent rented = _pool.GetSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rented, Is.SameAs(content));
            Assert.That(rented.TryGetLeaf(key, out _), Is.False);
            Assert.That(rented.TryGetNodeGroup(groupKey, out _), Is.False);
            Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.Zero);
        }
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
        byte[] encoding = PbtNodeCodec.Encode(new PbtBranchNode(new PbtBitPrefix([], 0), hash, hash));
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

    [Test]
    public void ReturnedPendingFlatWrites_AreRentedAgainAndReset()
    {
        PbtPendingFlatWrites pending = _pool.GetPendingFlatWrites(PbtResourcePool.Usage.MainBlockProcessing);
        pending.Accounts[TestItem.AddressA] = Build.An.Account.TestObject;
        _pool.ReturnPendingFlatWrites(PbtResourcePool.Usage.MainBlockProcessing, pending);
        Assert.That(_pool.GetPendingFlatWrites(PbtResourcePool.Usage.MainBlockProcessing), Is.SameAs(pending));
        Assert.That(pending.Accounts, Is.Empty);
    }
}
