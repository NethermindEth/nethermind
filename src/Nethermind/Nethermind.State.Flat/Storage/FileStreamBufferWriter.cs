// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.Storage;

public struct FileStreamBufferWriter(FileStream stream) : IByteBufferWriter, IDisposable
{
    private const int BufferSize = 1024 * 1024; // 1MB

    private readonly FileStream _stream = stream;
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
    private int _buffered;
    private long _flushed;

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        if (sizeHint > _buffer.Length - _buffered)
            Flush();

        return _buffer.AsSpan(_buffered);
    }

    public void Advance(int count) => _buffered += count;

    public int Written => (int)(_flushed + _buffered);

    public void Flush()
    {
        if (_buffered > 0)
        {
            _stream.Write(_buffer, 0, _buffered);
            _flushed += _buffered;
            _buffered = 0;
        }
    }

    public void Dispose()
    {
        Flush();
        byte[] buffer = _buffer;
        _buffer = null!;
        ArrayPool<byte>.Shared.Return(buffer);
    }
}
