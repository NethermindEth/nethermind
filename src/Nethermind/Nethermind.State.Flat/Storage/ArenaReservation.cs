// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Utils;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// A reservation of space within an arena. Delegates span access to the owning <see cref="IArenaManager"/>.
/// </summary>
public sealed class ArenaReservation : RefCountingDisposable
{
    private readonly IArenaManager _arenaManager;
    // The owning file. Held directly so read-path operations skip the manager's id →
    // ArenaFile dictionary lookup.
    private readonly ArenaFile _arenaFile;
    private readonly long _initialSize;
    private readonly PersistedSnapshotTier _tier;

    internal int ArenaId { get; }
    internal long Offset { get; }
    public long Size { get; internal set; }

    public ArenaReservation(IArenaManager arenaManager, ArenaFile arenaFile,
                            int arenaId, long offset, long size)
        : base(1)
    {
        // Pin the arena file so it can't be torn down while this reservation is alive.
        // TryAcquireLease handles the race where the manager removed the file from its
        // dict between the caller's lookup and this ctor — surface as InvalidOperationException
        // so the caller's lease path can react instead of operating on a doomed file.
        if (!arenaFile.TryAcquireLease())
            throw new InvalidOperationException(
                $"Cannot construct ArenaReservation for arena {arenaId}: the underlying ArenaFile is already being disposed.");
        _arenaManager = arenaManager;
        _arenaFile = arenaFile;
        _tier = arenaManager.Tier;
        ArenaId = arenaId;
        Offset = offset;
        Size = size;
        _initialSize = size;
        Metrics.ArenaReservationCountByTier.AddOrUpdate(_tier, 1L, static (_, c) => c + 1);
        Metrics.ArenaReservationBytesByTier.AddOrUpdate(_tier,
            static (_, s) => s, static (_, b, s) => b + s, size);
    }

    /// <summary>
    /// Record a single OS-page access by a reader of this reservation. Records the page in the
    /// per-manager <see cref="PageResidencyTracker"/>. On a non-<see cref="TouchOutcome.Hit"/>
    /// outcome the page just entered the working set, so we pre-fault it via
    /// <c>madvise(MADV_POPULATE_READ)</c> on the local <see cref="ArenaFile"/> — the next read
    /// finds the page resident instead of taking a minor fault inline. On a displacement, the
    /// evicted key is handed to <see cref="IArenaManager.QueueEviction"/>, which enqueues it
    /// onto an MPSC ring drained by a background worker — the actual <c>madvise(MADV_DONTNEED)</c>
    /// syscall happens off the producer thread.
    /// </summary>
    internal void TouchPage(int pageIdx)
    {
        TouchOutcome outcome = _arenaManager.PageTracker.TryTouch(ArenaId, pageIdx,
            out int evictedArenaId, out int evictedPageIdx);
        if (outcome == TouchOutcome.Hit) return;

        _arenaFile.PopulateRead((long)pageIdx * Environment.SystemPageSize, Environment.SystemPageSize);

        if (outcome == TouchOutcome.Evicted)
            _arenaManager.QueueEviction(evictedArenaId, evictedPageIdx);
    }

    /// <summary>
    /// Range version of <see cref="TouchPage"/>: probe every OS page that overlaps the
    /// reader-relative byte range <c>[localOffset, localOffset + length)</c> against the
    /// <see cref="PageResidencyTracker"/>, queue any displaced occupants, and — if more
    /// than one probed page was a non-<see cref="TouchOutcome.Hit"/> — issue a <em>single</em>
    /// <c>madvise(MADV_POPULATE_READ)</c> over the page-aligned envelope of the range.
    /// </summary>
    /// <remarks>
    /// Used by callers that know a contiguous span of data is about to be read and want to
    /// coalesce the per-page pre-fault syscalls into one. <c>MADV_POPULATE_READ</c> is a
    /// no-op on already-resident pages, so over-faulting the few hot pages inside the
    /// range is harmless. The per-page tracker probes themselves are unchanged from
    /// <see cref="TouchPage"/> — same arming, same clock eviction, same dispatch into
    /// <see cref="IArenaManager.QueueEviction"/> for displaced pages.
    /// If only a single probed page was non-<see cref="TouchOutcome.Hit"/>, the batched
    /// <c>madvise</c> call is skipped — a one-page syscall is not amortized vs. the
    /// inline minor fault the reader would otherwise take on that page.
    /// </remarks>
    internal void TouchRangePopulate(long localOffset, long length)
    {
        if (length <= 0) return;
        int pageSize = Environment.SystemPageSize;
        long absStart = Offset + localOffset;
        long absEnd = absStart + length;
        long firstPageBase = absStart & ~(long)(pageSize - 1);
        long lastPageBaseExclusive = (absEnd + pageSize - 1) & ~(long)(pageSize - 1);
        int firstPage = (int)(firstPageBase / pageSize);
        int lastPage = (int)((lastPageBaseExclusive - 1) / pageSize);

        int missedCount = 0;
        PageResidencyTracker tracker = _arenaManager.PageTracker;
        for (int p = firstPage; p <= lastPage; p++)
        {
            TouchOutcome outcome = tracker.TryTouch(ArenaId, p,
                out int evictedArenaId, out int evictedPageIdx);
            if (outcome == TouchOutcome.Hit) continue;
            missedCount++;
            if (outcome == TouchOutcome.Evicted)
                _arenaManager.QueueEviction(evictedArenaId, evictedPageIdx);
        }

        // A single cold page is cheaper to bring in via the reader's inline minor fault
        // than via a madvise syscall, so only batch-populate when at least two pages
        // are cold and the syscall overhead is actually amortized.
        if (missedCount > 1)
            _arenaFile.PopulateRead(firstPageBase, lastPageBaseExclusive - firstPageBase);
    }

