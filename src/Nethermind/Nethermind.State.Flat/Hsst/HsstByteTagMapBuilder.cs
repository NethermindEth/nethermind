// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Builds a tiny single-byte-keyed HSST. The output is concatenated values followed by a
/// flat trailer: <c>[Ends: N×OffsetSize LE][Tags: N×u8][Count: u8 = N - 1][OffsetSize: u8][IndexType: u8 = 0x03]</c>.
/// <c>OffsetSize</c> is chosen at <see cref="Build"/> time from the running values total
/// (1, 2, 4, or 6 bytes — the same policy as <see cref="HsstOffset.ChooseOffsetSize"/>),
/// so small maps pay 1 byte per cumulative end instead of a fixed 4.
///
/// Designed for the persisted-snapshot column container (≤7 entries), per-address
/// sub-tag map (≤3 entries), and the slot-suffix bucket (≤256 entries) where the
/// b-tree's fixed parse cost dominates.
///
/// Tags must be added in strictly ascending order. <c>N</c> is capped at
/// <see cref="MaxEntries"/> (256). The on-disk <c>Count</c> byte stores <c>N - 1</c>,
/// so 0..255 cover all 256 possible entry counts; the empty map cannot be represented
/// — callers must skip <see cref="Build"/> for empty maps.
/// </summary>
public ref struct HsstByteTagMapBuilder<TWriter>
    where TWriter : IByteBufferWriter
{
    /// <summary>
    /// Maximum entries per ByteTagMap HSST. The on-disk <c>Count</c> byte stores
    /// <c>N - 1</c>, so a single byte covers entry counts 1..256.
    /// </summary>
    public const int MaxEntries = 256;

    private const int InitialCapacity = 16;

    private ref TWriter _writer;
    private readonly long _baseOffset;
    private long _writtenBeforeValue;
    private int _count;
    private byte[]? _tags;
    private long[]? _ends;

    /// <summary>
    /// Create a builder writing via <paramref name="writer"/>. The trailing
    /// <see cref="IndexType"/> byte is appended in <see cref="Build"/>.
    /// </summary>
    public HsstByteTagMapBuilder(ref TWriter writer)
    {
        _writer = ref writer;
        _baseOffset = _writer.Written;
        _count = 0;
    }

    /// <summary>Returns rented working buffers (if any) to the shared array pool.</summary>
    public void Dispose()
    {
        if (_tags is not null) { ArrayPool<byte>.Shared.Return(_tags); _tags = null; }
        if (_ends is not null) { ArrayPool<long>.Shared.Return(_ends); _ends = null; }
    }

    /// <summary>
    /// Begin writing a value. Returns a ref to the shared writer and snapshots the current
    /// write position. After writing the value bytes, call <see cref="FinishValueWrite(byte)"/>
    /// with the entry's tag.
    /// </summary>
    public ref TWriter BeginValueWrite()
    {
        _writtenBeforeValue = _writer.Written;
        return ref _writer;
    }

    /// <summary>
    /// Finish a value previously begun with <see cref="BeginValueWrite"/>. <paramref name="tag"/>
    /// must be strictly greater than the previously written tag.
    /// </summary>
    public void FinishValueWrite(byte tag)
    {
        if (_count >= MaxEntries)
            throw new InvalidOperationException($"ByteTagMap supports at most {MaxEntries} entries (Count byte stores N-1)");
        if (_count > 0 && tag <= _tags![_count - 1])
            throw new ArgumentException($"Tags must be strictly ascending; got 0x{tag:X2} after 0x{_tags[_count - 1]:X2}", nameof(tag));

        EnsureCapacity(_count + 1);
        long end = _writer.Written - _baseOffset;
        _tags![_count] = tag;
        _ends![_count] = end;
        _count++;
    }

    private void EnsureCapacity(int needed)
    {
        int current = _tags?.Length ?? 0;
        if (needed <= current) return;

        int newCap = current == 0 ? InitialCapacity : current * 2;
        if (newCap < needed) newCap = needed;

        byte[] newTags = ArrayPool<byte>.Shared.Rent(newCap);
        long[] newEnds = ArrayPool<long>.Shared.Rent(newCap);
        if (_tags is not null)
        {
            Array.Copy(_tags, newTags, _count);
            Array.Copy(_ends!, newEnds, _count);
            ArrayPool<byte>.Shared.Return(_tags);
            ArrayPool<long>.Shared.Return(_ends!);
        }
        _tags = newTags;
        _ends = newEnds;
    }

    /// <summary>Convenience: write a tag/value pair in one call.</summary>
    public void Add(byte tag, scoped ReadOnlySpan<byte> value)
    {
        _writtenBeforeValue = _writer.Written;
        IByteBufferWriter.Copy(ref _writer, value);
        FinishValueWrite(tag);
    }

    /// <summary>
    /// Span overload for symmetry with <see cref="HsstBuilder{TWriter}.FinishValueWrite"/> —
    /// the tag must be a single byte; multi-byte spans throw.
    /// </summary>
    public void FinishValueWrite(scoped ReadOnlySpan<byte> tag)
    {
        if (tag.Length != 1)
            throw new ArgumentException($"ByteTagMap requires single-byte tags; got length {tag.Length}", nameof(tag));
        FinishValueWrite(tag[0]);
    }

    /// <summary>Span overload of <see cref="Add(byte, ReadOnlySpan{byte})"/>; tag must be a single byte.</summary>
    public void Add(scoped ReadOnlySpan<byte> tag, scoped ReadOnlySpan<byte> value)
    {
        if (tag.Length != 1)
            throw new ArgumentException($"ByteTagMap requires single-byte tags; got length {tag.Length}", nameof(tag));
        Add(tag[0], value);
    }

    /// <summary>
    /// Append the trailer (<c>[Ends][Tags][Count][OffsetSize][IndexType]</c>) to the writer.
    /// The writer is already advanced through every value at this point.
    /// </summary>
    public void Build()
    {
        int n = _count;
        if (n == 0)
            throw new InvalidOperationException("ByteTagMap cannot encode an empty map; the caller must omit Build for zero-entry maps");

        // Pick the smallest end-offset width that fits the cumulative max (= last entry's end).
        long valuesTotal = _ends![n - 1];
        int offsetSize = HsstOffset.ChooseOffsetSize(valuesTotal);

        // Ends section, written at the chosen stride. Use an 8-byte scratch and slice
        // off the low offsetSize bytes (LE).
        Span<byte> endsSpan = _writer.GetSpan(n * offsetSize);
        Span<byte> scratch = stackalloc byte[8];
        for (int i = 0; i < n; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(scratch, (ulong)_ends![i]);
            scratch[..offsetSize].CopyTo(endsSpan[(i * offsetSize)..]);
        }
        _writer.Advance(n * offsetSize);

        // Tags section (adjacent to Count so reader hits it on the same cache line).
        Span<byte> tagsSpan = _writer.GetSpan(n);
        for (int i = 0; i < n; i++) tagsSpan[i] = _tags![i];
        _writer.Advance(n);

        // Trailer: Count (N - 1) + OffsetSize + IndexType.
        Span<byte> trailer = _writer.GetSpan(3);
        trailer[0] = (byte)(n - 1);
        trailer[1] = (byte)offsetSize;
        trailer[2] = (byte)IndexType.ByteTagMap;
        _writer.Advance(3);
    }
}
