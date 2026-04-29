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

    public ReadOnlySpan<byte> GetSpan() => _arenaManager.GetSpan(this);

    /// <summary>
    /// Construct a span-backed <see cref="SpanByteReader"/> over this reservation's bytes.
    /// Reader-shaped APIs consume this rather than poking at <see cref="GetSpan"/> directly,
    /// keeping the read path on the reader abstraction end-to-end.
    /// </summary>
    public SpanByteReader CreateReader() => new(GetSpan());

    public void AdviseDontNeed() => _arenaManager.AdviseDontNeed(this);

    public void Touch(int subOffset, int size) => _arenaManager.Touch(this, subOffset, size);

    protected override void CleanUp()
    {
        AdviseDontNeed();
        _arenaManager.MarkDead(new SnapshotLocation(ArenaId, Offset, Size));
    }
}
