// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;

namespace Nethermind.Mcp.Plugin.Tools;

/// <summary>
/// An <see cref="IBufferWriter{T}"/> over pooled arrays that refuses to hold more than a fixed number of bytes.
/// </summary>
/// <remarks>
/// <see cref="System.Text.Json.Utf8JsonWriter"/> commits its pending bytes through <see cref="Advance"/> every time it
/// needs more room, so an oversized payload is rejected with <see cref="ResultTooLargeException"/> after at most about
/// twice the limit has been buffered, instead of being materialized in full first.
/// </remarks>
internal sealed class LimitedPooledBufferWriter(int maxSize) : IBufferWriter<byte>, IDisposable
{
    private const int InitialSize = 1024;

    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(Math.Min(InitialSize, Math.Max(maxSize, 1)));
    private int _written;

    /// <summary>Gets the bytes written so far.</summary>
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

    /// <inheritdoc/>
    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > _buffer.Length - _written)
        {
            throw new InvalidOperationException("Cannot advance past the end of the buffer.");
        }

        if (count > maxSize - _written)
        {
            throw new ResultTooLargeException();
        }

        _written += count;
    }

    /// <inheritdoc/>
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written);
    }

    /// <inheritdoc/>
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written);
    }

    public void Dispose()
    {
        byte[] buffer = _buffer;
        _buffer = [];
        _written = 0;
        if (buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void EnsureCapacity(int sizeHint)
    {
        ObjectDisposedException.ThrowIf(_buffer.Length == 0, this);

        int required = Math.Max(sizeHint, 1);
        if (_buffer.Length - _written >= required)
        {
            return;
        }

        // Nothing more can be committed once the limit is reached, so there is no point in growing further.
        if (_written >= maxSize)
        {
            throw new ResultTooLargeException();
        }

        long newSize = Math.Max((long)_buffer.Length * 2, (long)_written + required);
        if (newSize > Array.MaxLength)
        {
            throw new ResultTooLargeException();
        }

        byte[] newBuffer = ArrayPool<byte>.Shared.Rent((int)newSize);
        _buffer.AsSpan(0, _written).CopyTo(newBuffer);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = newBuffer;
    }
}

/// <summary>Thrown by <see cref="LimitedPooledBufferWriter"/> when a payload exceeds its size limit.</summary>
internal sealed class ResultTooLargeException() : Exception("The serialized result exceeds the configured size limit.");
