// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core.Buffers;

namespace Nethermind.Pbt;

/// <summary>Writes directly to a fixed or rented-growable buffer.</summary>
/// <remarks><see cref="Detach"/> transfers a rented buffer; <see cref="Dispose"/> releases an undetached one.</remarks>
public ref struct BufferWriter
{
    private readonly IRefCountingMemoryProvider? _provider;
    private readonly int _capacityHint;
    private RefCountingMemory? _memory;
    private Span<byte> _buffer;
    private int _written;

    /// <summary>Writes into <paramref name="destination"/> and no further; <see cref="Detach"/> is unavailable.</summary>
    public BufferWriter(Span<byte> destination) => _buffer = destination;

    /// <summary>Rents from <paramref name="provider"/> on the first write and grows as needed.</summary>
    /// <param name="capacityHint">Expected output length; zero sizes the first rent from the write.</param>
    public BufferWriter(IRefCountingMemoryProvider provider, int capacityHint = 0)
    {
        _provider = provider;
        _capacityHint = capacityHint;
    }

    /// <summary>How many bytes have been committed so far, which doubles as the offset of the next write.</summary>
    public readonly int WrittenCount => _written;

    /// <summary>The bytes committed so far.</summary>
    public readonly ReadOnlySpan<byte> WrittenSpan => _buffer[.._written];

    /// <summary>Gets room for at least <paramref name="sizeHint"/> bytes; the next call may invalidate the span.</summary>
    public Span<byte> GetSpan(int sizeHint)
    {
        Debug.Assert(sizeHint >= 0);
        if (_buffer.Length - _written < sizeHint) Grow(sizeHint);
        return _buffer[_written..];
    }

    /// <summary>Commits <paramref name="count"/> of the bytes <see cref="GetSpan"/> last handed out.</summary>
    public void Advance(int count)
    {
        Debug.Assert((uint)count <= (uint)(_buffer.Length - _written), "the writer advances only over room it handed out");
        _written += count;
    }

    /// <summary>Appends <paramref name="source"/> verbatim.</summary>
    public void Write(ReadOnlySpan<byte> source)
    {
        source.CopyTo(GetSpan(source.Length));
        _written += source.Length;
    }

    /// <summary>Rolls back to <paramref name="count"/> while retaining the buffer.</summary>
    public void Reset(int count)
    {
        Debug.Assert((uint)count <= (uint)_written, "a reset only ever rolls back");
        _written = count;
    }

    /// <summary>Transfers the written rented buffer to the caller, or returns <c>null</c> when empty.</summary>
    /// <exception cref="InvalidOperationException">The writer wrote into a caller's buffer, which it cannot hand over.</exception>
    public RefCountingMemory? Detach()
    {
        if (_written == 0)
        {
            Dispose();
            return null;
        }

        if (_memory is null) throw new InvalidOperationException("The writer has no buffer of its own to hand over");

        RefCountingMemory memory = _memory;
        memory.Shrink(_written);
        _memory = null;
        _buffer = default;
        _written = 0;
        return memory;
    }

    /// <summary>Releases an undetached rented buffer.</summary>
    public void Dispose()
    {
        ((IDisposable?)_memory)?.Dispose();
        _memory = null;
        _buffer = default;
        _written = 0;
    }

    private void Grow(int sizeHint)
    {
        if (_provider is null) throw new InvalidOperationException($"A writer over {_buffer.Length} bytes has no room for {sizeHint} more past {_written}");

        int required = _written + sizeHint;
        int capacity = Math.Max(required, Math.Max(_capacityHint, _buffer.Length * 2));
        RefCountingMemory grown = _provider.Rent(capacity);
        WrittenSpan.CopyTo(grown.GetSpan());
        ((IDisposable?)_memory)?.Dispose();
        _memory = grown;
        _buffer = grown.GetSpan();
    }
}
