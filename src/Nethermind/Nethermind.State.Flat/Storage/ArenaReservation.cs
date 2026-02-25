// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Utils;

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

    protected override void CleanUp() =>
        _arenaManager.MarkDead(new SnapshotLocation(ArenaId, Offset, Size));
}
