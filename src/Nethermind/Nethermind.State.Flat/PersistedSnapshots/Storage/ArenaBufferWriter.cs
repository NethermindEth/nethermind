// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.PersistedSnapshots.Storage;

/// <summary>
/// Arena-backed <see cref="IByteBufferWriter"/> with a 1 MiB write-buffer.
///
/// Writes are buffered into a pooled byte array and flushed to the underlying
/// <see cref="Stream"/> in 1 MiB chunks.
/// </summary>
public struct ArenaBufferWriter(Stream stream, long firstOffset)
    : IByteBufferWriter, IDisposable
{
    private const int BufferSize = 1024 * 1024; // 1 MiB
    private const int MaxSizeHint = 8 * 1024 * 1024; // 8 MiB — largest single span a caller may request

    private readonly Stream _stream = stream;
    private readonly long _firstOffset = firstOffset;
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
    private int _buffered;
    private long _flushed;

    public Span<byte> GetSpan(int sizeHint)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sizeHint, MaxSizeHint);

        if (sizeHint > _buffer.Length - _buffered)
        {
            Flush();
            // Honor the hint exactly: after the flush the buffer is empty and its
            // bytes are on the stream, so it can be swapped for a larger rented one.
            if (sizeHint > _buffer.Length)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = ArrayPool<byte>.Shared.Rent(sizeHint);
            }
        }

        return _buffer.AsSpan(_buffered);
    }

    public void Advance(int count) => _buffered += count;

    public readonly long Written => _flushed + _buffered;

    public readonly long FirstOffset => _firstOffset;

    public void Flush()
    {
        if (_buffered > 0)
        {
            _stream.Write(_buffer, 0, _buffered);
            _flushed += _buffered;
            _buffered = 0;
        }
        _stream.Flush();
    }

    public void Dispose()
    {
        Flush();
        _stream.Dispose();
        byte[] buffer = _buffer;
        _buffer = null!;
        if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
    }
}
