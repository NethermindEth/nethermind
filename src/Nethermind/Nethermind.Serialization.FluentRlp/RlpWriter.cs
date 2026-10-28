// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Nethermind.Serialization.FluentRlp;

/// <summary>Writes a value to an <see cref="RlpWriter"/> with an extra context argument.</summary>
public delegate void RefRlpWriterAction<in TContext>(ref RlpWriter arg, TContext value)
    where TContext : allows ref struct;

/// <summary>Writes to an <see cref="RlpWriter"/>.</summary>
public delegate void RefRlpWriterAction(ref RlpWriter arg);

/// <summary>
/// Writes canonical RLP items, to measure their length or to write their bytes.
/// </summary>
/// <remarks>
/// <para>
/// A sequence prefix depends on the length of its payload. The writer that
/// <see cref="Rlp.Write{TContext}(TContext, RefRlpWriterAction{TContext})"/> uses writes in one pass to a growable
/// buffer: it reserves one prefix byte for each sequence and, when the payload is longer than 55 bytes, inserts the
/// long prefix while it copies the buffer to the output.
/// </para>
/// <para>
/// A <see cref="ContentWriter"/> writes to a fixed span instead, so it runs the write action of each sequence twice:
/// once with a <see cref="LengthWriter"/> to measure the payload, then to write it.
/// </para>
/// </remarks>
public ref struct RlpWriter
{
    private const int MinBufferLength = 256;
    private const int StackCheckInterval = 32;
    private const int MaxCachedBufferLength = 1 << 20;

    // Buffers of the last buffered write on this thread. A nested write takes new ones while they are in use.
    [ThreadStatic]
    private static byte[]? CachedBuffer;

    [ThreadStatic]
    private static int[]? CachedSequences;

    private Span<byte> _destination;
    private readonly bool _isContent;
    private int _length;

    // Only for a buffering writer: the pooled buffer, and pairs of (prefix position, payload length) for the
    // sequences in the order they start. A position of -1 marks a short sequence, whose prefix is already written.
    private byte[]? _buffer;
    private int[]? _sequences;
    private int _sequenceCount;
    private int _longPrefixBytes;

    // Sequences that started and did not end. It is not zero after a write action caught an exception of a nested one.
    private int _openSequences;

    private RlpWriter(Span<byte> destination, bool isContent)
    {
        _destination = destination;
        _isContent = isContent;
    }

    /// <summary>
    /// Gets the number of bytes measured by a <see cref="LengthWriter"/>, or written by a <see cref="ContentWriter"/>.
    /// </summary>
    public readonly int Length => _length + _longPrefixBytes;

    /// <summary>Creates a writer that only measures the length of the items.</summary>
    public static RlpWriter LengthWriter() => new(default, isContent: false);

    /// <summary>Creates a writer that writes the items to <paramref name="destination"/>.</summary>
    /// <remarks>The write action of each sequence runs twice, so prefer <see cref="Rlp"/> to encode a whole item.</remarks>
    public static RlpWriter ContentWriter(Span<byte> destination) => new(destination, isContent: true);

    internal static RlpWriter BufferingWriter()
    {
        byte[] buffer = CachedBuffer ?? new byte[MinBufferLength];
        int[] sequences = CachedSequences ?? new int[MinBufferLength];
        CachedBuffer = null;
        CachedSequences = null;
        return new RlpWriter(buffer, isContent: true)
        {
            _buffer = buffer,
            _sequences = sequences
        };
    }

    /// <summary>Copies the bytes of a buffering writer to <paramref name="output"/>, which has exactly <see cref="Length"/> bytes.</summary>
    internal readonly void CopyBufferedTo(Span<byte> output)
    {
        if (_openSequences != 0) throw new InvalidOperationException("RLP write action did not end all sequences");

        ReadOnlySpan<byte> buffer = _destination[.._length];
        ReadOnlySpan<int> sequences = _sequences.AsSpan(0, _sequenceCount * 2);
        int source = 0;
        int target = 0;
        for (int i = 0; i < sequences.Length; i += 2)
        {
            int prefixPosition = sequences[i];
            if (prefixPosition < 0) continue;

            buffer[source..prefixPosition].CopyTo(output[target..]);
            target += prefixPosition - source;
            target += WriteLongPrefix(output[target..], RlpPrefix.ShortList, sequences[i + 1]);
            source = prefixPosition + 1;
        }

        buffer[source..].CopyTo(output[target..]);
        if (target + buffer.Length - source != output.Length) throw new InvalidOperationException("RLP output has a wrong length");
    }

    internal readonly void ReturnBuffers()
    {
        if (_buffer!.Length <= MaxCachedBufferLength) CachedBuffer = _buffer;
        if (_sequences!.Length * sizeof(int) <= MaxCachedBufferLength) CachedSequences = _sequences;
    }

    /// <summary>Writes a non-negative integer.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is negative.</exception>
    public unsafe void Write<T>(T value) where T : unmanaged, IBinaryInteger<T>
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);

        if (sizeof(T) <= sizeof(ulong))
        {
            WriteUInt64(ulong.CreateTruncating(value));
            return;
        }

        Span<byte> bigEndian = stackalloc byte[sizeof(T)];
        value.WriteBigEndian(bigEndian);
        WriteIntegerBytes(bigEndian);
    }

    /// <summary>Writes a non-negative integer from its big-endian bytes, without leading zeros.</summary>
    public void WriteIntegerBytes(scoped ReadOnlySpan<byte> bigEndian)
    {
        int leadingZeros = bigEndian.IndexOfAnyExcept((byte)0);
        Write(leadingZeros < 0 ? [] : bigEndian[leadingZeros..]);
    }

    private void WriteUInt64(ulong value)
    {
        if (value < RlpPrefix.ShortString)
        {
            if (_isContent) Reserve(1)[0] = value == 0 ? RlpPrefix.ShortString : (byte)value;
            _length++;
            return;
        }

        int byteCount = sizeof(ulong) - BitOperations.LeadingZeroCount(value) / 8;
        if (_isContent)
        {
            Span<byte> destination = Reserve(1 + byteCount);
            destination[0] = (byte)(RlpPrefix.ShortString + byteCount);
            WriteBigEndian(destination[1..], value);
        }

        _length += 1 + byteCount;
    }

    /// <summary>Writes a byte string.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(scoped ReadOnlySpan<byte> value)
    {
        if (value.Length == 1 && value[0] < RlpPrefix.ShortString)
        {
            if (_isContent) Reserve(1)[0] = value[0];
            _length++;
            return;
        }

        if (value.Length > RlpPrefix.MaxShortLength)
        {
            WriteLongString(value);
            return;
        }

        if (_isContent)
        {
            Span<byte> destination = Reserve(1 + value.Length);
            destination[0] = (byte)(RlpPrefix.ShortString + value.Length);
            value.CopyTo(destination[1..]);
        }

        _length += 1 + value.Length;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void WriteLongString(scoped ReadOnlySpan<byte> value)
    {
        int prefixLength = 1 + LengthOfLength(value.Length);
        if (_isContent)
        {
            Span<byte> destination = Reserve(prefixLength + value.Length);
            WriteLongPrefix(destination, RlpPrefix.ShortString, value.Length);
            value.CopyTo(destination[prefixLength..]);
        }

        _length += prefixLength + value.Length;
    }

    /// <summary>Writes a sequence whose payload is written by <paramref name="action"/>.</summary>
    public void WriteSequence(RefRlpWriterAction action)
        => WriteSequence(action, static (ref RlpWriter w, RefRlpWriterAction action) => action(ref w));

    /// <inheritdoc cref="WriteSequence(RefRlpWriterAction)"/>
    public void WriteSequence<TContext>(TContext ctx, RefRlpWriterAction<TContext> action)
    {
        // A decoded value can be as deep as the reader allows, so encoding it again must not overflow the stack.
        int openSequences = ++_openSequences;
        if (openSequences % StackCheckInterval == 0) RuntimeHelpers.EnsureSufficientExecutionStack();

        if (!_isContent)
        {
            int payloadStart = _length;
            action(ref this, ctx);
            int payloadLength = _length - payloadStart;
            _length += payloadLength > RlpPrefix.MaxShortLength ? 1 + LengthOfLength(payloadLength) : 1;
        }
        else if (_buffer is null)
        {
            WriteMeasuredSequence(ctx, action);
        }
        else
        {
            WriteBufferedSequence(ctx, action);
        }

        if (_openSequences != openSequences) throw new InvalidOperationException("RLP write action did not end all sequences");
        _openSequences = openSequences - 1;
    }

    private void WriteBufferedSequence<TContext>(TContext ctx, RefRlpWriterAction<TContext> action)
    {
        int slot = _sequenceCount * 2;
        if (slot == _sequences!.Length) GrowSequences();
        _sequenceCount++;

        Reserve(1);
        int prefixPosition = _length++;
        int longPrefixBytes = _longPrefixBytes;
        action(ref this, ctx);

        // Payload length in the output, which includes the long prefixes inserted into the payload.
        int payloadLength = _length - prefixPosition - 1 + _longPrefixBytes - longPrefixBytes;
        if (payloadLength <= RlpPrefix.MaxShortLength)
        {
            _destination[prefixPosition] = (byte)(RlpPrefix.ShortList + payloadLength);
            _sequences![slot] = -1;
        }
        else
        {
            _longPrefixBytes += LengthOfLength(payloadLength);
            _sequences![slot] = prefixPosition;
            _sequences[slot + 1] = payloadLength;
        }
    }

    /// <summary>Writes a sequence whose payload of <paramref name="payloadLength"/> bytes is written by <paramref name="action"/>.</summary>
    /// <remarks>
    /// A known payload length saves work: a <see cref="LengthWriter"/> does not run <paramref name="action"/>, and the
    /// other writers write the exact prefix at once.
    /// </remarks>
    /// <exception cref="InvalidOperationException"><paramref name="action"/> writes another number of bytes.</exception>
    public void WriteSequence<TContext>(int payloadLength, TContext ctx, RefRlpWriterAction<TContext> action)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(payloadLength);
        int prefixLength = payloadLength > RlpPrefix.MaxShortLength ? 1 + LengthOfLength(payloadLength) : 1;
        if (!_isContent)
        {
            _length += prefixLength + payloadLength;
            return;
        }

        int openSequences = ++_openSequences;
        if (openSequences % StackCheckInterval == 0) RuntimeHelpers.EnsureSufficientExecutionStack();

        Span<byte> prefix = Reserve(prefixLength);
        if (prefixLength == 1)
        {
            prefix[0] = (byte)(RlpPrefix.ShortList + payloadLength);
        }
        else
        {
            WriteLongPrefix(prefix, RlpPrefix.ShortList, payloadLength);
        }

        _length += prefixLength;
        int payloadStart = _length;
        int longPrefixBytes = _longPrefixBytes;
        action(ref this, ctx);

        if (_length - payloadStart + _longPrefixBytes - longPrefixBytes != payloadLength)
        {
            throw new InvalidOperationException("RLP write action wrote another length than the sequence declares");
        }

        if (_openSequences != openSequences) throw new InvalidOperationException("RLP write action did not end all sequences");
        _openSequences = openSequences - 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void WriteMeasuredSequence<TContext>(TContext ctx, RefRlpWriterAction<TContext> action)
    {
        RlpWriter lengthWriter = LengthWriter();
        action(ref lengthWriter, ctx);
        int payloadLength = lengthWriter._length;

        if (payloadLength > RlpPrefix.MaxShortLength)
        {
            int prefixLength = WriteLongPrefix(Reserve(1 + LengthOfLength(payloadLength)), RlpPrefix.ShortList, payloadLength);
            _length += prefixLength;
        }
        else
        {
            Reserve(1)[0] = (byte)(RlpPrefix.ShortList + payloadLength);
            _length++;
        }

        int payloadStart = _length;
        action(ref this, ctx);
        if (_length - payloadStart != payloadLength) throw new InvalidOperationException("RLP write action wrote a different payload than it measured");
    }

    /// <summary>Returns the destination span for the next <paramref name="count"/> bytes, and grows the buffer if needed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Span<byte> Reserve(int count)
    {
        if ((uint)(_length + count) > (uint)_destination.Length) Grow(count);
        return _destination.Slice(_length, count);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow(int count)
    {
        if (_buffer is null) throw new ArgumentException("RLP destination is too short");

        byte[] larger = GC.AllocateUninitializedArray<byte>(Math.Max(_buffer.Length * 2, _length + count));
        _buffer.AsSpan(0, _length).CopyTo(larger);
        _buffer = larger;
        _destination = larger;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void GrowSequences()
    {
        int[] sequences = _sequences!;
        int[] larger = new int[sequences.Length * 2];
        sequences.AsSpan().CopyTo(larger);
        _sequences = larger;
    }

    private static int LengthOfLength(int length) => sizeof(int) - BitOperations.LeadingZeroCount((uint)length) / 8;

    /// <returns>The number of bytes written.</returns>
    private static int WriteLongPrefix(Span<byte> destination, byte shortPrefix, int payloadLength)
    {
        int lengthOfLength = LengthOfLength(payloadLength);
        destination[0] = (byte)(shortPrefix + RlpPrefix.MaxShortLength + lengthOfLength);
        WriteBigEndian(destination.Slice(1, lengthOfLength), (uint)payloadLength);
        return 1 + lengthOfLength;
    }

    private static void WriteBigEndian(Span<byte> destination, ulong value)
    {
        for (int i = destination.Length - 1; i >= 0; i--)
        {
            destination[i] = (byte)value;
            value >>= 8;
        }
    }
}
