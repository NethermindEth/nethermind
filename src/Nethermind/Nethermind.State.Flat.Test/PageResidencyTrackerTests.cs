// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.Storage;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

public class PageResidencyTrackerTests
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

    /// <summary>
    /// Minimal <see cref="IArenaManager"/> stub for <see cref="ArenaByteReader"/> tests:
    /// forwards <see cref="IArenaManager.TouchPage"/> into the supplied tracker + handler so
    /// test assertions on tracker state and recorded evictions still work after the reader
    /// stopped depending on those primitives directly.
    /// </summary>
    private sealed unsafe class StubArenaManager(PageResidencyTracker tracker, IPageEvictionHandler handler) : IArenaManager
    {
        public void TouchPage(int arenaId, int pageIdx)
        {
            if (tracker.TryTouch(arenaId, pageIdx, out int evictedArenaId, out int evictedPageIdx))
                handler.OnPageEvicted(evictedArenaId, evictedPageIdx);
        }
        public int ArenaFileCount => 0;
        public long ArenaMappedBytes => 0;
        public void Initialize(IReadOnlyList<SnapshotCatalog.CatalogEntry> entries) => throw new NotSupportedException();
        public ArenaWriter CreateWriter(long estimatedSize, string tag) => throw new NotSupportedException();
        public (SnapshotLocation Location, ArenaReservation Reservation) CompleteWrite(int arenaId, long startOffset, long actualSize, string tag) => throw new NotSupportedException();
        public void CancelWrite(int arenaId, long startOffset) => throw new NotSupportedException();
        public ArenaReservation Open(in SnapshotLocation location, string tag) => throw new NotSupportedException();
        public ReadOnlySpan<byte> GetSpan(ArenaReservation reservation) => throw new NotSupportedException();
        public IArenaWholeView OpenWholeView(ArenaReservation reservation) => throw new NotSupportedException();
        public IArenaWholeView OpenPendingView(int arenaId, long absoluteOffset, long size) => throw new NotSupportedException();
        public void GetReservationPointer(ArenaReservation reservation, out byte* dataPtr, out long size) => throw new NotSupportedException();
        public void MarkDead(in SnapshotLocation location) => throw new NotSupportedException();
        public void AdviseDontNeed(ArenaReservation reservation) => throw new NotSupportedException();
        public void Touch(ArenaReservation reservation, long subOffset, long size) => throw new NotSupportedException();
        public void Dispose() { }
    }

    /// <summary>
    /// Touch wrapper used by tests that exercise the tracker directly: pumps any displaced
    /// key into <paramref name="handler"/>, mirroring what <see cref="ArenaByteReader"/>
    /// does in production now that eviction dispatch lives at the call site.
    /// </summary>
    private static void Touch(PageResidencyTracker tracker, int arenaId, int pageIdx, IPageEvictionHandler? handler = null)
    {
        if (tracker.TryTouch(arenaId, pageIdx, out int evictedArenaId, out int evictedPageIdx))
            handler?.OnPageEvicted(evictedArenaId, evictedPageIdx);
    }

    [Test]
    public void Touch_RepeatedSamePage_NeverEvicts()
    {
        RecordingHandler handler = new();
        PageResidencyTracker tracker = new(maxCapacity: 4);

        for (int i = 0; i < 1000; i++)
            Touch(tracker, 7, 42, handler);

        handler.Evictions.Should().BeEmpty();
        tracker.Count.Should().Be(1);
        tracker.ContainsPage(7, 42).Should().BeTrue();
    }

    [Test]
    public void Touch_SingleSlot_CollisionEvictsOccupant()
    {
        // maxCapacity=1 → every distinct key collides on the only slot.
        RecordingHandler handler = new();
        PageResidencyTracker tracker = new(maxCapacity: 1);

        Touch(tracker, 0, 0, handler);
        handler.Evictions.Should().BeEmpty();
        tracker.ContainsPage(0, 0).Should().BeTrue();

        Touch(tracker, 0, 1, handler);
        handler.Evictions.Should().ContainSingle().Which.Should().Be((0, 0));
        tracker.ContainsPage(0, 0).Should().BeFalse();
        tracker.ContainsPage(0, 1).Should().BeTrue();

        Touch(tracker, 0, 2, handler);
        handler.Evictions.Should().HaveCount(2);
        handler.Evictions[1].Should().Be((0, 1));
    }

    [Test]
    public void TryTouch_ReturnsDisplacedKeyDirectly()
    {
        PageResidencyTracker tracker = new(maxCapacity: 1);

        tracker.TryTouch(0, 0, out _, out _).Should().BeFalse();
        tracker.TryTouch(0, 1, out int evictedArenaId, out int evictedPageIdx).Should().BeTrue();
        evictedArenaId.Should().Be(0);
        evictedPageIdx.Should().Be(0);

        // Re-touching the current occupant must NOT report itself as evicted.
        tracker.TryTouch(0, 1, out _, out _).Should().BeFalse();
    }

    [Test]
    public void MaxCapacityZero_TouchIsNoOp()
    {
        RecordingHandler handler = new();
        PageResidencyTracker tracker = new(maxCapacity: 0);
        Touch(tracker, 1, 1, handler);
        Touch(tracker, 2, 2, handler);
        handler.Evictions.Should().BeEmpty();
        tracker.Count.Should().Be(0);
        tracker.ContainsPage(1, 1).Should().BeFalse();
    }

    [Test]
    public void MaxCapacity_RoundsUpToPowerOfTwo()
    {
        PageResidencyTracker tracker = new(maxCapacity: 3);
        tracker.MaxCapacity.Should().Be(4);
    }

    [Test]
    public void Clear_RemovesAllEntries()
    {
        RecordingHandler handler = new();
        PageResidencyTracker tracker = new(maxCapacity: 8);
        Touch(tracker, 0, 0, handler);
        Touch(tracker, 0, 1, handler);
        Touch(tracker, 0, 2, handler);

        tracker.Clear();
        tracker.Count.Should().Be(0);
        tracker.ContainsPage(0, 0).Should().BeFalse();
        tracker.ContainsPage(0, 1).Should().BeFalse();
        tracker.ContainsPage(0, 2).Should().BeFalse();
        // Clear must not invoke the eviction handler — pages dropped wholesale, not displaced.
        handler.Evictions.Should().BeEmpty();
    }

    [Test]
    public unsafe void ArenaByteReader_TryRead_TouchesAllSpannedPages()
    {
        PageResidencyTracker tracker = new(maxCapacity: 1024);
        int pageSize = Environment.SystemPageSize;
        long baseOffset = pageSize - 8;
        byte[] data = new byte[pageSize * 2];
        fixed (byte* dataPtr = data)
        {
            ArenaByteReader reader = new(dataPtr, data.Length, new StubArenaManager(tracker, NoopHandler.Instance), arenaId: 9, baseOffset: baseOffset);

            Span<byte> sink = stackalloc byte[16];
            reader.TryRead(0, sink).Should().BeTrue();

            int firstPage = (int)(baseOffset / pageSize);
            int lastPage = (int)((baseOffset + 15) / pageSize);
            firstPage.Should().NotBe(lastPage, "test setup must straddle a page boundary");
            tracker.ContainsPage(9, firstPage).Should().BeTrue();
            tracker.ContainsPage(9, lastPage).Should().BeTrue();
        }
    }

    [Test]
    public unsafe void ArenaByteReader_PinBuffer_TouchesAllSpannedPages()
    {
        PageResidencyTracker tracker = new(maxCapacity: 1024);
        int pageSize = Environment.SystemPageSize;
        byte[] data = new byte[pageSize * 3];
        fixed (byte* dataPtr = data)
        {
            ArenaByteReader reader = new(dataPtr, data.Length, new StubArenaManager(tracker, NoopHandler.Instance), arenaId: 1, baseOffset: 0);

            using NoOpPin pin = reader.PinBuffer(0, pageSize * 2 + 1);
            pin.Buffer.Length.Should().Be(pageSize * 2 + 1);
            tracker.ContainsPage(1, 0).Should().BeTrue();
            tracker.ContainsPage(1, 1).Should().BeTrue();
            tracker.ContainsPage(1, 2).Should().BeTrue();
        }
    }

    [Test]
    public unsafe void ArenaByteReader_DispatchesEvictionsToHandler()
    {
        // maxCapacity=1 forces every Touch to evict whatever was there.
        RecordingHandler handler = new();
        PageResidencyTracker tracker = new(maxCapacity: 1);
        int pageSize = Environment.SystemPageSize;
        byte[] data = new byte[pageSize * 2];
        fixed (byte* dataPtr = data)
        {
            ArenaByteReader reader = new(dataPtr, data.Length, new StubArenaManager(tracker, handler), arenaId: 5, baseOffset: 0);

            Span<byte> b = stackalloc byte[1];
            reader.TryRead(0, b).Should().BeTrue();           // primes (5,0)
            reader.TryRead(pageSize, b).Should().BeTrue();    // crosses to page 1 → evicts (5,0)

            handler.Evictions.Should().ContainSingle().Which.Should().Be((5, 0));
        }
    }

    [Test]
    public unsafe void ArenaByteReader_RepeatedSamePageReads_OnlyTouchOnce()
    {
        // maxCapacity=1: every Touch lands on the only slot. We probe the memo
        // by forcing a sentinel back into the slot before each read and checking
        // whether the next read displaced it. If ArenaByteReader's memo is
        // working, repeated reads on the same page must NOT call Touch and the
        // sentinel must remain.
        PageResidencyTracker tracker = new(maxCapacity: 1);
        int pageSize = Environment.SystemPageSize;
        byte[] data = new byte[pageSize * 2];
        fixed (byte* dataPtr = data)
        {
            ArenaByteReader reader = new(dataPtr, data.Length, new StubArenaManager(tracker, NoopHandler.Instance), arenaId: 0, baseOffset: 0);

            Span<byte> b = stackalloc byte[1];

            // First read materializes (0,0) in the slot.
            reader.TryRead(0, b).Should().BeTrue();
            tracker.ContainsPage(0, 0).Should().BeTrue();

            // 99 more reads on page 0 — memo path must not Touch.
            for (int i = 1; i < 100; i++)
            {
                Touch(tracker, 99, 99);
                reader.TryRead(i, b).Should().BeTrue();
                tracker.ContainsPage(99, 99).Should().BeTrue("memo must skip Touch for same page");
                tracker.ContainsPage(0, 0).Should().BeFalse();
            }

            // Crossing into page 1 must invalidate the memo and Touch exactly once.
            Touch(tracker, 99, 99);
            reader.TryRead(pageSize, b).Should().BeTrue();
            tracker.ContainsPage(0, 1).Should().BeTrue("page boundary must invalidate the memo");
            tracker.ContainsPage(99, 99).Should().BeFalse();

            // Still on page 1 — memo holds again.
            Touch(tracker, 99, 99);
            reader.TryRead(pageSize + 4, b).Should().BeTrue();
            tracker.ContainsPage(99, 99).Should().BeTrue();
        }
    }

    [Test]
    public unsafe void ArenaByteReader_DisabledTracker_DoesNotThrow()
    {
        // Capacity-0 tracker is the "disabled" form — TryTouch is a no-op, no allocation.
        using PageResidencyTracker disabled = new(maxCapacity: 0);
        byte[] data = new byte[64];
        fixed (byte* dataPtr = data)
        {
            ArenaByteReader reader = new(dataPtr, data.Length, new StubArenaManager(disabled, NoopHandler.Instance), arenaId: 0, baseOffset: 0);
            Span<byte> sink = stackalloc byte[8];
            reader.TryRead(4, sink).Should().BeTrue();
            using NoOpPin pin = reader.PinBuffer(0, 16);
            pin.Buffer.Length.Should().Be(16);
        }
    }
}
