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
    /// exposes the supplied tracker via <see cref="PageTracker"/> so an
    /// <see cref="ArenaReservation"/> can call into it directly, and forwards
    /// <see cref="IArenaManager.AdviseDontNeedPage"/> into <paramref name="handler"/> so test
    /// assertions on cross-arena evictions still work. Same-arena evictions skip this stub
    /// entirely (the reservation handles them directly off its captured ArenaFile, which is
    /// null in tests so they no-op silently).
    /// </summary>
    private sealed unsafe class StubArenaManager(PageResidencyTracker tracker, IPageEvictionHandler handler) : IArenaManager
    {
        public PageResidencyTracker PageTracker => tracker;
        public void AdviseDontNeedPage(int arenaId, int pageIdx) => handler.OnPageEvicted(arenaId, pageIdx);
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
        // No-op so reservation disposal doesn't blow up in tests.
        public void MarkDead(in SnapshotLocation location) { }
        public void AdviseDontNeed(ArenaReservation reservation) { }
        public void Touch(ArenaReservation reservation, long subOffset, long size) { }
        public void Dispose() { }
    }

    /// <summary>
    /// Touch wrapper used by tests that exercise the tracker directly: pumps any displaced
    /// key into <paramref name="handler"/>, mirroring what <see cref="ArenaByteReader"/>
    /// does in production now that eviction dispatch lives at the call site.
    /// </summary>
    private static void Touch(PageResidencyTracker tracker, int arenaId, int pageIdx, IPageEvictionHandler? handler = null)
    {
        if (tracker.TryTouch(arenaId, pageIdx, out int evictedArenaId, out int evictedPageIdx) == TouchOutcome.Evicted)
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
    public void TryTouch_ReturnsOutcomeAndDisplacedKey()
    {
        PageResidencyTracker tracker = new(maxCapacity: 1);

        // Empty slot: Inserted, no displaced key.
        tracker.TryTouch(0, 0, out _, out _).Should().Be(TouchOutcome.Inserted);

        // Different key on the same slot: Evicted, with displaced key surfaced.
        tracker.TryTouch(0, 1, out int evictedArenaId, out int evictedPageIdx).Should().Be(TouchOutcome.Evicted);
        evictedArenaId.Should().Be(0);
        evictedPageIdx.Should().Be(0);

        // Re-touching the current occupant: Hit.
        tracker.TryTouch(0, 1, out _, out _).Should().Be(TouchOutcome.Hit);
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

    private static ArenaReservation MakeReservation(IArenaManager manager, int arenaId, long offset, long size, string tag = "test") =>
        new(manager, arenaFile: null, arenaId, offset, size, tag);

    [Test]
    public unsafe void ArenaByteReader_TryRead_TouchesAllSpannedPages()
    {
        PageResidencyTracker tracker = new(maxCapacity: 1024);
        int pageSize = Environment.SystemPageSize;
        long baseOffset = pageSize - 8;
        byte[] data = new byte[pageSize * 2];
        fixed (byte* dataPtr = data)
        {
            using ArenaReservation reservation = MakeReservation(
                new StubArenaManager(tracker, NoopHandler.Instance), arenaId: 9, offset: baseOffset, size: data.Length);
            ArenaByteReader reader = new(dataPtr, data.Length, reservation);

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
            using ArenaReservation reservation = MakeReservation(
                new StubArenaManager(tracker, NoopHandler.Instance), arenaId: 1, offset: 0, size: data.Length);
            ArenaByteReader reader = new(dataPtr, data.Length, reservation);

            using NoOpPin pin = reader.PinBuffer(0, pageSize * 2 + 1);
            pin.Buffer.Length.Should().Be(pageSize * 2 + 1);
            tracker.ContainsPage(1, 0).Should().BeTrue();
            tracker.ContainsPage(1, 1).Should().BeTrue();
            tracker.ContainsPage(1, 2).Should().BeTrue();
        }
    }

    [Test]
    public unsafe void ArenaByteReader_DispatchesCrossArenaEvictionsToHandler()
    {
        // maxCapacity=1 → every distinct (arenaId, pageIdx) collides on the only slot.
        // Use two arenas (5 and 6) on the same shared tracker so the eviction crosses arenas:
        // the only path that surfaces evictions to the handler now that same-arena evictions
        // go directly through the reservation's ArenaFile reference (null in tests, so silently
        // skipped).
        RecordingHandler handler = new();
        PageResidencyTracker tracker = new(maxCapacity: 1);
        StubArenaManager manager = new(tracker, handler);
        int pageSize = Environment.SystemPageSize;
        byte[] data = new byte[pageSize];
        fixed (byte* dataPtr = data)
        {
            using ArenaReservation r5 = MakeReservation(manager, arenaId: 5, offset: 0, size: data.Length, tag: "r5");
            using ArenaReservation r6 = MakeReservation(manager, arenaId: 6, offset: 0, size: data.Length, tag: "r6");
            ArenaByteReader reader5 = new(dataPtr, data.Length, r5);
            ArenaByteReader reader6 = new(dataPtr, data.Length, r6);

            Span<byte> b = stackalloc byte[1];
            reader5.TryRead(0, b).Should().BeTrue();   // primes (5, 0)
            reader6.TryRead(0, b).Should().BeTrue();   // collides → evicts (5, 0); cross-arena → handler

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
            using ArenaReservation reservation = MakeReservation(
                new StubArenaManager(tracker, NoopHandler.Instance), arenaId: 0, offset: 0, size: data.Length);
            ArenaByteReader reader = new(dataPtr, data.Length, reservation);

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
            using ArenaReservation reservation = MakeReservation(
                new StubArenaManager(disabled, NoopHandler.Instance), arenaId: 0, offset: 0, size: data.Length);
            ArenaByteReader reader = new(dataPtr, data.Length, reservation);
            Span<byte> sink = stackalloc byte[8];
            reader.TryRead(4, sink).Should().BeTrue();
            using NoOpPin pin = reader.PinBuffer(0, 16);
            pin.Buffer.Length.Should().Be(16);
        }
    }
}
