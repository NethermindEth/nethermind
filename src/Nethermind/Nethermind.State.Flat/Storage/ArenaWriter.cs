// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Storage;

public sealed class ArenaWriter : IDisposable
{
    private ArenaBufferWriter _writer;
    private readonly IArenaManager _manager;
    private readonly int _arenaId;
    private readonly long _startOffset;
    private readonly string _tag;
    private bool _completed;

    internal ArenaWriter(IArenaManager manager, int arenaId, long startOffset, Stream stream, string tag)
    {
        _manager = manager;
        _arenaId = arenaId;
        _startOffset = startOffset;
        long firstOffset = (-startOffset) & 4095L;
        _writer = new ArenaBufferWriter(stream, firstOffset,
            (relOffset, size) => manager.OpenPendingView(arenaId, startOffset + relOffset, size));
        _tag = tag;
    }

    internal int ArenaId => _arenaId;
    internal long StartOffset => _startOffset;

    public ref ArenaBufferWriter GetWriter() => ref _writer;

    public (SnapshotLocation Location, ArenaReservation Reservation) Complete()
    {
        _writer.Flush();
        _completed = true;
        long actualSize = _writer.Written;
        return _manager.CompleteWrite(_arenaId, _startOffset, actualSize, _tag);
    }

    /// <summary>
    /// Complete the write without constructing a slice <see cref="ArenaReservation"/>. Used by
    /// the blob-arena path where a single whole-file reservation (offset 0, current frontier)
    /// is shared by all writers and snapshots referencing the file — a per-write slice
    /// reservation here would later <c>MarkDead</c> the slice and corrupt the underlying
    /// manager's dead-byte accounting before the file is actually unreferenced.
    /// </summary>
    internal SnapshotLocation CompleteSliceless()
    {
        _writer.Flush();
        _completed = true;
        long actualSize = _writer.Written;
        return _manager.CompleteWriteSliceless(_arenaId, _startOffset, actualSize, _tag);
    }

    public void Dispose()
    {
        _writer.Dispose();
        if (!_completed)
            _manager.CancelWrite(_arenaId, _startOffset);
    }
}
