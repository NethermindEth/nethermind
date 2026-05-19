// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
    public void AsSpan_reflects_count()
    {
        using NativeMemoryList<int> list = new(4);
        list.AddRange(stackalloc int[] { 1, 2, 3 });
        list.AsSpan().Length.Should().Be(3);
        list.AsSpan()[1].Should().Be(2);
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
    }

    // sizeof(long) == 8 is a power of two, so the native path must route through
    // AlignedAlloc(sizeof(T)) instead of plain malloc — the returned buffer address must
    // be aligned to sizeof(T).
    [TestCase(typeof(long), 256)]   // 2 KiB → native
    [TestCase(typeof(int), 512)]    // 2 KiB → native
    public unsafe void Native_path_buffer_is_aligned_to_sizeof_T_for_pow2_sizes(Type elementType, int capacity)
    {
        if (elementType == typeof(long))
        {
            using NativeMemoryList<long> list = new(capacity);
            for (int i = 0; i < capacity; i++) list.Add(i);
            nuint addr = (nuint)Unsafe.AsPointer(ref MemoryMarshal.GetReference(list.AsSpan()));
            (addr % (nuint)sizeof(long)).Should().Be((nuint)0);
        }
        else
        {
            using NativeMemoryList<int> list = new(capacity);
            for (int i = 0; i < capacity; i++) list.Add(i);
            nuint addr = (nuint)Unsafe.AsPointer(ref MemoryMarshal.GetReference(list.AsSpan()));
            (addr % (nuint)sizeof(int)).Should().Be((nuint)0);
        }
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
}
