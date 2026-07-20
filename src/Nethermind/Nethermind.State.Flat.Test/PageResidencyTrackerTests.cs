// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.State.Flat.Io;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Test-only eviction-notification hook. Production <see cref="PageResidencyTracker"/> does not
/// surface eviction callbacks; the test stubs below drive this to assert eviction outcomes.
/// </summary>
internal interface IPageEvictionHandler
{
    void OnPageEvicted(int arenaId, int pageIdx);
}

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
        public void QueueEviction(int arenaId, uint pageIdx) => handler.OnPageEvicted(arenaId, (int)pageIdx);
        public ArenaWriter CreateWriter(long estimatedSize, bool small = false) => throw new NotSupportedException();
        public IReadOnlyList<CatalogEntry> Initialize(IReadOnlyList<CatalogEntry> entries) => throw new NotSupportedException();
        public ArenaReservation Open(in SnapshotLocation location) => throw new NotSupportedException();
        public void OnWriteCompleted(ArenaFile file, long newFrontier, bool hasHeadroom) => throw new NotSupportedException();
        public void OnWriteCancelledShared(ArenaFile file) => throw new NotSupportedException();
        public void OnWriteCancelledDedicated(ArenaFile file) => throw new NotSupportedException();
        // No-op so reservation disposal doesn't blow up in tests.
        public bool MarkDead(ArenaFile file, long deadSize) => false;
        public void ForgetTrackerRange(int arenaId, long byteOffset, long byteSize) { }
        public bool TryPunchHole(ArenaFile file, long offset, long size) => false;

        public ArenaFile GetOrCreateFile(int arenaId)
        {
            if (_files.TryGetValue(arenaId, out ArenaFile? existing)) return existing;
            string path = Path.Combine(tempDir, $"stub_{arenaId:D4}.bin");
            // Size to comfortably cover the widest test reservation (~16 pages).
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
    /// key into <paramref name="handler"/>, mirroring what <see cref="ArenaReservation.TouchRangePopulate"/>
    /// does in production via <see cref="IArenaManager.QueueEviction"/>.
    /// </summary>
    private static void Touch(PageResidencyTracker tracker, int arenaId, int pageIdx, IPageEvictionHandler? handler = null)
    {
        if (tracker.TryTouch(arenaId, (uint)pageIdx, out int evictedArenaId, out uint evictedPageIdx) == PageResidencyTracker.TouchOutcome.Evicted)
            handler?.OnPageEvicted(evictedArenaId, (int)evictedPageIdx);
    }

    [Test]
    public void Touch_RepeatedSamePage_NeverEvicts()
    {
        RecordingHandler handler = new();
        PageResidencyTracker tracker = new(maxCapacity: OneSetCapacity);

        for (int i = 0; i < 1000; i++)
            Touch(tracker, 7, 42, handler);

        Assert.That(handler.Evictions, Is.Empty);
        Assert.That(tracker.Count, Is.EqualTo(1));
        Assert.That(tracker.ContainsPage(7, 42), Is.True);
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
        Assert.That(handler.Evictions, Is.Empty);
        Assert.That(tracker.Count, Is.EqualTo(Ways));

        Touch(tracker, 0, Ways, handler);
        Assert.That(handler.Evictions, Is.EqualTo(new[] { (0, 0) }));
        Assert.That(tracker.ContainsPage(0, 0), Is.False);
        Assert.That(tracker.ContainsPage(0, Ways), Is.True);
        Assert.That(tracker.Count, Is.EqualTo(Ways));
    }

    [Test]
    public void TryTouch_ReturnsOutcomeAndDisplacedKey()
    {
        PageResidencyTracker tracker = new(OneSetCapacity);

        Assert.That(tracker.TryTouch(0, 0, out _, out _), Is.EqualTo(PageResidencyTracker.TouchOutcome.Inserted));
        Assert.That(tracker.TryTouch(0, 0, out _, out _), Is.EqualTo(PageResidencyTracker.TouchOutcome.Hit));

        for (int i = 1; i < Ways; i++)
            Assert.That(tracker.TryTouch(0, (uint)i, out _, out _), Is.EqualTo(PageResidencyTracker.TouchOutcome.Inserted));

        // Set is full and every way has REF=1. The 9th touch's clock pass clears all 8 REF
        // bits, then wraps back to way 0 and evicts (0, 0) — the first inserted key.
        Assert.That(tracker.TryTouch(0, Ways, out int evictedArenaId, out uint evictedPageIdx), Is.EqualTo(PageResidencyTracker.TouchOutcome.Evicted));
        Assert.That(evictedArenaId, Is.EqualTo(0));
        Assert.That(evictedPageIdx, Is.EqualTo(0));
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
        Assert.That(handler.Evictions, Is.EqualTo(new[] { (0, 0) }));

        Touch(tracker, 0, 3, handler);                          // arms way 3's REF bit
        Assert.That(handler.Evictions, Has.Count.EqualTo(1), "re-touching is a Hit, not an eviction");

        for (int i = 0; i < 3; i++)                             // three more streaming keys
            Touch(tracker, 0, Ways + 1 + i, handler);

        Assert.That(handler.Evictions, Is.EqualTo(new[] { (0, 0), (0, 1), (0, 2), (0, 4) }));
        Assert.That(tracker.ContainsPage(0, 3), Is.True, "re-touched key got a second chance");
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
        Assert.That(handler.Evictions, Has.Count.EqualTo(1));
        Assert.That(tracker.Count, Is.EqualTo(Ways));
    }

    [Test]
    public void MaxCapacityZero_TouchIsNoOp()
    {
        RecordingHandler handler = new();
        PageResidencyTracker tracker = new(maxCapacity: 0);
        Touch(tracker, 1, 1, handler);
        Touch(tracker, 2, 2, handler);
        Assert.That(handler.Evictions, Is.Empty);
        Assert.That(tracker.Count, Is.EqualTo(0));
        Assert.That(tracker.ContainsPage(1, 1), Is.False);
    }

    [TestCase(1, Ways)]
    [TestCase(Ways, Ways)]
    [TestCase(Ways + 1, 2 * Ways)]
    [TestCase(3 * Ways, 4 * Ways)]
    public void MaxCapacity_RoundsUpToWayMultipleOfPowerOfTwoSets(int requested, int expected)
    {
        PageResidencyTracker tracker = new(maxCapacity: requested);
        Assert.That(tracker.MaxCapacity, Is.EqualTo(expected));
    }

    [Test]
    public void Forget_RemovesPresentEntry_AndIsNoOpForAbsentOrDisabled()
    {
        PageResidencyTracker tracker = new(maxCapacity: OneSetCapacity);

        // Present: insert, then Forget — gone.
        tracker.TryTouch(5, 3, out _, out _);
        Assert.That(tracker.ContainsPage(5, 3), Is.True);
        tracker.Forget(5, 3);
        Assert.That(tracker.ContainsPage(5, 3), Is.False);
        Assert.That(tracker.Count, Is.EqualTo(0));

        // Absent: Forget on a key the tracker never saw — neighbouring entries survive.
        tracker.TryTouch(5, 3, out _, out _);
        tracker.Forget(5, 4);
        Assert.That(tracker.ContainsPage(5, 3), Is.True);

        // After REF bit armed (Hit re-arms it), Forget still clears via CAS retry.
        tracker.TryTouch(5, 3, out _, out _);  // Hit, sets REF=1
        tracker.Forget(5, 3);
        Assert.That(tracker.ContainsPage(5, 3), Is.False);

        // Disabled tracker: no-op, no exception.
        using PageResidencyTracker disabled = new(maxCapacity: 0);
        disabled.Forget(5, 3);
    }

    [Test]
    public void TryPickResidentPage_DisabledOrEmpty_ReturnsFalse()
    {
        // Disabled tracker: immediate false, no allocation needed for the probe.
        using (PageResidencyTracker disabled = new(maxCapacity: 0))
            Assert.That(disabled.TryPickResidentPage(out _, out _), Is.False);

        // Empty tracker: probe budget runs out on VALID=0 slots.
        PageResidencyTracker tracker = new(maxCapacity: OneSetCapacity);
        Assert.That(tracker.TryPickResidentPage(out _, out _), Is.False);

        // Insert + Forget — slot is back to 0, so picks miss again.
        tracker.TryTouch(5, 3, out _, out _);
        tracker.Forget(5, 3);
        Assert.That(tracker.TryPickResidentPage(out _, out _), Is.False);
    }

    [Test]
    public void TryPickResidentPage_ReturnsOnlyInsertedKeys()
    {
        // Fully populate a single set with a known key set, then make many picks. Every result
        // must be one of the inserted keys (hand wraps via Interlocked.Increment + mask).
        PageResidencyTracker tracker = new(maxCapacity: OneSetCapacity);
        HashSet<(int, int)> inserted = [];
        for (int i = 0; i < Ways; i++)
        {
            tracker.TryTouch(7, (uint)i, out _, out _);
            inserted.Add((7, i));
        }

        for (int i = 0; i < 100; i++)
        {
            Assert.That(tracker.TryPickResidentPage(out int aid, out uint pid), Is.True);
            Assert.That(inserted, Does.Contain((aid, (int)pid)));
        }
    }

    [Test]
    public void GcMemoryPressure_AccountsForMetadataAndResidentPages()
    {
        long pageSize = Environment.SystemPageSize;

        using (PageResidencyTracker disabled = new(maxCapacity: 0))
        {
            Assert.That(disabled.MetadataBytes, Is.EqualTo(0));
            Assert.That(disabled.ResidentBytes, Is.EqualTo(0));
            Assert.That(disabled.TryTouch(0, 0, out _, out _), Is.EqualTo(PageResidencyTracker.TouchOutcome.Hit));
            Assert.That(disabled.ResidentBytes, Is.EqualTo(0));
        }

        PageResidencyTracker tracker = new(maxCapacity: OneSetCapacity);
        Assert.That(tracker.MetadataBytes, Is.GreaterThan(0));
        Assert.That(tracker.ResidentBytes, Is.EqualTo(0));

        Assert.That(tracker.TryTouch(0, 0, out _, out _), Is.EqualTo(PageResidencyTracker.TouchOutcome.Inserted));
        Assert.That(tracker.ResidentBytes, Is.EqualTo(pageSize));

        Assert.That(tracker.TryTouch(0, 0, out _, out _), Is.EqualTo(PageResidencyTracker.TouchOutcome.Hit));
        Assert.That(tracker.ResidentBytes, Is.EqualTo(pageSize));

        for (int i = 1; i < Ways; i++)
            Assert.That(tracker.TryTouch(0, (uint)i, out _, out _), Is.EqualTo(PageResidencyTracker.TouchOutcome.Inserted));
        Assert.That(tracker.ResidentBytes, Is.EqualTo((long)Ways * pageSize));

        // Eviction: net zero (one in, one out).
        Assert.That(tracker.TryTouch(0, Ways, out _, out _), Is.EqualTo(PageResidencyTracker.TouchOutcome.Evicted));
        Assert.That(tracker.ResidentBytes, Is.EqualTo((long)Ways * pageSize));

        // Bounds invariant: continued streaming inserts never exceed the capacity ceiling.
        for (int i = Ways + 1; i < 4 * Ways; i++)
            tracker.TryTouch(0, (uint)i, out _, out _);
        Assert.That(tracker.ResidentBytes, Is.LessThanOrEqualTo((long)tracker.MaxCapacity * pageSize));

        int presentKey = -1;
        for (int i = 4 * Ways - 1; i >= 0 && presentKey < 0; i--)
            if (tracker.ContainsPage(0, (uint)i)) presentKey = i;
        Assert.That(presentKey, Is.GreaterThanOrEqualTo(0), "the set should still hold at least one streamed key");
        long beforeForget = tracker.ResidentBytes;
        tracker.Forget(0, (uint)presentKey);
        Assert.That(tracker.ResidentBytes, Is.EqualTo(beforeForget - pageSize));

        // Re-inserting into the freed slot restores occupancy without raising GC pressure —
        // the high-water mark already covers this level, so only the counter changes.
        Assert.That(tracker.TryTouch(0, (uint)presentKey, out _, out _), Is.EqualTo(PageResidencyTracker.TouchOutcome.Inserted));
        Assert.That(tracker.ResidentBytes, Is.EqualTo(beforeForget));

        // Dispose releases the reported pressure (cannot observe GC pressure directly, but
        // the dispose path must not throw and must be idempotent).
        tracker.Dispose();
        tracker.Dispose();
    }

    private static ArenaReservation MakeReservation(StubArenaManager manager, int arenaId, long offset, long size) =>
        new(manager, manager.GetOrCreateFile(arenaId), arenaId, offset, size);

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
            Assert.That(reader.TryRead(0, sink), Is.True);

            int firstPage = (int)(baseOffset / pageSize);
            int lastPage = (int)((baseOffset + 15) / pageSize);
            Assert.That(firstPage, Is.Not.EqualTo(lastPage), "test setup must straddle a page boundary");
            Assert.That(tracker.ContainsPage(9, (uint)firstPage), Is.True);
            Assert.That(tracker.ContainsPage(9, (uint)lastPage), Is.True);
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

            using NoOpPin pin = reader.PinBuffer(new Bound(0, pageSize * 2 + 1));
            Assert.That(pin.Buffer.Length, Is.EqualTo(pageSize * 2 + 1));
            Assert.That(tracker.ContainsPage(1, 0), Is.True);
            Assert.That(tracker.ContainsPage(1, 1), Is.True);
            Assert.That(tracker.ContainsPage(1, 2), Is.True);
        }
    }

    [Test]
    public unsafe void ArenaByteReader_DispatchesCrossArenaEvictionsToHandler()
    {
        // Fill the only set with 8 reads from arena 5, then read from arena 6 to force a clock
        // eviction. The displaced key (5, 0) surfaces through QueueEviction → handler.
        RecordingHandler handler = new();
        PageResidencyTracker tracker = new(maxCapacity: OneSetCapacity);
        using StubArenaManager manager = new(tracker, handler, _tempDir);
        int pageSize = Environment.SystemPageSize;
        byte[] data = new byte[pageSize * (Ways + 1)];
        fixed (byte* dataPtr = data)
        {
            using ArenaReservation r5 = MakeReservation(manager, arenaId: 5, offset: 0, size: data.Length);
            using ArenaReservation r6 = MakeReservation(manager, arenaId: 6, offset: 0, size: data.Length);
            ArenaByteReader reader5 = new(dataPtr, data.Length, r5);
            ArenaByteReader reader6 = new(dataPtr, data.Length, r6);

            Span<byte> b = stackalloc byte[1];
            for (int p = 0; p < Ways; p++)
                Assert.That(reader5.TryRead((long)p * pageSize, b), Is.True);   // primes (5, 0..7)
            Assert.That(handler.Evictions, Is.Empty);

            Assert.That(reader6.TryRead(0, b), Is.True);                        // forces clock eviction of (5, 0)
            Assert.That(handler.Evictions, Is.EqualTo(new[] { (5, 0) }));
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

            Assert.That(reader.TryRead(0, b), Is.True);
            Assert.That(tracker.Count, Is.EqualTo(1));
            Assert.That(tracker.ContainsPage(0, 0), Is.True);

            tracker.Forget(0, 0);
            for (int i = 1; i < 100; i++)
                Assert.That(reader.TryRead(i, b), Is.True);
            Assert.That(tracker.Count, Is.EqualTo(0), "memo must skip Touch for repeated reads on the same page");

            // Crossing into page 1 must invalidate the memo.
            Assert.That(reader.TryRead(pageSize, b), Is.True);
            Assert.That(tracker.Count, Is.EqualTo(1));
            Assert.That(tracker.ContainsPage(0, 1), Is.True);

            tracker.Forget(0, 1);
            Assert.That(reader.TryRead(pageSize + 4, b), Is.True);
            Assert.That(tracker.Count, Is.EqualTo(0), "memo holds across reads still on page 1");
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
            Assert.That(reader.TryRead(4, sink), Is.True);
            using NoOpPin pin = reader.PinBuffer(new Bound(0, 16));
            Assert.That(pin.Buffer.Length, Is.EqualTo(16));
        }
    }
}
