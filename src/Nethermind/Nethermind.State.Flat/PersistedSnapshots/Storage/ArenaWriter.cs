// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.PersistedSnapshots.Storage;

/// <summary>
/// Buffered writer over an arena slice. Complete and Cancel mutate the <see cref="ArenaFile"/>
/// (truncate / drop manager-lease) and then notify <see cref="ArenaManager"/> for dict / metric
/// bookkeeping.
/// </summary>
public sealed class ArenaWriter : IDisposable
{
    private ArenaBufferWriter _writer;
    private readonly IArenaManager _manager;
    private readonly ArenaFile _file;
    private readonly bool _dedicated;
    private readonly long _startOffset;
    private bool _completed;

    internal ArenaWriter(IArenaManager manager, ArenaFile file, bool dedicated, long startOffset, Stream stream)
    {
        _manager = manager;
        _file = file;
        _dedicated = dedicated;
        _startOffset = startOffset;
        long firstOffset = (-startOffset) & PageLayout.PageMask;
        _writer = new ArenaBufferWriter(stream, firstOffset);
    }

    public ref ArenaBufferWriter GetWriter() => ref _writer;

    public (SnapshotLocation Location, ArenaReservation Reservation) Complete()
    {
        _writer.Flush();
        long actualSize = _writer.Written;
        long dataEnd = _startOffset + actualSize;
        // Fail loud if the write ran past the arena's mapped extent: the Math.Min cap below would
        // otherwise silently clamp the frontier and corrupt the per-arena accounting (bytes beyond
        // MappedSize are not covered by the mmap). Indicates the up-front size estimate was too low.
        if (dataEnd > _file.MappedSize)
            throw new InvalidOperationException(
                $"Arena write overflowed its mapped size: wrote up to {dataEnd} but arena {_file.Id} is mapped to {_file.MappedSize}.");
        // Shared arenas pack many reservations per file. Pad the frontier up to an OS-page
        // boundary so the next reservation starts page-aligned and reclamation syscalls
        // (fadvise / fallocate punch-hole) over a reservation cover whole pages exactly.
        long newFrontier = _dedicated
            ? dataEnd
            : Math.Min(PageLayout.RoundUpToOsPage(dataEnd), _file.MappedSize);
        // Frontier is published under the manager lock by OnWriteCompleted below (not written here), so it
        // is serialized against MarkDead's DeadBytes-vs-Frontier read on a shared arena.

        if (_dedicated && newFrontier > 0 && newFrontier < _file.MappedSize)
        {
            // Dedicated arenas are pre-sized to the writer's estimate; trim the file down
            // to the actual frontier so the on-disk length and mmap footprint match what
            // was written. Dedicated files reach this path before any reservation is
            // constructed against them, so it's safe to shrink the mapping in place.
            _file.Truncate(newFrontier);
        }

        SnapshotLocation location = new(_file.Id, _startOffset, actualSize);
        ArenaReservation reservation = new(_manager, _file, _file.Id, _startOffset, actualSize);
        // Mark completed only once the write is committed (the reservation now owns the file slice).
        // Anything that throws above — the overflow guard, Truncate, or the reservation ctor — leaves
        // _completed false so Dispose() runs the cancel/cleanup path and releases the file + pool slot.
        _completed = true;
        // Dedicated arenas are one-shot — they never return to the mutable pool. Shared
        // arenas re-enter the pool iff there's still room for the next packing scan.
        bool hasHeadroom = !_dedicated && newFrontier < _file.MappedSize;
        _manager.OnWriteCompleted(_file, newFrontier, hasHeadroom);
        return (location, reservation);
    }

    public void Dispose()
    {
        _writer.Dispose();
        if (_completed) return;
        if (_dedicated)
        {
            // Drop the manager's count=1 lease — the file's CleanUp closes mmap + handle and
            // deletes it on disk. Then notify the manager to clear its dict / metric state; the
            // file ref stays readable post-dispose (Id / ReportedFrontier are plain fields).
            _file.Dispose();
            _manager.OnWriteCancelledDedicated(_file);
        }
        else
        {
            _manager.OnWriteCancelledShared(_file);
        }
    }
}