    /// <summary>
    /// Direct span access used internally by <see cref="WholeReadSession"/> and the reader
    /// path. External consumers go through <see cref="BeginWholeReadSession"/> so that the
    /// span's lifetime is bounded by an explicit Begin/End scope.
    /// </summary>
    internal ReadOnlySpan<byte> GetSpanInternal() => _arenaFile.GetSpan(Offset, Size);

    /// <summary>
    /// Begin a scoped whole-buffer read. The returned session holds a lease on this
    /// reservation; disposing it releases the lease and (by default) issues
    /// <c>madvise(MADV_DONTNEED)</c> on the mapped range. Pass
    /// <paramref name="adviseDontNeedOnDispose"/> = <c>false</c> when the caller has
    /// arranged an explicit eviction elsewhere and a redundant madvise on session close
    /// would be wasteful.
    /// </summary>
    public WholeReadSession BeginWholeReadSession(bool adviseDontNeedOnDispose = true) =>
        new(this, adviseDontNeedOnDispose);

    internal IArenaWholeView OpenWholeView(bool adviseDontNeedOnDispose) =>
        _arenaFile.OpenWholeView(Offset, Size, adviseDontNeedOnDispose);

    /// <summary>
    /// Construct an <see cref="ArenaByteReader"/> over this reservation's bytes. The reader
    /// reports each read/pin to the arena's <see cref="PageResidencyTracker"/> so collision-displaced
    /// OS pages can be advised <c>MADV_DONTNEED</c> on eviction. Pointer-backed so &gt;2 GiB
    /// reservations are addressable.
    /// </summary>
    public unsafe ArenaByteReader CreateReader() =>
        new(_arenaFile.BasePtr + Offset, Size, this);

    public void AdviseDontNeed()
    {
        _arenaFile.AdviseDontNeed(Offset, Size);
        _arenaManager.ForgetTrackerRange(ArenaId, Offset, Size);
    }

    /// <summary>
    /// Forget every PageResidencyTracker entry that points into this reservation. Skips the
    /// <c>madvise(MADV_DONTNEED)</c> step that <see cref="AdviseDontNeed"/> does; use this
    /// when the page-cache side has already been advised away (e.g. by a freshly-closed
    /// <see cref="WholeReadSession"/> over the same range) and only the tracker needs cleaning.
    /// </summary>
    public void ForgetTracker() =>
        _arenaManager.ForgetTrackerRange(ArenaId, Offset, Size);

    /// <summary>
    /// Forward a shutdown-preserve request to the underlying <see cref="ArenaFile"/>. Called
    /// by <see cref="PersistedSnapshots.PersistedSnapshot.PersistOnShutdown"/> as the snapshot
    /// is being marked for survival across the next session.
    /// </summary>
    public void PersistOnShutdown() => _arenaFile.PersistOnShutdown();

    protected override void CleanUp()
    {
        // File-side ops on the ref we already hold — no manager dict lookup. The manager's
        // MarkDead just does the atomic set/dict/metric bookkeeping, then we drop our lease
        // and let the file's own CleanUp delete the on-disk file when its refcount hits zero.
        _arenaFile.AdviseDontNeed(Offset, Size);
        if (_arenaManager.FadviseOnEviction)
            _arenaFile.FadviseDontNeed(Offset, Size);
        _arenaManager.MarkDead(_arenaFile, Size);
        _arenaManager.ForgetTrackerRange(ArenaId, Offset, Size);
        Metrics.ArenaReservationCountByTier.AddOrUpdate(_tier,
            0L, static (_, c) => Math.Max(0, c - 1));
        Metrics.ArenaReservationBytesByTier.AddOrUpdate(_tier,
            static (_, _) => 0L, static (_, b, s) => Math.Max(0, b - s), _initialSize);
        _arenaFile.Dispose();
    }
}
