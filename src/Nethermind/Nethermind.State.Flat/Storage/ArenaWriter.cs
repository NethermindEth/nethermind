// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Storage;

public sealed class ArenaWriter : IDisposable
{
    private StreamBufferWriter _writer;
    private readonly IArenaManager _manager;
    private readonly int _arenaId;
    private readonly long _startOffset;
    private bool _completed;

    internal ArenaWriter(IArenaManager manager, int arenaId, long startOffset, Stream stream)
    {
        _manager = manager;
        _arenaId = arenaId;
        _startOffset = startOffset;
        _writer = new StreamBufferWriter(stream);
    }

    public ref StreamBufferWriter GetWriter() => ref _writer;

    public (SnapshotLocation Location, ArenaReservation Reservation) Complete()
    {
        _writer.Flush();
        _completed = true;
        int actualSize = _writer.Written;
        return _manager.CompleteWrite(_arenaId, _startOffset, actualSize);
    }

    public void Dispose()
    {
        _writer.Dispose();
        if (!_completed)
            _manager.CancelWrite(_arenaId, _startOffset);
    }
}
