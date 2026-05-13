// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// Buffered writer over an arena slice. The writer holds the <see cref="ArenaFile"/> ref
/// directly — Complete and Cancel mutate the file (truncate / drop manager-lease) and then
/// notify <see cref="ArenaManager"/> for the dict / metric bookkeeping. The manager never
/// looks the file up by id in the writer's finish path; everything it needs is in the
/// notification arguments.
/// </summary>
public sealed class ArenaWriter : IDisposable
{
    private ArenaBufferWriter _writer;
    private readonly ArenaManager _manager;
    private readonly ArenaFile _file;
    private readonly bool _dedicated;
    private readonly long _startOffset;
    private readonly string _tag;
    private bool _completed;

    internal ArenaWriter(ArenaManager manager, ArenaFile file, bool dedicated, long startOffset, Stream stream, string tag)
    {
        _manager = manager;
        _file = file;
        _dedicated = dedicated;
        _startOffset = startOffset;
        long firstOffset = (-startOffset) & 4095L;
        _writer = new ArenaBufferWriter(stream, firstOffset,
            (relOffset, size) => manager.OpenPendingView(file.Id, startOffset + relOffset, size));
        _tag = tag;
    }

    internal int ArenaId => _file.Id;
    internal long StartOffset => _startOffset;

    public ref ArenaBufferWriter GetWriter() => ref _writer;

    public (SnapshotLocation Location, ArenaReservation Reservation) Complete()
    {
        _writer.Flush();
        _completed = true;
        long actualSize = _writer.Written;
        long newFrontier = _startOffset + actualSize;
        _file.Frontier = newFrontier;

        long resizeDelta = 0;
        if (_dedicated && newFrontier > 0 && newFrontier < _file.MappedSize)
        {
            // Dedicated arenas are pre-sized to the writer's estimate; trim the file down
            // to the actual frontier so the on-disk length and mmap footprint match what
            // was written. Dedicated files reach this path before any reservation is
            // constructed against them, so it's safe to shrink the mapping in place.
            long oldMapped = _file.MappedSize;
            _file.Truncate(newFrontier);
            resizeDelta = newFrontier - oldMapped;
        }

        SnapshotLocation location = new(_file.Id, _startOffset, actualSize);
        ArenaReservation reservation = new(_manager, _file, _file.Id, _startOffset, actualSize, _tag);
        // Dedicated arenas are one-shot — they never return to the mutable pool. Shared
        // arenas re-enter the pool iff there's still room for the next packing scan.
        bool hasHeadroom = !_dedicated && newFrontier < _file.MappedSize;
        _manager.OnWriteCompleted(_file.Id, hasHeadroom, resizeDelta);
        return (location, reservation);
    }

    public void Dispose()
    {
        _writer.Dispose();
        if (_completed) return;
        if (_dedicated)
        {
            // Drop the manager's count=1 lease on the file — its own CleanUp closes the
            // mmap + handle and deletes the on-disk file. Then notify the manager to clear
            // its dict / state. The manager NEVER touches the file in this path.
            long mappedSize = _file.MappedSize;
            _file.Dispose();
            _manager.OnWriteCancelledDedicated(_file.Id, mappedSize);
        }
        else
        {
            _manager.OnWriteCancelledShared(_file.Id);
        }
    }
}
