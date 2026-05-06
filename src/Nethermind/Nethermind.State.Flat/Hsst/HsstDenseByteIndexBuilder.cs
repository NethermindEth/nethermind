// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Builds a byte-addressed HSST: the tag byte is itself the array index. Tags are
/// added in strictly ascending order; any byte position skipped between two
/// consecutive Adds is auto-filled with a zero-length entry so the on-disk
/// <c>Ends</c> array remains contiguous and indexable by the lookup-key byte.
///
/// Output: concatenated values followed by
/// <c>[Ends: N·u32 LE][Count: u8 = N − 1][IndexType: u8 = 0x09]</c>. <c>N</c>
/// equals <c>(highestTag + 1)</c> and is capped at <see cref="MaxEntries"/> (256).
/// </summary>
public ref struct HsstDenseByteIndexBuilder<TWriter>
    where TWriter : IByteBufferWriter
{
    /// <summary>Maximum entries (and hence one past the maximum tag). The on-disk
    /// <c>Count</c> byte stores <c>N − 1</c>, so a single byte covers 1..256.</summary>
    public const int MaxEntries = 256;

    private const int InitialCapacity = 16;

    private ref TWriter _writer;
    private readonly int _baseOffset;
    private int _writtenBeforeValue;
    /// <summary>Number of entries appended so far, including auto-filled gap entries.</summary>
    private int _count;
    private uint[]? _ends;

    public HsstDenseByteIndexBuilder(ref TWriter writer)
    {
        _writer = ref writer;
        _baseOffset = _writer.Written;
        _count = 0;
    }

    public void Dispose()
    {
        if (_ends is not null) { ArrayPool<uint>.Shared.Return(_ends); _ends = null; }
    }

    /// <summary>
    /// Begin writing a value. After writing the value bytes, call
    /// <see cref="FinishValueWrite(byte)"/> with the entry's tag.
    /// </summary>
    public ref TWriter BeginValueWrite()
    {
        _writtenBeforeValue = _writer.Written;
        return ref _writer;
    }

    /// <summary>
    /// Finish a value previously begun with <see cref="BeginValueWrite"/>.
    /// <paramref name="tag"/> must be strictly greater than the previously written
    /// tag; intervening byte positions are auto-filled with zero-length entries.
    /// </summary>
    public void FinishValueWrite(byte tag)
    {
        // Strictly ascending: previously-written highest tag is _count - 1, so the
        // next tag must satisfy tag >= _count. (tag is a byte, so tag < 256 always
        // holds — the upper bound is enforced by the type.)
        if (tag < _count)
            throw new ArgumentException($"Tags must be strictly ascending; got 0x{tag:X2} after entry index {_count - 1}", nameof(tag));

        EnsureCapacity(tag + 1);
        uint end = (uint)(_writer.Written - _baseOffset);
        // Fill any gap positions [_count.._count-of-tag) with zero-length entries
        // pointing at _writtenBeforeValue (the new entry's value start; i.e. the
        // previous cumulative end).
        uint gapEnd = (uint)(_writtenBeforeValue - _baseOffset);
        for (int i = _count; i < tag; i++)
            _ends![i] = gapEnd;
        _ends![tag] = end;
        _count = tag + 1;
    }

    private void EnsureCapacity(int needed)
    {
        int current = _ends?.Length ?? 0;
        if (needed <= current) return;

        int newCap = current == 0 ? InitialCapacity : current * 2;
        if (newCap < needed) newCap = needed;

        uint[] newEnds = ArrayPool<uint>.Shared.Rent(newCap);
        if (_ends is not null)
        {
            Array.Copy(_ends, newEnds, _count);
            ArrayPool<uint>.Shared.Return(_ends);
        }
        _ends = newEnds;
    }

    /// <summary>Convenience: write a tag/value pair in one call.</summary>
    public void Add(byte tag, scoped ReadOnlySpan<byte> value)
    {
        _writtenBeforeValue = _writer.Written;
        IByteBufferWriter.Copy(ref _writer, value);
        FinishValueWrite(tag);
    }

    /// <summary>Span overload; tag must be a single byte.</summary>
    public void FinishValueWrite(scoped ReadOnlySpan<byte> tag)
    {
        if (tag.Length != 1)
            throw new ArgumentException($"DenseByteIndex requires single-byte tags; got length {tag.Length}", nameof(tag));
        FinishValueWrite(tag[0]);
    }

    /// <summary>Span overload of <see cref="Add(byte, ReadOnlySpan{byte})"/>; tag must be a single byte.</summary>
    public void Add(scoped ReadOnlySpan<byte> tag, scoped ReadOnlySpan<byte> value)
    {
        if (tag.Length != 1)
            throw new ArgumentException($"DenseByteIndex requires single-byte tags; got length {tag.Length}", nameof(tag));
        Add(tag[0], value);
    }

    /// <summary>
    /// Append the trailer (<c>[Ends][Count][IndexType]</c>). The writer is already
    /// advanced through every value and gap-fill at this point.
    /// </summary>
    public void Build()
    {
        int n = _count;
        if (n == 0)
            throw new InvalidOperationException("DenseByteIndex cannot encode an empty map; the caller must omit Build for zero-entry maps");

        // Ends section.
        Span<byte> endsSpan = _writer.GetSpan(n * 4);
        for (int i = 0; i < n; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(endsSpan[(i * 4)..], _ends![i]);
        _writer.Advance(n * 4);

        // Count + IndexType (Count stores N − 1 so a single byte covers 1..256).
        Span<byte> trailer = _writer.GetSpan(2);
        trailer[0] = (byte)(n - 1);
        trailer[1] = (byte)IndexType.DenseByteIndex;
        _writer.Advance(2);
    }
}
