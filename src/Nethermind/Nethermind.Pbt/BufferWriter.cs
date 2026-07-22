// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core.Buffers;

namespace Nethermind.Pbt;

/// <summary>
/// A <c>PipeWriter</c>-shaped cursor over a byte buffer: <see cref="GetSpan"/> hands out room and
/// <see cref="Advance"/> commits it, so a producer writes its encoding straight into its final
/// position rather than into a buffer of its own to be copied over afterwards.
/// </summary>
/// <remarks>
/// Backed either by a caller-supplied span, which cannot grow, or by an
/// <see cref="IRefCountingMemoryProvider"/>, which rents on the first write and doubles from there.
/// A growable writer hands its buffer to <see cref="Detach"/>, narrowed to what was written; one
/// whose bytes are dropped instead releases it through <see cref="Dispose"/>.
/// <para>
/// The two backings are what let one encoder serve both a nested producer — a trie node group
/// folding into the blob its parent is assembling — and a caller that has already sized and
/// allocated the destination itself.
/// </para>
/// </remarks>
public ref struct BufferWriter
{
    private readonly IRefCountingMemoryProvider? _provider;
    private readonly int _capacityHint;
    private RefCountingMemory? _memory;
    private Span<byte> _buffer;
    private int _written;

    /// <summary>Writes into <paramref name="destination"/> and no further; <see cref="Detach"/> is unavailable.</summary>
    public BufferWriter(Span<byte> destination) => _buffer = destination;

    /// <summary>
    /// Rents from <paramref name="provider"/> on the first write and grows as needed, starting at
    /// <paramref name="capacityHint"/> bytes.
    /// </summary>
    /// <param name="capacityHint">
    /// What the caller expects to write — the length of the blob being replaced makes a good one. Left
    /// at zero the first write sizes the rent itself, which is exactly right for a producer that writes
    /// once and knows its length, as a bare group's fold does.
    /// </param>
    public BufferWriter(IRefCountingMemoryProvider provider, int capacityHint = 0)
    {
        _provider = provider;
        _capacityHint = capacityHint;
    }

    /// <summary>How many bytes have been committed so far, which doubles as the offset of the next write.</summary>
    public readonly int WrittenCount => _written;

    /// <summary>The bytes committed so far.</summary>
    public readonly ReadOnlySpan<byte> WrittenSpan => _buffer[.._written];

    /// <summary>
    /// Room for at least <paramref name="sizeHint"/> more bytes, which <see cref="Advance"/> commits.
    /// The span is valid only until the next call, a growable writer moving its bytes as it rents.
    /// </summary>
    public Span<byte> GetSpan(int sizeHint)
    {
        Debug.Assert(sizeHint >= 0);
        if (_buffer.Length - _written < sizeHint) Grow(sizeHint);
        return _buffer[_written..];
    }

    /// <summary>
    /// Room for exactly <paramref name="length"/> more bytes, which <see cref="Advance"/> commits as much
    /// of as was used.
    /// </summary>
    /// <remarks>
    /// What <see cref="GetSpan"/> hands out for a producer that knows an upper bound on what it will
    /// write and wants no more than that: writing past the bound overruns the span it was given, rather
    /// than running on into room the writer happened to have spare.
    /// </remarks>
    public Span<byte> Reserve(int length) => GetSpan(length)[..length];

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

    /// <summary>
    /// Rolls the cursor back to <paramref name="count"/>, discarding everything written past it. The
    /// buffer is kept, so a writer that reconsiders what it is building reuses the room it already has.
    /// </summary>
    public void Reset(int count)
    {
        Debug.Assert((uint)count <= (uint)_written, "a reset only ever rolls back");
        _written = count;
    }

    /// <summary>
    /// Hands the buffer over as the value it holds, narrowed to <see cref="WrittenCount"/>, and takes
    /// this writer out of the picture: the caller owns the lease from here and must release it. Nothing
    /// written means no value, so the buffer goes back instead and the result is <c>null</c>.
    /// </summary>
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

    /// <summary>Releases the rented buffer of a writer whose bytes were dropped; a detached or span-backed one holds none.</summary>
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
