// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.State.Flat.Io;

namespace Nethermind.State.Flat.PersistedSnapshots.Storage;

/// <summary>
/// Arena-backed <see cref="IByteBufferWriter"/> with a 1 MiB write-buffer.
/// </summary>
/// <remarks>
/// The buffer is a <see cref="NativeMemoryList{T}"/> held at <c>Count == Capacity</c>,
/// so <see cref="NativeMemoryList{T}.AsSpan"/> exposes the whole backing buffer and the
/// writer slices the free tail with its own <c>_buffered</c> cursor. A hint larger than
/// the current buffer grows it by reconstruction (after a flush).
/// </remarks>
public struct ArenaBufferWriter(Stream stream, long firstOffset)
    : IByteBufferWriter, IDisposable
{
    private const int BufferSize = 1024 * 1024;
    private const int MaxSizeHint = 8 * 1024 * 1024; // 8 MiB

    private readonly Stream _stream = stream;
    private readonly long _firstOffset = firstOffset;
    private NativeMemoryList<byte> _buffer = new(BufferSize, BufferSize);
    private int _buffered;
    private long _flushed;

    public Span<byte> GetSpan(int sizeHint)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sizeHint, MaxSizeHint);

        if (sizeHint > _buffer.Count - _buffered)
        {
            Flush();
            // Honor the hint exactly: after the flush the buffer is empty and its
            // bytes are on the stream, so it can be swapped for a larger one.
            if (sizeHint > _buffer.Count)
            {
                _buffer.Dispose();
                _buffer = new(sizeHint, sizeHint);
            }
        }

        return _buffer.AsSpan()[_buffered..];
    }

    public void Advance(int count) => _buffered += count;

    public readonly long Written => _flushed + _buffered;

    public readonly long FirstOffset => _firstOffset;

    public void Flush()
    {
        if (_buffered > 0)
        {
            _stream.Write(_buffer.AsSpan()[.._buffered]);
            _flushed += _buffered;
            _buffered = 0;
        }
        _stream.Flush();
    }

    public void Dispose()
    {
        Flush();
        _stream.Dispose();
        _buffer.Dispose();
    }
}
