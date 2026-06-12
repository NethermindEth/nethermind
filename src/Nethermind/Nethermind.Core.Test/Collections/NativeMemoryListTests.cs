// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Linq;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Core.Test.Collections;

[Parallelizable(ParallelScope.All)]
public class NativeMemoryListTests
{
    [Test]
    public void Empty_list_and_zero_capacity_growth()
    {
        using NativeMemoryList<int> list = new(1024);
        using NativeMemoryList<int> empty = new(0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(list.Count, Is.EqualTo(0));
            Assert.That(list.Capacity, Is.EqualTo(1024));
            Assert.That(empty, Is.Empty);
        }
        empty.Add(1);
        Assert.That(empty.Count, Is.EqualTo(1));
        Assert.That(empty.Remove(1), Is.True);
        Assert.That(empty.Count, Is.EqualTo(0));
        empty.Add(2);
        Assert.That(empty[0], Is.EqualTo(2));
    }

    [Test]
    public void Add_AddRange_and_growth()
    {
        using NativeMemoryList<int> list = new(4);
        list.AddRange(Enumerable.Range(0, 50).ToArray());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(list, Is.EquivalentTo(Enumerable.Range(0, 50)));
            Assert.That(list.Count, Is.EqualTo(50));
            Assert.That(list.Capacity, Is.GreaterThanOrEqualTo(50));
        }

