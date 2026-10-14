// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;

namespace Nethermind.Facade.Filters;

/// <summary>
/// The JSON of one block's matching logs, written as comma-separated <see cref="FilterLog"/> objects without the
/// enclosing array, in a pooled buffer that <see cref="Dispose"/> returns.
/// </summary>
public sealed class LogsJsonChunk : IBufferWriter<byte>, IDisposable
{
    private const int InitialSize = 4096;

    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(InitialSize);
    private int _written;

    /// <summary>The number of logs written.</summary>
    public int Count { get; private set; }

    public ReadOnlySpan<byte> Json => _buffer.AsSpan(0, _written);

    /// <summary>Starts the next log, writing the separator after the previous one.</summary>
    public void StartLog()
    {
        if (Count++ == 0) return;

        GetSpan(1)[0] = (byte)',';
        Advance(1);
    }

    public void Advance(int count) => _written += count;

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written);
    }

    private void EnsureCapacity(int sizeHint)
    {
        int needed = _written + Math.Max(sizeHint, 1);
        if (needed <= _buffer.Length) return;

        byte[] larger = ArrayPool<byte>.Shared.Rent(Math.Max(needed, _buffer.Length * 2));
        _buffer.AsSpan(0, _written).CopyTo(larger);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = larger;
    }

    public void Dispose()
    {
        byte[] buffer = _buffer;
        _buffer = [];
        _written = 0;
        if (buffer.Length != 0) ArrayPool<byte>.Shared.Return(buffer);
    }
}
