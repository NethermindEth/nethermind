// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
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

    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nm-tracker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

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
    /// <see cref="IArenaManager.QueueEviction"/> into <paramref name="handler"/> so test
    /// assertions on cross-arena evictions still work. Lazily backs each arenaId with a
    /// small file-backed <see cref="ArenaFile"/> in <paramref name="tempDir"/> so the
    /// non-nullable contract on <see cref="ArenaReservation"/> is satisfied.
    /// </summary>
    private sealed class StubArenaManager(PageResidencyTracker tracker, IPageEvictionHandler handler, string tempDir) : IArenaManager, IDisposable
    {
        private readonly Dictionary<int, ArenaFile> _files = [];

        public PageResidencyTracker PageTracker => tracker;
        public void QueueEviction(int arenaId, int pageIdx) => handler.OnPageEvicted(arenaId, pageIdx);
        public ArenaWriter CreateWriter(long estimatedSize, string tag) => throw new NotSupportedException();
        public void Initialize(IReadOnlyList<SnapshotCatalog.CatalogEntry> entries) => throw new NotSupportedException();
        public ArenaReservation Open(in SnapshotLocation location, string tag) => throw new NotSupportedException();
        public IArenaWholeView OpenPendingView(int arenaId, long absoluteOffset, long size) => throw new NotSupportedException();
        // No-op so reservation disposal doesn't blow up in tests.
        public void MarkDead(in SnapshotLocation location) { }
        public void AdviseDontNeed(ArenaReservation reservation) { }

        public ArenaFile GetOrCreateFile(int arenaId)
        {
            if (_files.TryGetValue(arenaId, out ArenaFile? existing)) return existing;
            string path = Path.Combine(tempDir, $"stub_{arenaId:D4}.bin");
            // Size to comfortably cover the widest test reservation (~16 pages); reads past
            // file length via RandomAccess.Read just return 0 bytes, so this is a safety margin.
            ArenaFile file = new(arenaId, path, Environment.SystemPageSize * 16);
            _files[arenaId] = file;
            return file;
        }

        public void Dispose()
        {
            foreach (ArenaFile f in _files.Values) f.Dispose();
            _files.Clear();
        }
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
        // Single-set tracker → all keys land in set 0. Each insert arms REF=1, so the 9th
        // touch's clock pass clears all 8 REF bits before wrapping back to way 0 (the head)
        // and evicting (0, 0) — the first inserted key.
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

        // Set is full and every way has REF=1. The 9th touch's clock pass clears all 8 REF
        // bits, then wraps back to way 0 and evicts (0, 0) — the first inserted key.
        tracker.TryTouch(0, Ways, out int evictedArenaId, out int evictedPageIdx).Should().Be(TouchOutcome.Evicted);
        evictedArenaId.Should().Be(0);
        evictedPageIdx.Should().Be(0);
    }

    [Test]
    public void ReferenceBit_GivesSecondChance()
    {
        // Fill the set, then prime the clock with one streaming insert: that pass clears all
        // 8 REF bits and evicts (0, 0); afterwards way 0 = (0, 8)/REF=1 and ways 1..7 still
        // hold (0, 1..7) but with REF=0; clock hand sits at way 1.
        // Re-touching (0, 3) arms way 3's REF. The next three streaming inserts walk the hand
        // through ways 1, 2 (each REF=0 → evict) and then hit way 3 — REF=1 saves it (clears
        // the bit and moves on), so the third eviction lands on way 4 instead.
        // Net evictions: (0, 0), (0, 1), (0, 2), (0, 4). (0, 3) survived the streaming flood.
        RecordingHandler handler = new();
        PageResidencyTracker tracker = new(OneSetCapacity);

        for (int i = 0; i < Ways; i++)
            Touch(tracker, 0, i, handler);

        Touch(tracker, 0, Ways, handler);                       // primes the clock
        handler.Evictions.Should().Equal((0, 0));

        Touch(tracker, 0, 3, handler);                          // arms way 3's REF bit
        handler.Evictions.Should().HaveCount(1, "re-touching is a Hit, not an eviction");

        for (int i = 0; i < 3; i++)                             // three more streaming keys
            Touch(tracker, 0, Ways + 1 + i, handler);

        handler.Evictions.Should().Equal((0, 0), (0, 1), (0, 2), (0, 4));
        tracker.ContainsPage(0, 3).Should().BeTrue("re-touched key got a second chance");
    }

    [Test]
    public void Miss_OnFullSet_ProducesExactlyOneEviction()
    {
        // A miss on a full set must displace exactly one entry, regardless of how many REF
        // bits the clock had to clear before finding an unreferenced way.
        RecordingHandler handler = new();
        PageResidencyTracker tracker = new(OneSetCapacity);
        for (int i = 0; i < Ways; i++)
            Touch(tracker, 0, i, handler);

        // Re-touch every other entry so the clock has to clear REFs on its way to a victim.
        for (int i = 0; i < Ways; i += 2)
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
    public void Forget_RemovesPresentEntry_AndIsNoOpForAbsentOrDisabled()
    {
        PageResidencyTracker tracker = new(maxCapacity: OneSetCapacity);

        // Present: insert, then Forget — gone.
        tracker.TryTouch(5, 3, out _, out _);
        tracker.ContainsPage(5, 3).Should().BeTrue();
        tracker.Forget(5, 3);
        tracker.ContainsPage(5, 3).Should().BeFalse();
        tracker.Count.Should().Be(0);

        // Absent: Forget on a key the tracker never saw — neighbouring entries survive.
        tracker.TryTouch(5, 3, out _, out _);
        tracker.Forget(5, 4);
        tracker.ContainsPage(5, 3).Should().BeTrue();

        // After REF bit armed (Hit re-arms it), Forget still clears via CAS retry.
        tracker.TryTouch(5, 3, out _, out _);  // Hit, sets REF=1
        tracker.Forget(5, 3);
        tracker.ContainsPage(5, 3).Should().BeFalse();

        // Disabled tracker: no-op, no exception.
        using PageResidencyTracker disabled = new(maxCapacity: 0);
        disabled.Forget(5, 3);
    }

    [Test]
    public void GcMemoryPressure_AccountsForMetadataAndResidentPages()
    {
        long pageSize = Environment.SystemPageSize;

        // Disabled tracker reports no metadata and no residency.
        using (PageResidencyTracker disabled = new(maxCapacity: 0))
        {
            disabled.MetadataBytes.Should().Be(0);
            disabled.ResidentBytes.Should().Be(0);
            disabled.TryTouch(0, 0, out _, out _).Should().Be(TouchOutcome.Hit);
            disabled.ResidentBytes.Should().Be(0);
        }

        PageResidencyTracker tracker = new(maxCapacity: OneSetCapacity);
        tracker.MetadataBytes.Should().BeGreaterThan(0);
        tracker.ResidentBytes.Should().Be(0);

        // Inserted: +1 page.
        tracker.TryTouch(0, 0, out _, out _).Should().Be(TouchOutcome.Inserted);
        tracker.ResidentBytes.Should().Be(pageSize);

        // Hit: unchanged.
        tracker.TryTouch(0, 0, out _, out _).Should().Be(TouchOutcome.Hit);
        tracker.ResidentBytes.Should().Be(pageSize);

        // Fill the rest of the set.
        for (int i = 1; i < Ways; i++)
            tracker.TryTouch(0, i, out _, out _).Should().Be(TouchOutcome.Inserted);
        tracker.ResidentBytes.Should().Be((long)Ways * pageSize);

        // Eviction: net zero (one in, one out).
        tracker.TryTouch(0, Ways, out _, out _).Should().Be(TouchOutcome.Evicted);
        tracker.ResidentBytes.Should().Be((long)Ways * pageSize);

        // Bounds invariant: continued streaming inserts never exceed the capacity ceiling.
        for (int i = Ways + 1; i < 4 * Ways; i++)
            tracker.TryTouch(0, i, out _, out _);
        tracker.ResidentBytes.Should().BeLessOrEqualTo((long)tracker.MaxCapacity * pageSize);

        // Forget intentionally does NOT decrement the counter — residency reflects only
        // bulk-cleared state, not slot-level removals.
        long beforeForget = tracker.ResidentBytes;
        tracker.Forget(0, 4 * Ways - 1);
        tracker.ResidentBytes.Should().Be(beforeForget);

        // Dispose settles the residual back to zero (cannot observe GC pressure directly,
        // but the dispose path must not throw and must be idempotent).
        tracker.Dispose();
        tracker.Dispose();
    }

    private static ArenaReservation MakeReservation(StubArenaManager manager, int arenaId, long offset, long size, string tag = "test") =>
        new(manager, manager.GetOrCreateFile(arenaId), arenaId, offset, size, tag);

    [Test]
    public unsafe void ArenaByteReader_TryRead_TouchesAllSpannedPages()
    {
        PageResidencyTracker tracker = new(maxCapacity: 1024);
        int pageSize = Environment.SystemPageSize;
        long baseOffset = pageSize - 8;
        byte[] data = new byte[pageSize * 2];
        fixed (byte* dataPtr = data)
        {
            using StubArenaManager manager = new(tracker, NoopHandler.Instance, _tempDir);
            using ArenaReservation reservation = MakeReservation(
                manager, arenaId: 9, offset: baseOffset, size: data.Length);
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
            using StubArenaManager manager = new(tracker, NoopHandler.Instance, _tempDir);
            using ArenaReservation reservation = MakeReservation(
                manager, arenaId: 1, offset: 0, size: data.Length);
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
        using StubArenaManager manager = new(tracker, handler, _tempDir);
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
            using StubArenaManager manager = new(tracker, NoopHandler.Instance, _tempDir);
            using ArenaReservation reservation = MakeReservation(
                manager, arenaId: 0, offset: 0, size: data.Length);
            ArenaByteReader reader = new(dataPtr, data.Length, reservation);

            Span<byte> b = stackalloc byte[1];

            reader.TryRead(0, b).Should().BeTrue();
            tracker.Count.Should().Be(1);
            tracker.ContainsPage(0, 0).Should().BeTrue();

            tracker.Forget(0, 0);
            for (int i = 1; i < 100; i++)
                reader.TryRead(i, b).Should().BeTrue();
            tracker.Count.Should().Be(0, "memo must skip Touch for repeated reads on the same page");

            // Crossing into page 1 must invalidate the memo.
            reader.TryRead(pageSize, b).Should().BeTrue();
            tracker.Count.Should().Be(1);
            tracker.ContainsPage(0, 1).Should().BeTrue();

            tracker.Forget(0, 1);
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
            using StubArenaManager manager = new(disabled, NoopHandler.Instance, _tempDir);
            using ArenaReservation reservation = MakeReservation(
                manager, arenaId: 0, offset: 0, size: data.Length);
            ArenaByteReader reader = new(dataPtr, data.Length, reservation);
            Span<byte> sink = stackalloc byte[8];
            reader.TryRead(4, sink).Should().BeTrue();
            using NoOpPin pin = reader.PinBuffer(0, 16);
            pin.Buffer.Length.Should().Be(16);
        }
    }
}
