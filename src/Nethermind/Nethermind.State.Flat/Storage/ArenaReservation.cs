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
    private readonly long _initialSize;

    internal int ArenaId { get; }
    internal long Offset { get; }
    public int Size { get; internal set; }
    public string Tag { get; }

    public ArenaReservation(IArenaManager arenaManager, int arenaId, long offset, int size, string tag)
        : base(1)
    {
        _arenaManager = arenaManager;
        ArenaId = arenaId;
        Offset = offset;
        Size = size;
        Tag = tag;
        _initialSize = size;
        Metrics.ArenaReservationCountByTag.AddOrUpdate(tag, 1L, static (_, c) => c + 1);
        Metrics.ArenaReservationBytesByTag.AddOrUpdate(tag, static (_, s) => s, static (_, b, s) => b + s, (long)size);
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
    /// reports each read/pin to the arena's <see cref="PageSlotCache"/> so collision-displaced
    /// OS pages can be advised <c>MADV_DONTNEED</c> on eviction.
    /// </summary>
    public ArenaByteReader CreateReader() => new(GetSpanInternal(), _arenaManager.PageCache, ArenaId, Offset);

    public void AdviseDontNeed() => _arenaManager.AdviseDontNeed(this);

    public void Touch(int subOffset, int size) => _arenaManager.Touch(this, subOffset, size);

    protected override void CleanUp()
    {
        AdviseDontNeed();
        _arenaManager.MarkDead(new SnapshotLocation(ArenaId, Offset, Size));
        Metrics.ArenaReservationCountByTag.AddOrUpdate(Tag, 0L, static (_, c) => Math.Max(0, c - 1));
        Metrics.ArenaReservationBytesByTag.AddOrUpdate(Tag, static (_, _) => 0L, static (_, b, s) => Math.Max(0, b - s), _initialSize);
    }
}
