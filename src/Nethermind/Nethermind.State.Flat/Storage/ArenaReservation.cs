// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Utils;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// A reservation of space within an arena. Delegates span access to the owning <see cref="IArenaManager"/>.
/// </summary>
public sealed class ArenaReservation(IArenaManager arenaManager, int arenaId, long offset, int size)
    : RefCountingDisposable(1)
{
    private readonly IArenaManager _arenaManager = arenaManager;

    internal int ArenaId { get; } = arenaId;
    internal long Offset { get; } = offset;
    public int Size { get; internal set; } = size;

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
    /// reports each read/pin to the arena's <see cref="PageClockCache"/> so least-recently-used
    /// OS pages can be advised <c>MADV_DONTNEED</c> on eviction.
    /// </summary>
    public ArenaByteReader CreateReader() => new(GetSpanInternal(), _arenaManager.PageCache, ArenaId, Offset);

    public void AdviseDontNeed() => _arenaManager.AdviseDontNeed(this);

    public void Touch(int subOffset, int size) => _arenaManager.Touch(this, subOffset, size);

    protected override void CleanUp()
    {
        AdviseDontNeed();
        _arenaManager.MarkDead(new SnapshotLocation(ArenaId, Offset, Size));
    }
}
