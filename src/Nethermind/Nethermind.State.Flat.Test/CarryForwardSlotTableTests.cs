// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class CarryForwardSlotTableTests
{
    private const int OneSet = CarryForwardSlotTable.Ways;

    [Test]
    public void Constructor_InvalidCapacity_CanBeFinalized([Values(-1, 0, CarryForwardSlotTable.MaxCapacity + 1)] int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            using (new CarryForwardSlotTable(capacity)) { }
        });

        // A failed constructor still leaves an object on the finalization queue.
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    [Test]
    public void TryGet_AfterAdd_ReturnsTheCachedRead([Values] bool found)
    {
        using CarryForwardSlotTable table = new(1024);
        UInt256 value = found ? Pattern(1, 1) : default;

        Assert.That(Add(table, TestItem.AddressA, 7, found, value), Is.EqualTo(CarryForwardSlotTable.AddResult.Added));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(TryGet(table, TestItem.AddressA, 7, out bool cachedFound, out UInt256 cachedValue), Is.True);
            Assert.That(cachedFound, Is.EqualTo(found));
            Assert.That(cachedValue, Is.EqualTo(value));
            Assert.That(TryGet(table, TestItem.AddressA, 8, out _, out _), Is.False, "another slot of the same account");
            Assert.That(TryGet(table, TestItem.AddressB, 7, out _, out _), Is.False, "the same slot of another account");
            Assert.That(table.Count, Is.EqualTo(1));
        }
    }

    [Test]
    public void AddNoLock_SameKeyTwice_KeepsOneEntry()
    {
        using CarryForwardSlotTable table = new(1024);

        CarryForwardSlotTable.AddResult first = Add(table, TestItem.AddressA, 1, true, Pattern(1, 1));
        CarryForwardSlotTable.AddResult second = Add(table, TestItem.AddressA, 1, true, Pattern(1, 1));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.EqualTo(CarryForwardSlotTable.AddResult.Added));
            Assert.That(second, Is.EqualTo(CarryForwardSlotTable.AddResult.AlreadyPresent));
            Assert.That(table.Count, Is.EqualTo(1));
        }
    }

    [Test]
    public void AddNoLock_FullSet_ReplacesOneWayInsteadOfWiping()
    {
        using CarryForwardSlotTable table = new(OneSet);
        List<CarryForwardSlotTable.AddResult> results = [];
        for (int i = 0; i <= OneSet; i++) results.Add(Add(table, TestItem.AddressA, (ulong)i, true, Pattern(i, 1)));

        int stillCached = 0;
        for (int i = 0; i < OneSet; i++)
        {
            if (TryGet(table, TestItem.AddressA, (ulong)i, out _, out UInt256 value))
            {
                Assert.That(value, Is.EqualTo(Pattern(i, 1)));
                stillCached++;
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.GetRange(0, OneSet), Is.All.EqualTo(CarryForwardSlotTable.AddResult.Added));
            Assert.That(results[OneSet], Is.EqualTo(CarryForwardSlotTable.AddResult.Replaced));
            Assert.That(TryGet(table, TestItem.AddressA, OneSet, out _, out _), Is.True, "the newest read is cached");
            Assert.That(stillCached, Is.EqualTo(OneSet - 1), "exactly one older read was replaced");
            Assert.That(table.Count, Is.EqualTo(OneSet));
        }
    }

    [Test]
    public void RemoveNoLock_DropsOnlyThatSlot()
    {
        using CarryForwardSlotTable table = new(1024);
        Add(table, TestItem.AddressA, 1, true, Pattern(1, 1));
        Add(table, TestItem.AddressA, 2, true, Pattern(2, 1));

        bool removed = Remove(table, TestItem.AddressA, 1);
        bool removedAgain = Remove(table, TestItem.AddressA, 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(removed, Is.True);
            Assert.That(removedAgain, Is.False);
            Assert.That(TryGet(table, TestItem.AddressA, 1, out _, out _), Is.False);
            Assert.That(TryGet(table, TestItem.AddressA, 2, out _, out _), Is.True);
            Assert.That(table.Count, Is.EqualTo(1));
        }
    }

    [Test]
    public void ClearNoLock_HidesEveryEntryAndFreesTheirWays()
    {
        using CarryForwardSlotTable table = new(OneSet);
        for (int i = 0; i < OneSet; i++) Add(table, TestItem.AddressA, (ulong)i, true, Pattern(i, 1));

        table.ClearNoLock();
        bool anyVisible = false;
        for (int i = 0; i < OneSet; i++) anyVisible |= TryGet(table, TestItem.AddressA, (ulong)i, out _, out _);
        CarryForwardSlotTable.AddResult afterClear = Add(table, TestItem.AddressA, 100, true, Pattern(100, 1));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(anyVisible, Is.False);
            Assert.That(afterClear, Is.EqualTo(CarryForwardSlotTable.AddResult.Added), "a cleared entry is a free way");
            Assert.That(table.Count, Is.EqualTo(1));
        }
    }

    [Test]
    public void ClearNoLock_EpochWrap_DoesNotBringBackOldEntries()
    {
        using CarryForwardSlotTable table = new(1024);
        uint firstEpoch = table.Epoch;
        Add(table, TestItem.AddressA, 1, true, Pattern(1, 1));

        // Skip ahead to the last epoch; the entry above keeps its stamp and would read as current again after the wrap.
        table.Epoch = uint.MaxValue;
        Add(table, TestItem.AddressA, 2, true, Pattern(2, 1));
        bool lastEpochVisible = TryGet(table, TestItem.AddressA, 2, out _, out _);

        table.ClearNoLock();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(lastEpochVisible, Is.True);
            Assert.That(table.Epoch, Is.EqualTo(firstEpoch));
            Assert.That(TryGet(table, TestItem.AddressA, 1, out _, out _), Is.False, "stamped with the epoch the table wrapped to");
            Assert.That(TryGet(table, TestItem.AddressA, 2, out _, out _), Is.False, "stamped with the last epoch");
            Assert.That(table.Count, Is.Zero);
        }
    }

    [Test]
    public void TryGet_KeysWithTheSameSetAndTag_ComparesTheFullKey()
    {
        using CarryForwardSlotTable table = new(OneSet);
        (UInt256 first, UInt256 second) = FindSlotsWithTheSameTag(TestItem.AddressA);
        Add(table, TestItem.AddressA, first, true, Pattern(1, 1));
        Add(table, TestItem.AddressA, second, true, Pattern(2, 1));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(TryGet(table, TestItem.AddressA, first, out _, out UInt256 firstValue), Is.True);
            Assert.That(firstValue, Is.EqualTo(Pattern(1, 1)));
            Assert.That(TryGet(table, TestItem.AddressA, second, out _, out UInt256 secondValue), Is.True);
            Assert.That(secondValue, Is.EqualTo(Pattern(2, 1)));
            Assert.That(table.Count, Is.EqualTo(2));
        }
    }

    [Test]
    public void TryGet_EntryReplacedByTheSameTagMidRead_Misses()
    {
        // One set and one cached key, so every write below lands on the way the read is copying.
        using CarryForwardSlotTable table = new(OneSet);
        (UInt256 first, UInt256 second) = FindSlotsWithTheSameTag(TestItem.AddressA);
        Add(table, TestItem.AddressA, first, true, Pattern(1, 1));

        // Between the read's key and value copies: 2^20 writes, which bring a version of up to 20 bits back to where the
        // read started, and leave the way holding the other key.
        const int writes = 1 << 20;
        bool hit = table.TryGet<OnFlag>(CarryForwardSlotTable.Hash(TestItem.AddressA, first), TestItem.AddressA, first,
            out _, out UInt256 value, afterKeyCopy: () =>
            {
                Remove(table, TestItem.AddressA, first);
                for (int i = 2; i < writes; i += 2)
                {
                    Add(table, TestItem.AddressA, second, true, Pattern(2, 1));
                    Remove(table, TestItem.AddressA, second);
                }
                Add(table, TestItem.AddressA, second, true, Pattern(2, 1));
            });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hit, Is.False, $"returned {value}, the value of the key that replaced the one being read");
            Assert.That(TryGet(table, TestItem.AddressA, second, out _, out UInt256 secondValue), Is.True);
            Assert.That(secondValue, Is.EqualTo(Pattern(2, 1)));
        }
    }

    [TestCaseSource(nameof(NearKeys))]
    public void TryGet_KeyDifferingInOneByte_Misses(Address cachedAddress, UInt256 cachedSlot, Address probedAddress, UInt256 probedSlot)
    {
        using CarryForwardSlotTable table = new(1024);
        Add(table, cachedAddress, cachedSlot, true, Pattern(1, 1));

        Assert.That(TryGet(table, probedAddress, probedSlot, out _, out _), Is.False);
    }

    [Test]
    public void Dispose_FreesTheMemoryOnlyAfterTheLastLease()
    {
        CarryForwardSlotTable table = new(1024);
        bool leased = table.TryLease();

        table.Dispose();
        bool allocatedWhileLeased = table.IsAllocated;
        table.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(leased, Is.True);
            Assert.That(allocatedWhileLeased, Is.True);
            Assert.That(table.IsAllocated, Is.False);
            Assert.That(table.TryLease(), Is.False, "no lease once released for good");
        }
    }

    [Test]
    public void TryGet_WhileEntriesAreRewritten_NeverReturnsATornEntry()
    {
        // One set and more keys than ways, so the writer both rewrites keys in place and replaces them with others.
        const int keys = OneSet + 2;
        const int readers = 8;
        using CarryForwardSlotTable table = new(OneSet);
        Address[] addresses = new Address[keys];
        for (int i = 0; i < keys; i++) addresses[i] = TestItem.Addresses[i];

        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(2));
        long hits = 0;
        long torn = 0;
        Task writer = Task.Factory.StartNew(() =>
        {
            Random random = new(1);
            ulong version = 0;
            while (!stop.IsCancellationRequested)
            {
                int key = random.Next(keys);
                version++;
                Remove(table, addresses[key], (ulong)key);
                Add(table, addresses[key], (ulong)key, true, Pattern(key, version));
            }
        }, TaskCreationOptions.LongRunning);

        Task[] readerTasks = new Task[readers];
        for (int r = 0; r < readers; r++)
        {
            int seed = r + 2;
            readerTasks[r] = Task.Factory.StartNew(() =>
            {
                Random random = new(seed);
                while (!stop.IsCancellationRequested)
                {
                    int key = random.Next(keys);
                    if (!TryGet(table, addresses[key], (ulong)key, out bool found, out UInt256 value)) continue;
                    Interlocked.Increment(ref hits);
                    if (!found || !IsPattern(value, key)) Interlocked.Increment(ref torn);
                }
            }, TaskCreationOptions.LongRunning);
        }

        Task.WaitAll([writer, .. readerTasks]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(torn, Is.Zero);
            Assert.That(hits, Is.GreaterThan(1000), "readers hit entries while they were being rewritten");
        }
    }

    private static IEnumerable<TestCaseData> NearKeys()
    {
        Address address = TestItem.AddressA;
        byte[] lastByteFlipped = address.Bytes.ToArray();
        lastByteFlipped[^1] ^= 1;
        byte[] firstByteFlipped = address.Bytes.ToArray();
        firstByteFlipped[0] ^= 1;
        UInt256 slot = new(1, 2, 3, 4);

        yield return new TestCaseData(address, slot, new Address(lastByteFlipped), slot) { TestName = "last_address_byte" };
        yield return new TestCaseData(address, slot, new Address(firstByteFlipped), slot) { TestName = "first_address_byte" };
        yield return new TestCaseData(address, slot, address, new UInt256(1, 2, 3, 4 ^ (1UL << 63))) { TestName = "top_slot_bit" };
        yield return new TestCaseData(address, slot, address, new UInt256(1 ^ 1UL, 2, 3, 4)) { TestName = "bottom_slot_bit" };
    }

    private static (UInt256, UInt256) FindSlotsWithTheSameTag(Address address)
    {
        Dictionary<ulong, UInt256> byTag = [];
        for (ulong i = 0; ; i++)
        {
            UInt256 slot = new(i);
            ulong tag = CarryForwardSlotTable.TagOf(CarryForwardSlotTable.Hash(address, slot));
            if (byTag.TryGetValue(tag, out UInt256 other)) return (other, slot);
            byTag[tag] = slot;
        }
    }

    private static UInt256 Pattern(int key, ulong version) => CarryForwardCachingPersistenceTests.Pattern(key, version);

    private static bool IsPattern(in UInt256 value, int key) =>
        value.u0 >> CarryForwardCachingPersistenceTests.PatternVersionBits == (ulong)key
        && value == Pattern(key, value.u0 & ((1UL << CarryForwardCachingPersistenceTests.PatternVersionBits) - 1));

    private static CarryForwardSlotTable.AddResult Add(CarryForwardSlotTable table, Address address, in UInt256 slot, bool found, in UInt256 value) =>
        table.AddNoLock(CarryForwardSlotTable.Hash(address, slot), address, slot, found, value);

    private static bool Remove(CarryForwardSlotTable table, Address address, in UInt256 slot) =>
        table.RemoveNoLock(CarryForwardSlotTable.Hash(address, slot), address, slot);

    private static bool TryGet(CarryForwardSlotTable table, Address address, in UInt256 slot, out bool found, out UInt256 value) =>
        table.TryGet(CarryForwardSlotTable.Hash(address, slot), address, slot, out found, out value);
}
