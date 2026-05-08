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
    // Captured at construction so per-page touches and same-arena evictions skip the
    // manager's id → ArenaFile lookup. Null for in-memory test arenas with no per-page mapping.
    private readonly ArenaFile? _arenaFile;
    private readonly long _initialSize;

    internal int ArenaId { get; }
    internal long Offset { get; }
    public long Size { get; internal set; }
    public string Tag { get; }

    public ArenaReservation(IArenaManager arenaManager, ArenaFile? arenaFile,
                            int arenaId, long offset, long size, string tag)
        : base(1)
    {
        _arenaManager = arenaManager;
        _arenaFile = arenaFile;
        ArenaId = arenaId;
        Offset = offset;
        Size = size;
        Tag = tag;
        _initialSize = size;
        Metrics.ArenaReservationCountByTag.AddOrUpdate(tag, 1L, static (_, c) => c + 1);
        Metrics.ArenaReservationBytesByTag.AddOrUpdate(tag, static (_, s) => s, static (_, b, s) => b + s, size);
    }

    /// <summary>
    /// Record a single OS-page access by a reader of this reservation. Records the page in the
    /// per-manager <see cref="PageResidencyTracker"/>; on a fresh insertion, pre-faults the
    /// local page via <see cref="ArenaFile.PopulateRead"/> directly. On a displacement, hands
    /// the evicted key to <see cref="IArenaManager.QueueEviction"/>, which enqueues it onto an
    /// MPSC ring drained by a background worker — the actual <c>madvise(MADV_DONTNEED)</c>
    /// syscall happens off the producer thread.
    /// </summary>
    internal void TouchPage(int pageIdx)
    {
        TouchOutcome outcome = _arenaManager.PageTracker.TryTouch(ArenaId, pageIdx,
            out int evictedArenaId, out int evictedPageIdx);
        if (outcome == TouchOutcome.Hit) return;

        // Pre-fault the freshly tracked local page so the next read does not block on a fault.
        _arenaFile?.PopulateRead((long)pageIdx * Environment.SystemPageSize, Environment.SystemPageSize);

        if (outcome == TouchOutcome.Evicted)
            _arenaManager.QueueEviction(evictedArenaId, evictedPageIdx);
    }

    /// <summary>
    /// Direct span access used internally by <see cref="WholeReadSession"/> and the reader
    /// path. External consumers go through <see cref="BeginWholeReadSession"/> so that the
    /// span's lifetime is bounded by an explicit Begin/End scope.
    /// </summary>
    internal ReadOnlySpan<byte> GetSpanInternal() => _arenaManager.GetSpan(this);

    /// <summary>
    /// Begin a scoped whole-buffer read. The returned session holds a lease on this
    /// reservation; disposing it releases the lease.
    /// </summary>
    public WholeReadSession BeginWholeReadSession() => new(this);

    internal IArenaWholeView OpenWholeView() => _arenaManager.OpenWholeView(this);

    /// <summary>
    /// Construct an <see cref="ArenaByteReader"/> over this reservation's bytes. The reader
    /// reports each read/pin to the arena's <see cref="PageResidencyTracker"/> so collision-displaced
    /// OS pages can be advised <c>MADV_DONTNEED</c> on eviction. Pointer-backed so &gt;2 GiB
    /// reservations are addressable.
    /// </summary>
    public unsafe ArenaByteReader CreateReader()
    {
        _arenaManager.GetReservationPointer(this, out byte* dataPtr, out long size);
        return new ArenaByteReader(dataPtr, size, this);
    }

    public void AdviseDontNeed() => _arenaManager.AdviseDontNeed(this);

    public void Touch(long subOffset, long size) => _arenaManager.Touch(this, subOffset, size);

    protected override void CleanUp()
    {
        AdviseDontNeed();
        _arenaManager.MarkDead(new SnapshotLocation(ArenaId, Offset, Size));
        Metrics.ArenaReservationCountByTag.AddOrUpdate(Tag, 0L, static (_, c) => Math.Max(0, c - 1));
        Metrics.ArenaReservationBytesByTag.AddOrUpdate(Tag, static (_, _) => 0L, static (_, b, s) => Math.Max(0, b - s), _initialSize);
    }
}
