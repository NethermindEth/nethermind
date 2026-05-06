// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.Storage;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

public class PageSlotCacheTests
{
    private sealed class RecordingHandler : IPageEvictionHandler
    {
        public readonly List<(int arena, int page)> Evictions = [];
        public void OnPageEvicted(int arenaId, int pageIdx) => Evictions.Add((arenaId, pageIdx));
    }

    private sealed class NoopHandler : IPageEvictionHandler
    {
        public static readonly NoopHandler Instance = new();
        public void OnPageEvicted(int arenaId, int pageIdx) { }
    }

    [Test]
    public void Touch_RepeatedSamePage_NeverEvicts()
    {
        RecordingHandler handler = new();
        PageSlotCache cache = new(maxCapacity: 4, handler);

        for (int i = 0; i < 1000; i++)
            cache.Touch(7, 42);

        handler.Evictions.Should().BeEmpty();
        cache.Count.Should().Be(1);
        cache.ContainsPage(7, 42).Should().BeTrue();
    }

    [Test]
    public void Touch_SingleSlot_CollisionEvictsOccupant()
    {
        // maxCapacity=1 → every distinct key collides on the only slot.
        RecordingHandler handler = new();
        PageSlotCache cache = new(maxCapacity: 1, handler);

        cache.Touch(0, 0);
        handler.Evictions.Should().BeEmpty();
        cache.ContainsPage(0, 0).Should().BeTrue();

        cache.Touch(0, 1);
        handler.Evictions.Should().ContainSingle().Which.Should().Be((0, 0));
        cache.ContainsPage(0, 0).Should().BeFalse();
        cache.ContainsPage(0, 1).Should().BeTrue();

        cache.Touch(0, 2);
        handler.Evictions.Should().HaveCount(2);
        handler.Evictions[1].Should().Be((0, 1));
    }

    [Test]
    public void MaxCapacityZero_TouchIsNoOp()
    {
        RecordingHandler handler = new();
        PageSlotCache cache = new(maxCapacity: 0, handler);
        cache.Touch(1, 1);
        cache.Touch(2, 2);
        handler.Evictions.Should().BeEmpty();
        cache.Count.Should().Be(0);
        cache.ContainsPage(1, 1).Should().BeFalse();
    }

    [Test]
    public void MaxCapacity_RoundsUpToPowerOfTwo()
    {
        PageSlotCache cache = new(maxCapacity: 3, NoopHandler.Instance);
        cache.MaxCapacity.Should().Be(4);
    }

    [Test]
    public void Clear_RemovesAllEntries()
    {
        RecordingHandler handler = new();
        PageSlotCache cache = new(maxCapacity: 8, handler);
        cache.Touch(0, 0);
        cache.Touch(0, 1);
        cache.Touch(0, 2);

        cache.Clear();
        cache.Count.Should().Be(0);
        cache.ContainsPage(0, 0).Should().BeFalse();
        cache.ContainsPage(0, 1).Should().BeFalse();
        cache.ContainsPage(0, 2).Should().BeFalse();
        // Clear must not invoke the eviction handler — pages dropped wholesale, not displaced.
        handler.Evictions.Should().BeEmpty();
    }

    [Test]
    public void ArenaByteReader_TryRead_TouchesAllSpannedPages()
    {
        PageSlotCache cache = new(maxCapacity: 1024, NoopHandler.Instance);
        int pageSize = Environment.SystemPageSize;
        long baseOffset = pageSize - 8;
        byte[] data = new byte[pageSize * 2];
        ArenaByteReader reader = new(data, cache, arenaId: 9, baseOffset: baseOffset);

        Span<byte> sink = stackalloc byte[16];
        reader.TryRead(0, sink).Should().BeTrue();

        int firstPage = (int)(baseOffset / pageSize);
        int lastPage = (int)((baseOffset + 15) / pageSize);
        firstPage.Should().NotBe(lastPage, "test setup must straddle a page boundary");
        cache.ContainsPage(9, firstPage).Should().BeTrue();
        cache.ContainsPage(9, lastPage).Should().BeTrue();
    }

    [Test]
    public void ArenaByteReader_PinBuffer_TouchesAllSpannedPages()
    {
        PageSlotCache cache = new(maxCapacity: 1024, NoopHandler.Instance);
        int pageSize = Environment.SystemPageSize;
        byte[] data = new byte[pageSize * 3];
        ArenaByteReader reader = new(data, cache, arenaId: 1, baseOffset: 0);

        using NoOpPin pin = reader.PinBuffer(0, pageSize * 2 + 1);
        pin.Buffer.Length.Should().Be(pageSize * 2 + 1);
        cache.ContainsPage(1, 0).Should().BeTrue();
        cache.ContainsPage(1, 1).Should().BeTrue();
        cache.ContainsPage(1, 2).Should().BeTrue();
    }

    [Test]
    public void ArenaByteReader_RepeatedSamePageReads_OnlyTouchOnce()
    {
        // maxCapacity=1: every Touch lands on the only slot. We probe the memo
        // by forcing a sentinel back into the slot before each read and checking
        // whether the next read displaced it. If ArenaByteReader's memo is
        // working, repeated reads on the same page must NOT call Touch and the
        // sentinel must remain.
        PageSlotCache cache = new(maxCapacity: 1, NoopHandler.Instance);
        int pageSize = Environment.SystemPageSize;
        byte[] data = new byte[pageSize * 2];
        ArenaByteReader reader = new(data, cache, arenaId: 0, baseOffset: 0);

        Span<byte> b = stackalloc byte[1];

        // First read materializes (0,0) in the slot.
        reader.TryRead(0, b).Should().BeTrue();
        cache.ContainsPage(0, 0).Should().BeTrue();

        // 99 more reads on page 0 — memo path must not Touch.
        for (int i = 1; i < 100; i++)
        {
            cache.Touch(99, 99);
            reader.TryRead(i, b).Should().BeTrue();
            cache.ContainsPage(99, 99).Should().BeTrue("memo must skip Touch for same page");
            cache.ContainsPage(0, 0).Should().BeFalse();
        }

        // Crossing into page 1 must invalidate the memo and Touch exactly once.
        cache.Touch(99, 99);
        reader.TryRead(pageSize, b).Should().BeTrue();
        cache.ContainsPage(0, 1).Should().BeTrue("page boundary must invalidate the memo");
        cache.ContainsPage(99, 99).Should().BeFalse();

        // Still on page 1 — memo holds again.
        cache.Touch(99, 99);
        reader.TryRead(pageSize + 4, b).Should().BeTrue();
        cache.ContainsPage(99, 99).Should().BeTrue();
    }

    [Test]
    public void ArenaByteReader_NullCache_DoesNotThrow()
    {
        byte[] data = new byte[64];
        ArenaByteReader reader = new(data, cache: null, arenaId: 0, baseOffset: 0);
        Span<byte> sink = stackalloc byte[8];
        reader.TryRead(4, sink).Should().BeTrue();
        using NoOpPin pin = reader.PinBuffer(0, 16);
        pin.Buffer.Length.Should().Be(16);
    }
}
