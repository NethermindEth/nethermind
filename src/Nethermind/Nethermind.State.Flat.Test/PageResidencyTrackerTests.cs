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
    // The tracker is 8-way set-associative; tests that need a known eviction outcome use a
    // single-set tracker (Capacity=8) so every distinct key lands in the same set and the
    // clock order is fully determined.
    private const int Ways = 8;
    private const int OneSetCapacity = Ways;

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
        PageResidencyTracker tracker = new(maxCapacity: OneSetCapacity);

        for (int i = 0; i < 1000; i++)
            Touch(tracker, 7, 42, handler);

        handler.Evictions.Should().BeEmpty();
        tracker.Count.Should().Be(1);
        tracker.ContainsPage(7, 42).Should().BeTrue();
    }

    [Test]
    public void Set_FullWithUnreferencedSlots_NextTouchEvictsClockVictim()
    {
        // Single-set tracker → all keys land in set 0. Insert 8 distinct keys; each insertion
        // arms its REF bit. The 9th touch must:
        //   1) clear all 8 REF bits on the first clock pass,
        //   2) evict way 0 (the head of the clock) on the wrap-around pass,
        //   3) report (0, 0) — the first inserted key — as the displaced key.
        RecordingHandler handler = new();
        PageResidencyTracker tracker = new(OneSetCapacity);

        for (int i = 0; i < Ways; i++)
            Touch(tracker, 0, i, handler);
        handler.Evictions.Should().BeEmpty();
        tracker.Count.Should().Be(Ways);

        Touch(tracker, 0, Ways, handler);
        handler.Evictions.Should().ContainSingle().Which.Should().Be((0, 0));
        tracker.ContainsPage(0, 0).Should().BeFalse();
        tracker.ContainsPage(0, Ways).Should().BeTrue();
        tracker.Count.Should().Be(Ways);
    }

    [Test]
    public void TryTouch_ReturnsOutcomeAndDisplacedKey()
    {
        PageResidencyTracker tracker = new(OneSetCapacity);

        // Empty set: Inserted, no displaced key.
        tracker.TryTouch(0, 0, out _, out _).Should().Be(TouchOutcome.Inserted);

        // Re-touching the same key: Hit.
        tracker.TryTouch(0, 0, out _, out _).Should().Be(TouchOutcome.Hit);

        // Fill the remaining 7 ways — all Inserted.
        for (int i = 1; i < Ways; i++)
            tracker.TryTouch(0, i, out _, out _).Should().Be(TouchOutcome.Inserted);

        // Set is full and all REFs are armed. The 9th touch evicts the clock head (0, 0).
        tracker.TryTouch(0, Ways, out int evictedArenaId, out int evictedPageIdx).Should().Be(TouchOutcome.Evicted);
        evictedArenaId.Should().Be(0);
        evictedPageIdx.Should().Be(0);
    }

    [Test]
    public void ReferenceBit_GivesSecondChance()
    {
        // After the first eviction at step (1) below, ways 1..7 have their REF bits cleared
        // (the clock arm wiped them on its first pass) while way 0 holds the freshly-inserted
        // key with REF=1. Re-touching the key in (say) way 3 re-arms its REF. The next eviction
        // must skip way 3 and evict the next REF=0 way the hand encounters — way 1 — proving
        // the second-chance semantic.
        RecordingHandler handler = new();
        PageResidencyTracker tracker = new(OneSetCapacity);

        // Step 0: fill the set with (0,0) .. (0,7). All REF=1.
        for (int i = 0; i < Ways; i++)
            Touch(tracker, 0, i, handler);

        // Step 1: insert (0, 8). Clock clears all REFs, evicts way 0 → (0,0). Hand now at 1.
        Touch(tracker, 0, 8, handler);
        handler.Evictions.Should().ContainSingle().Which.Should().Be((0, 0));

        // Step 2: re-touch (0, 3) — sets its REF bit back to 1. Way 0's (0,8) REF is also 1.
        Touch(tracker, 0, 3, handler);
        handler.Evictions.Should().HaveCount(1, "re-touching is a Hit, not an eviction");

        // Step 3: insert (0, 9). Hand starts at 1 (way 1 has REF=0 since the previous pass
        // cleared it and nothing re-touched it) → evicts (0, 1). (0, 3) survives.
        Touch(tracker, 0, 9, handler);
        handler.Evictions.Should().HaveCount(2);
        handler.Evictions[1].Should().Be((0, 1));
        tracker.ContainsPage(0, 3).Should().BeTrue("re-touched key got a second chance");
        tracker.ContainsPage(0, 9).Should().BeTrue();
    }

    [Test]
    public void RefBit_ClearedOnSecondPass_ExactlyOneEviction()
    {
        // Fill the set; every way has REF=1. The very next miss must clear all 8 REFs on the
        // first clock pass and evict exactly one entry on the wrap-around.
        RecordingHandler handler = new();
        PageResidencyTracker tracker = new(OneSetCapacity);
        for (int i = 0; i < Ways; i++)
            Touch(tracker, 0, i, handler);

        Touch(tracker, 0, Ways, handler);
        handler.Evictions.Should().ContainSingle();
        tracker.Count.Should().Be(Ways);
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

    [TestCase(1, Ways)]
    [TestCase(Ways, Ways)]
    [TestCase(Ways + 1, 2 * Ways)]
    [TestCase(3 * Ways, 4 * Ways)]
    public void MaxCapacity_RoundsUpToWayMultipleOfPowerOfTwoSets(int requested, int expected)
    {
        PageResidencyTracker tracker = new(maxCapacity: requested);
        tracker.MaxCapacity.Should().Be(expected);
    }

    [Test]
    public void Clear_RemovesAllEntries()
    {
        RecordingHandler handler = new();
        PageResidencyTracker tracker = new(maxCapacity: OneSetCapacity);
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
        // Fill the only set with 8 reads from arena 5, then read from arena 6 to force a clock
        // eviction. The displaced key has arenaId=5, so it crosses arenas and surfaces through
        // the handler (same-arena evictions go directly through the reservation's ArenaFile,
        // which is null in tests and silently skipped).
        RecordingHandler handler = new();
        PageResidencyTracker tracker = new(maxCapacity: OneSetCapacity);
        StubArenaManager manager = new(tracker, handler);
        int pageSize = Environment.SystemPageSize;
        byte[] data = new byte[pageSize * (Ways + 1)];
        fixed (byte* dataPtr = data)
        {
            using ArenaReservation r5 = MakeReservation(manager, arenaId: 5, offset: 0, size: data.Length, tag: "r5");
            using ArenaReservation r6 = MakeReservation(manager, arenaId: 6, offset: 0, size: data.Length, tag: "r6");
            ArenaByteReader reader5 = new(dataPtr, data.Length, r5);
            ArenaByteReader reader6 = new(dataPtr, data.Length, r6);

            Span<byte> b = stackalloc byte[1];
            for (int p = 0; p < Ways; p++)
                reader5.TryRead((long)p * pageSize, b).Should().BeTrue();   // primes (5, 0..7)
            handler.Evictions.Should().BeEmpty();

            reader6.TryRead(0, b).Should().BeTrue();                        // forces clock eviction of (5, 0)
            handler.Evictions.Should().ContainSingle().Which.Should().Be((5, 0));
        }
    }

    [Test]
    public unsafe void ArenaByteReader_RepeatedSamePageReads_OnlyTouchOnce()
    {
        // ArenaByteReader has a per-instance memo keyed on the last touched OS page; repeated
        // reads inside the same page must skip the per-page Touch loop. We verify by clearing
        // the tracker after the first read and asserting that subsequent same-page reads do
        // not repopulate it. Crossing the page boundary must invalidate the memo and re-Touch.
        PageResidencyTracker tracker = new(maxCapacity: 1024);
        int pageSize = Environment.SystemPageSize;
        byte[] data = new byte[pageSize * 2];
        fixed (byte* dataPtr = data)
        {
            using ArenaReservation reservation = MakeReservation(
                new StubArenaManager(tracker, NoopHandler.Instance), arenaId: 0, offset: 0, size: data.Length);
            ArenaByteReader reader = new(dataPtr, data.Length, reservation);

            Span<byte> b = stackalloc byte[1];

            reader.TryRead(0, b).Should().BeTrue();
            tracker.Count.Should().Be(1);
            tracker.ContainsPage(0, 0).Should().BeTrue();

            tracker.Clear();
            for (int i = 1; i < 100; i++)
                reader.TryRead(i, b).Should().BeTrue();
            tracker.Count.Should().Be(0, "memo must skip Touch for repeated reads on the same page");

            // Crossing into page 1 must invalidate the memo.
            reader.TryRead(pageSize, b).Should().BeTrue();
            tracker.Count.Should().Be(1);
            tracker.ContainsPage(0, 1).Should().BeTrue();

            tracker.Clear();
            reader.TryRead(pageSize + 4, b).Should().BeTrue();
            tracker.Count.Should().Be(0, "memo holds across reads still on page 1");
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
