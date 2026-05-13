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

    internal int ArenaId { get; }
    internal long Offset { get; }
    public long Size { get; internal set; }
    private string Tag { get; }

    public ArenaReservation(IArenaManager arenaManager, ArenaFile arenaFile,
                            int arenaId, long offset, long size, string tag)
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
        _arenaFile.PopulateRead((long)pageIdx * Environment.SystemPageSize, Environment.SystemPageSize);

        if (outcome == TouchOutcome.Evicted)
            _arenaManager.QueueEviction(evictedArenaId, evictedPageIdx);
    }

    /// <summary>
    /// Direct span access used internally by <see cref="WholeReadSession"/> and the reader
    /// path. External consumers go through <see cref="BeginWholeReadSession"/> so that the
    /// span's lifetime is bounded by an explicit Begin/End scope.
    /// </summary>
    internal ReadOnlySpan<byte> GetSpanInternal() => _arenaFile.GetSpan(Offset, Size);

    /// <summary>
    /// Begin a scoped whole-buffer read. The returned session holds a lease on this
    /// reservation; disposing it releases the lease.
    /// </summary>
    public WholeReadSession BeginWholeReadSession() => new(this);

    internal IArenaWholeView OpenWholeView() => _arenaFile.OpenWholeView(Offset, Size);

    /// <summary>
    /// Construct an <see cref="ArenaByteReader"/> over this reservation's bytes. The reader
    /// reports each read/pin to the arena's <see cref="PageResidencyTracker"/> so collision-displaced
    /// OS pages can be advised <c>MADV_DONTNEED</c> on eviction. Pointer-backed so &gt;2 GiB
    /// reservations are addressable.
    /// </summary>
    public unsafe ArenaByteReader CreateReader() =>
        new(_arenaFile.BasePtr + Offset, Size, this);

    public void AdviseDontNeed() => _arenaManager.AdviseDontNeed(this);

    /// <summary>
    /// Forward a shutdown-preserve request to the underlying <see cref="ArenaFile"/>. Called
    /// by <see cref="PersistedSnapshots.PersistedSnapshot.PersistOnShutdown"/> as the snapshot
    /// is being marked for survival across the next session.
    /// </summary>
    public void PersistOnShutdown() => _arenaFile.PersistOnShutdown();

    protected override void CleanUp()
    {
        AdviseDontNeed();
        _arenaManager.MarkDead(new SnapshotLocation(ArenaId, Offset, Size));
        Metrics.ArenaReservationCountByTag.AddOrUpdate(Tag, 0L, static (_, c) => Math.Max(0, c - 1));
        Metrics.ArenaReservationBytesByTag.AddOrUpdate(Tag, static (_, _) => 0L, static (_, b, s) => Math.Max(0, b - s), _initialSize);
        // Release the lease taken at construction. If this was the last lease (manager has
        // already dropped its dict ref via MarkDead's "all dead" branch), the file's CleanUp
        // runs and the on-disk file is deleted.
        _arenaFile.Dispose();
    }
}
