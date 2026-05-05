// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Builds a tiny single-byte-keyed HSST. The output is concatenated values followed by a
/// flat trailer: <c>[Ends: N×u32 LE][Tags: N×u8][Count: u8 = N][IndexType: u8 = 0x08]</c>.
/// Designed for the persisted-snapshot column container (≤7 entries), per-address
/// sub-tag map (≤3 entries), and the slot-suffix bucket (≤256 entries) where the
/// b-tree's fixed parse cost dominates.
///
/// Tags must be added in strictly ascending order. <c>N</c> is capped at
/// <see cref="MaxEntries"/> (255) — the on-disk <c>Count</c> field is a single byte.
/// </summary>
public ref struct HsstByteTagMapBuilder<TWriter>
    where TWriter : IByteBufferWriter
{
    /// <summary>
    /// Maximum entries per ByteTagMap HSST — the on-disk <c>Count</c> field is a
    /// single byte, and <c>0</c> is reserved for the empty case.
    /// </summary>
    public const int MaxEntries = 255;

    private const int InitialCapacity = 16;

    private ref TWriter _writer;
    private readonly int _baseOffset;
    private int _writtenBeforeValue;
    private int _count;
    private byte[]? _tags;
    private uint[]? _ends;

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
        if (_ends is not null) { ArrayPool<uint>.Shared.Return(_ends); _ends = null; }
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
        if (_count > 0 && tag <= _tags![_count - 1])
            throw new ArgumentException($"Tags must be strictly ascending; got 0x{tag:X2} after 0x{_tags[_count - 1]:X2}", nameof(tag));
        if (_count >= MaxEntries)
            throw new InvalidOperationException($"ByteTagMap supports at most {MaxEntries} entries (Count is u8)");

        EnsureCapacity(_count + 1);
        uint end = (uint)(_writer.Written - _baseOffset);
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
        uint[] newEnds = ArrayPool<uint>.Shared.Rent(newCap);
        if (_tags is not null)
        {
            Array.Copy(_tags, newTags, _count);
            Array.Copy(_ends!, newEnds, _count);
            ArrayPool<byte>.Shared.Return(_tags);
            ArrayPool<uint>.Shared.Return(_ends!);
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
    /// Append the trailer (<c>[Ends][Tags][Count][IndexType]</c>) to the writer. The writer
    /// is already advanced through every value at this point.
    /// </summary>
    public void Build()
    {
        int n = _count;
        if (n > 0)
        {
            // Ends section.
            Span<byte> endsSpan = _writer.GetSpan(n * 4);
            for (int i = 0; i < n; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(endsSpan[(i * 4)..], _ends![i]);
            _writer.Advance(n * 4);

            // Tags section (adjacent to Count so reader hits it on the same cache line).
            Span<byte> tagsSpan = _writer.GetSpan(n);
            for (int i = 0; i < n; i++) tagsSpan[i] = _tags![i];
            _writer.Advance(n);
        }

        Span<byte> trailer = _writer.GetSpan(2);
        trailer[0] = (byte)n;
        trailer[1] = (byte)IndexType.ByteTagMap;
        _writer.Advance(2);
    }
}
