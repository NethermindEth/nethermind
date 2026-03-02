// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Hashing;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.BloomFilter;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class BloomFilterManagerTests
{
    private static StateId MakeStateId(long block) =>
        new(block, TestItem.KeccakA);

    private static Snapshot MakeSnapshot(long blockNumber, params Address[] addresses)
    {
        SnapshotContent content = new();
        foreach (Address addr in addresses)
            content.Accounts[addr] = Build.An.Account.WithBalance(1).TestObject;

        return new Snapshot(
            MakeStateId(blockNumber - 1),
            MakeStateId(blockNumber),
            content,
            Substitute.For<IResourcePool>(),
            ResourcePool.Usage.MainBlockProcessing);
    }

    [Test]
    public void AddEntries_and_GetBloomFiltersForRange_returns_correct_blooms()
    {
        // rangeSize=4: buckets [0-3], [4-7], [8-11]
        BloomFilterManager manager = new(rangeSize: 4, estimatedEntriesPerBlock: 100, bitsPerKey: 14);

        Address addr1 = TestItem.AddressA;
        Address addr2 = TestItem.AddressB;

        Snapshot snap1 = MakeSnapshot(1, addr1);      // bucket 0
        Snapshot snap5 = MakeSnapshot(5, addr2);      // bucket 1

        manager.AddEntries(snap1);
        manager.AddEntries(snap5);

        // Query bucket 0 only
        using ArrayPoolList<IBloomFilter> bucket0 = manager.GetBloomFiltersForRange(0, 3);
        Assert.That(bucket0.Count, Is.EqualTo(1));
        Assert.That(bucket0[0].MightContain(XxHash64.HashToUInt64(addr1.Bytes)), Is.True);
        Assert.That(bucket0[0].StartingBlockNumber, Is.EqualTo(0));
        Assert.That(bucket0[0].EndingBlockNumber, Is.EqualTo(3));

        // Query bucket 1 only
        using ArrayPoolList<IBloomFilter> bucket1 = manager.GetBloomFiltersForRange(4, 7);
        Assert.That(bucket1.Count, Is.EqualTo(1));
        Assert.That(bucket1[0].MightContain(XxHash64.HashToUInt64(addr2.Bytes)), Is.True);

        // Query spanning both buckets
        using ArrayPoolList<IBloomFilter> both = manager.GetBloomFiltersForRange(0, 7);
        Assert.That(both.Count, Is.EqualTo(2));

        // Query empty bucket
        using ArrayPoolList<IBloomFilter> empty = manager.GetBloomFiltersForRange(8, 11);
        Assert.That(empty.Count, Is.EqualTo(0));

        manager.Dispose();
    }

    [Test]
    public void Bloom_negative_lookup_returns_false()
    {
        BloomFilterManager manager = new(rangeSize: 4, estimatedEntriesPerBlock: 100, bitsPerKey: 14);

        Address addr1 = TestItem.AddressA;
        Address addrNotAdded = TestItem.AddressC;

        manager.AddEntries(MakeSnapshot(1, addr1));

        using ArrayPoolList<IBloomFilter> blooms = manager.GetBloomFiltersForRange(0, 3);
        Assert.That(blooms[0].MightContain(XxHash64.HashToUInt64(addrNotAdded.Bytes)), Is.False);

        manager.Dispose();
    }

    [Test]
    public void NoopBloomFilterManager_returns_empty()
    {
        NoopBloomFilterManager noop = NoopBloomFilterManager.Instance;
        using ArrayPoolList<IBloomFilter> result = noop.GetBloomFiltersForRange(0, 1000);
        Assert.That(result.Count, Is.EqualTo(0));
    }

    [Test]
    public void ReadOnlySnapshotBundle_skips_segments_via_bloom()
    {
        // Create a bloom filter manager with rangeSize=128
        BloomFilterManager manager = new(rangeSize: 128, estimatedEntriesPerBlock: 100, bitsPerKey: 14);

        Address addressInSnapshot = TestItem.AddressA;
        Address addressNotInSnapshot = TestItem.AddressC;

        // Create a snapshot at block 10 with addressInSnapshot
        SnapshotContent content = new();
        content.Accounts[addressInSnapshot] = Build.An.Account.WithBalance(42).TestObject;

        Snapshot snapshot = new(
            MakeStateId(9),
            MakeStateId(10),
            content,
            Substitute.For<IResourcePool>(),
            ResourcePool.Usage.MainBlockProcessing);

        manager.AddEntries(snapshot);

        // Build a ReadOnlySnapshotBundle with this snapshot
        SnapshotPooledList list = new(1);
        list.Add(snapshot);
        snapshot.TryAcquire(); // Keep it alive

        ReadOnlySnapshotBundle bundle = new(list, new NoopPersistenceReader(), false, manager, 128);
        bundle.TryLease();

        // Looking up the address that IS in the snapshot should succeed
        Account? found = bundle.GetAccount(addressInSnapshot);
        Assert.That(found, Is.Not.Null);
        Assert.That(found!.Balance, Is.EqualTo((Nethermind.Int256.UInt256)42));

        // Looking up an address NOT in the snapshot should return null (bloom skip + persistence returns null)
        Account? notFound = bundle.GetAccount(addressNotInSnapshot);
        Assert.That(notFound, Is.Null);

        // BloomFilterSkip should have been incremented
        Assert.That(Metrics.BloomFilterSkip, Is.GreaterThan(0));

        bundle.Dispose();
        manager.Dispose();
    }

    [Test]
    public void BlockRangeBloomFilter_delegates_correctly()
    {
        BloomFilter inner = new(100, 14);
        BlockRangeBloomFilter wrapper = new(inner, 0, 127);

        Assert.That(wrapper.StartingBlockNumber, Is.EqualTo(0));
        Assert.That(wrapper.EndingBlockNumber, Is.EqualTo(127));

        ulong key = XxHash64.HashToUInt64(TestItem.AddressA.Bytes);
        Assert.That(wrapper.MightContain(key), Is.False);

        wrapper.Add(key);
        Assert.That(wrapper.MightContain(key), Is.True);

        wrapper.Dispose();
    }
}
