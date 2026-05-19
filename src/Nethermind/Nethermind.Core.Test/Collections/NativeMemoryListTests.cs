// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
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
        list.Count.Should().Be(0);
        list.Capacity.Should().Be(1024);

        using NativeMemoryList<int> empty = new(0);
        empty.Should().BeEmpty();
        empty.Add(1);
        empty.Count.Should().Be(1);
        empty.Remove(1).Should().BeTrue();
        empty.Count.Should().Be(0);
        empty.Add(2);
        empty[0].Should().Be(2);
    }

    [Test]
    public void Add_AddRange_and_growth()
    {
        using NativeMemoryList<int> list = new(4);
        list.AddRange(Enumerable.Range(0, 50).ToArray());
        list.Should().BeEquivalentTo(Enumerable.Range(0, 50));
        list.Count.Should().Be(50);
        list.Capacity.Should().BeGreaterThanOrEqualTo(50);

        list.Add(123);
        list[50].Should().Be(123);
    }

    [Test]
    public void Clear_resets_count_only()
    {
        using NativeMemoryList<int> list = new(8);
        list.AddRange(stackalloc int[] { 1, 2, 3 });
        int before = list.Capacity;
        list.Clear();
        list.Count.Should().Be(0);
        list.Capacity.Should().Be(before);
        list.Add(99);
        list[0].Should().Be(99);
    }

    [TestCase(0)]
    [TestCase(2)]
    [TestCase(4)]
    public void Insert_RemoveAt_at_various_indices(int index)
    {
        using NativeMemoryList<int> list = new(8);
        list.AddRange(stackalloc int[] { 0, 1, 2, 3, 4 });
        list.Insert(index, 99);
        list[index].Should().Be(99);
        list.Count.Should().Be(6);

        list.RemoveAt(index);
        list.Should().BeEquivalentTo(new[] { 0, 1, 2, 3, 4 });
    }

    [Test]
    public void IndexOf_Contains_Remove_work()
    {
        using NativeMemoryList<int> list = new(4, [10, 20, 30]);
        list.IndexOf(20).Should().Be(1);
        list.Contains(30).Should().BeTrue();
        list.Contains(99).Should().BeFalse();
        list.Remove(20).Should().BeTrue();
        list.Should().BeEquivalentTo(new[] { 10, 30 });
        list.Remove(99).Should().BeFalse();
    }

    [Test]
    public void GetRef_returns_writable_reference()
    {
        using NativeMemoryList<int> list = new(2, 2);
        ref int slot = ref list.GetRef(1);
        slot = 42;
        list[1].Should().Be(42);
    }

    [Test]
    public void AsSpan_reflects_count()
    {
        using NativeMemoryList<int> list = new(4);
        list.AddRange(stackalloc int[] { 1, 2, 3 });
        list.AsSpan().Length.Should().Be(3);
        list.AsSpan()[1].Should().Be(2);
    }

    [Test]
    public void Sort_and_Reverse()
    {
        using NativeMemoryList<int> list = new(4, [3, 1, 4, 1, 5, 9, 2, 6]);
        list.Sort((a, b) => a.CompareTo(b));
        list.Should().BeEquivalentTo(new[] { 1, 1, 2, 3, 4, 5, 6, 9 }, o => o.WithStrictOrdering());
        list.Reverse();
        list.Should().BeEquivalentTo(new[] { 9, 6, 5, 4, 3, 2, 1, 1 }, o => o.WithStrictOrdering());
    }

    [Test]
    public void Truncate_and_ReduceCount()
    {
        using NativeMemoryList<int> list = new(64);
        list.AddRange(Enumerable.Range(0, 50).ToArray());

        list.Truncate(10);
        list.Count.Should().Be(10);

        list.ReduceCount(2);
        list.Count.Should().Be(2);
        list.Should().BeEquivalentTo(new[] { 0, 1 }, o => o.WithStrictOrdering());
    }

    [Test]
    public void CopyTo_writes_into_destination_array()
    {
        using NativeMemoryList<int> list = new(4, [1, 2, 3]);
        int[] dest = new int[5];
        list.CopyTo(dest, 1);
        dest.Should().BeEquivalentTo(new[] { 0, 1, 2, 3, 0 }, o => o.WithStrictOrdering());
    }

    [Test]
    public void Dispose_is_idempotent_and_post_dispose_throws()
    {
        NativeMemoryList<int> list = new(4, [1, 2, 3]);
        list.Dispose();
        list.Dispose();
        Action act = () => _ = list[0];
        act.Should().Throw<ObjectDisposedException>();
    }

    [Test]
    public void IList_interface_compliance()
    {
        using NativeMemoryList<int> list = new(4);
        IList ilist = list;
        ilist.Add(1).Should().Be(0);
        ilist.Add(2).Should().Be(1);
        ilist.Contains(1).Should().BeTrue();
        ilist.IndexOf(2).Should().Be(1);
        ilist[0] = 99;
        ilist[0].Should().Be(99);
        ilist.Insert(0, 7);
        ilist[0].Should().Be(7);
        ilist.Remove(7);
        ilist.Count.Should().Be(2);
    }

    [Test]
    public void Ref_struct_round_trip()
    {
        NativeMemoryListRef<int> r = new(4);
        try
        {
            r.AddRange(stackalloc int[] { 1, 2, 3 });
            r.Count.Should().Be(3);
            r[1].Should().Be(2);
            r.AsSpan().ToArray().Should().BeEquivalentTo(new[] { 1, 2, 3 }, o => o.WithStrictOrdering());
            r.Add(4);
            r.AsSpan()[3].Should().Be(4);
            r.Insert(0, 0);
            r.AsSpan().ToArray().Should().BeEquivalentTo(new[] { 0, 1, 2, 3, 4 }, o => o.WithStrictOrdering());
            r.RemoveAt(0);
            r.AsSpan().ToArray().Should().BeEquivalentTo(new[] { 1, 2, 3, 4 }, o => o.WithStrictOrdering());
            r.Clear();
            r.Count.Should().Be(0);
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
            r.Count.Should().Be(1000);
            r.Capacity.Should().BeGreaterThanOrEqualTo(1000);
            r[0].Should().Be(0L);
            r[999].Should().Be(999L);
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
            r.Capacity.Should().BeGreaterThanOrEqualTo(4096);
        }
        finally { r.Dispose(); }
    }

    [Test]
    public void Empty_constructor_returns_disposable_zero_capacity()
    {
        using NativeMemoryList<int> empty = NativeMemoryList<int>.Empty();
        empty.Count.Should().Be(0);
        empty.Capacity.Should().Be(0);
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
        list.Count.Should().Be(0);
        list.Capacity.Should().BeGreaterThanOrEqualTo(capacity);

        list.AddRange(Bytes.FromHexString("deadbeef"));
        list.Count.Should().Be(4);
        list[0].Should().Be(0xde);
        list[3].Should().Be(0xef);

        list.Insert(0, 0x01);
        list[0].Should().Be(0x01);
        list[4].Should().Be(0xef);

        list.RemoveAt(0);
        list.AsSpan().ToArray().Should().BeEquivalentTo(Bytes.FromHexString("deadbeef"), o => o.WithStrictOrdering());

        list.Reverse();
        list[0].Should().Be(0xef);
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

        list.Count.Should().Be(payload.Length);
        list.Capacity.Should().BeGreaterThanOrEqualTo(payload.Length);
        list.AsSpan().ToArray().Should().BeEquivalentTo(payload, o => o.WithStrictOrdering());
    }

    // ReduceCount shrinks below the byte threshold; the internal reallocation must route to
    // ArrayPool and the data must remain readable.
    [Test]
    public void ReduceCount_downgrades_native_to_pool_when_under_threshold()
    {
        using NativeMemoryList<long> list = new(256);  // 256 * 8 = 2 KiB → native
        for (int i = 0; i < 256; i++) list.Add(i);

        list.ReduceCount(8);  // 8 * 8 = 64 bytes → pool
        list.Count.Should().Be(8);
        list[0].Should().Be(0L);
        list[7].Should().Be(7L);
    }
}