        list.Add(123);
        Assert.That(list[50], Is.EqualTo(123));
    }

    [Test]
    public void Clear_resets_count_only()
    {
        using NativeMemoryList<int> list = new(8);
        list.AddRange(stackalloc int[] { 1, 2, 3 });
        int before = list.Capacity;
        list.Clear();
        Assert.That(list.Count, Is.EqualTo(0));
        Assert.That(list.Capacity, Is.EqualTo(before));
        list.Add(99);
        Assert.That(list[0], Is.EqualTo(99));
    }

    [TestCase(0)]
    [TestCase(2)]
    [TestCase(4)]
    public void Insert_RemoveAt_at_various_indices(int index)
    {
        using NativeMemoryList<int> list = new(8);
        list.AddRange(stackalloc int[] { 0, 1, 2, 3, 4 });
        list.Insert(index, 99);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(list[index], Is.EqualTo(99));
            Assert.That(list.Count, Is.EqualTo(6));
        }

        list.RemoveAt(index);
        Assert.That(list, Is.EquivalentTo(new[] { 0, 1, 2, 3, 4 }));
    }

    [Test]
    public void IndexOf_Contains_Remove_work()
    {
        using NativeMemoryList<int> list = new(4, [10, 20, 30]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(list.IndexOf(20), Is.EqualTo(1));
            Assert.That(list.Contains(30), Is.True);
            Assert.That(list.Contains(99), Is.False);
        }
        Assert.That(list.Remove(20), Is.True);
        Assert.That(list, Is.EquivalentTo(new[] { 10, 30 }));
        Assert.That(list.Remove(99), Is.False);
    }

    [Test]
    public void GetRef_returns_writable_reference()
    {
        using NativeMemoryList<int> list = new(2, 2);
        ref int slot = ref list.GetRef(1);
        slot = 42;
        Assert.That(list[1], Is.EqualTo(42));
    }

    [Test]
    public void AsSpan_reflects_count()
    {
        using NativeMemoryList<int> list = new(4);
        list.AddRange(stackalloc int[] { 1, 2, 3 });
        Assert.That(list.AsSpan().Length, Is.EqualTo(3));
        Assert.That(list.AsSpan()[1], Is.EqualTo(2));
    }

    [Test]
    public void Sort_and_Reverse()
    {
        using NativeMemoryList<int> list = new(4, [3, 1, 4, 1, 5, 9, 2, 6]);
        list.Sort((a, b) => a.CompareTo(b));
        Assert.That(list, Is.EqualTo(new[] { 1, 1, 2, 3, 4, 5, 6, 9 }));
        list.Reverse();
        Assert.That(list, Is.EqualTo(new[] { 9, 6, 5, 4, 3, 2, 1, 1 }));
    }

    [Test]
    public void Truncate_and_ReduceCount()
    {
        using NativeMemoryList<int> list = new(64);
        list.AddRange(Enumerable.Range(0, 50).ToArray());

        list.Truncate(10);
        Assert.That(list.Count, Is.EqualTo(10));

        list.ReduceCount(2);
        Assert.That(list.Count, Is.EqualTo(2));
        Assert.That(list, Is.EqualTo(new[] { 0, 1 }));
    }

    [Test]
    public void CopyTo_writes_into_destination_array()
    {
        using NativeMemoryList<int> list = new(4, [1, 2, 3]);
        int[] dest = new int[5];
        list.CopyTo(dest, 1);
        Assert.That(dest, Is.EqualTo(new[] { 0, 1, 2, 3, 0 }));
    }

    [Test]
    public void Dispose_is_idempotent_and_post_dispose_throws()
    {
        NativeMemoryList<int> list = new(4, [1, 2, 3]);
        list.Dispose();
        list.Dispose();
        Action act = () => _ = list[0];
        Assert.That(act, Throws.TypeOf<ObjectDisposedException>());
    }

    [Test]
    public void IList_interface_compliance()
    {
        using NativeMemoryList<int> list = new(4);
        IList ilist = list;
        Assert.That(ilist.Add(1), Is.EqualTo(0));
        Assert.That(ilist.Add(2), Is.EqualTo(1));
        Assert.That(ilist.Contains(1), Is.True);
        Assert.That(ilist.IndexOf(2), Is.EqualTo(1));
        ilist[0] = 99;
        Assert.That(ilist[0], Is.EqualTo(99));
        ilist.Insert(0, 7);
        Assert.That(ilist[0], Is.EqualTo(7));
        ilist.Remove(7);
        Assert.That(ilist.Count, Is.EqualTo(2));
    }

    [Test]
    public void Ref_struct_round_trip()
    {
        NativeMemoryListRef<int> r = new(4);
        try
        {
            r.AddRange(stackalloc int[] { 1, 2, 3 });
            Assert.That(r.Count, Is.EqualTo(3));
            Assert.That(r[1], Is.EqualTo(2));
            Assert.That(r.AsSpan().ToArray(), Is.EqualTo(new[] { 1, 2, 3 }));
            r.Add(4);
            Assert.That(r.AsSpan()[3], Is.EqualTo(4));
            r.Insert(0, 0);
            Assert.That(r.AsSpan().ToArray(), Is.EqualTo(new[] { 0, 1, 2, 3, 4 }));
            r.RemoveAt(0);
            Assert.That(r.AsSpan().ToArray(), Is.EqualTo(new[] { 1, 2, 3, 4 }));
            r.Clear();
            Assert.That(r.Count, Is.EqualTo(0));
        }
        finally
        {
            r.Dispose();
            r.Dispose(); // idempotent
        }
    }

    [Test]
    public void Ref_struct_growth_past_initial_capacity()
    {
        NativeMemoryListRef<long> r = new(2);
        try
        {
            for (int i = 0; i < 1000; i++) r.Add(i);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(r.Count, Is.EqualTo(1000));
                Assert.That(r.Capacity, Is.GreaterThanOrEqualTo(1000));
                Assert.That(r[0], Is.EqualTo(0L));
                Assert.That(r[999], Is.EqualTo(999L));
            }
        }
        finally { r.Dispose(); }
    }

    [Test]
    public void Ref_struct_EnsureCapacity()
    {
        NativeMemoryListRef<byte> r = new(4);
        try
        {
            r.EnsureCapacity(4096);
            Assert.That(r.Capacity, Is.GreaterThanOrEqualTo(4096));
        }
        finally { r.Dispose(); }
    }

    [Test]
    public void Empty_constructor_returns_disposable_zero_capacity()
    {
        using NativeMemoryList<int> empty = NativeMemoryList<int>.Empty();
        Assert.That(empty.Count, Is.EqualTo(0));
        Assert.That(empty.Capacity, Is.EqualTo(0));
    }

    // Capacity*sizeof(T) is well under the 1024-byte pool threshold here, so the underlying
    // buffer is rented from ArrayPool<T>.Shared (pinned) rather than NativeMemory.Alloc.
    // The list must behave identically regardless of which strategy was used; verify all
    // mutating + read paths with a single end-to-end exercise.
    [TestCase(8)]
    [TestCase(32)]
    [TestCase(64)]
    public void Sub_threshold_capacity_round_trips(int capacity)
    {
        using NativeMemoryList<byte> list = new(capacity);
        Assert.That(list.Count, Is.EqualTo(0));
        Assert.That(list.Capacity, Is.GreaterThanOrEqualTo(capacity));

        list.AddRange(Bytes.FromHexString("deadbeef"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(list.Count, Is.EqualTo(4));
            Assert.That(list[0], Is.EqualTo(0xde));
            Assert.That(list[3], Is.EqualTo(0xef));
        }

        list.Insert(0, 0x01);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(list[0], Is.EqualTo(0x01));
            Assert.That(list[4], Is.EqualTo(0xef));
        }

        list.RemoveAt(0);
        Assert.That(list.AsSpan().ToArray(), Is.EqualTo(Bytes.FromHexString("deadbeef")));

        list.Reverse();
        Assert.That(list[0], Is.EqualTo(0xef));
    }

    // Cross the pool/native boundary inside one list lifetime: start in the pool (16 bytes),
    // grow past 1 KiB so subsequent reallocations route to NativeMemory.Alloc, and confirm
    // the data survives the strategy switch and that Dispose frees both code paths cleanly.
    [Test]
    public void Growth_across_pool_native_threshold_preserves_data()
    {
        using NativeMemoryList<byte> list = new(16);
        byte[] payload = new byte[4096];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i & 0xFF);
        list.AddRange(payload);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(list.Count, Is.EqualTo(payload.Length));
            Assert.That(list.Capacity, Is.GreaterThanOrEqualTo(payload.Length));
            Assert.That(list.AsSpan().ToArray(), Is.EqualTo(payload));
        }
    }

    // ReduceCount shrinks below the byte threshold; the internal reallocation must route to
    // ArrayPool and the data must remain readable.
    [Test]
    public void ReduceCount_downgrades_native_to_pool_when_under_threshold()
    {
        using NativeMemoryList<long> list = new(256);  // 256 * 8 = 2 KiB → native
        for (int i = 0; i < 256; i++) list.Add(i);

        list.ReduceCount(8);  // 8 * 8 = 64 bytes → pool
        using (Assert.EnterMultipleScope())
        {
            Assert.That(list.Count, Is.EqualTo(8));
            Assert.That(list[0], Is.EqualTo(0L));
            Assert.That(list[7], Is.EqualTo(7L));
        }
    }

    // Regression for an issue where the (capacity, count) ctor would zero-clear `count` elements
    // against a buffer sized for `capacity` — heap overwrite when count > capacity on the native
    // path (no pool overallocation).
    [TestCase(-1)]
    [TestCase(5)]
    public void Ctor_starting_count_out_of_range_throws(int badCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => { using NativeMemoryList<int> _ = new(4, badCount); });
        Assert.Throws<ArgumentOutOfRangeException>(() => CtorRef(badCount));

        static void CtorRef(int bad) { NativeMemoryListRef<int> _ = new(4, bad); }
    }
}
