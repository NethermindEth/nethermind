// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Buffers.Binary;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Builds a byte-addressed HSST: the tag byte is itself the array index. Tags are
/// added in <b>strictly descending</b> order — the first <see cref="FinishValueWrite(byte)"/>
/// fixes the array size to <c>firstTag + 1</c>, and every subsequent tag must be lower
/// than the previous one. Byte positions skipped between two consecutive Adds (and any
/// positions below the lowest-written tag) are auto-filled with zero-length entries so
/// the on-disk <c>Ends</c> array remains contiguous and indexable by the lookup-key byte.
///
/// Output: concatenated values (laid down high-tag first → low-tag last, so the low-tag
/// blobs sit adjacent to <c>Ends</c>) followed by
/// <c>[Ends: N·OffsetSize LE][Count: u8 = N − 1][OffsetSize: u8][IndexType: u8 = 0x04]</c>.
/// <c>OffsetSize</c> is chosen at <see cref="Build"/> time from the running values total
/// (1, 2, 4, or 6 bytes — the same policy as <see cref="HsstOffset.ChooseOffsetSize"/>).
/// <c>N</c> equals <c>(firstWrittenTag + 1)</c> and is capped at <see cref="MaxEntries"/> (256).
/// </summary>
/// <remarks>
/// The descending insertion contract puts hot small-blob tags (low tag values) at the end
/// of the data section so they share OS pages with the <c>Ends</c> table that lookup-time
/// reads always pin. The reader's per-tag math becomes
/// <c>valueLen = Ends[tag] − (tag == N − 1 ? 0 : Ends[tag + 1])</c>.
///
/// <para>
/// <c>N</c> is fixed by the first <see cref="FinishValueWrite(byte)"/>. Callers can therefore
/// omit the trailer entries for absent high-tag columns simply by not calling the builder for
/// them — every tag strictly above the first written tag is out-of-range from the reader's
/// perspective (<c>TrySeek</c> returns false), so absence and gap-fill are indistinguishable
/// on read. The per-address inner HSST exploits this: an EOA skips storage-trie sub-tags
/// (0x07/0x06/0x05), slots (0x04) and self-destruct (0x03), so the first call is the
/// account sub-tag (0x02) and <c>Ends[]</c> is 3 entries instead of 8.
/// </para>
/// </remarks>
public ref struct HsstDenseByteIndexBuilder<TWriter>
    where TWriter : IByteBufferWriter
{
    /// <summary>Maximum entries (and hence one past the maximum tag). The on-disk
    /// <c>Count</c> byte stores <c>N − 1</c>, so a single byte covers 1..256.</summary>
    public const int MaxEntries = 256;

    /// <summary>Sentinel for "no tag has been written yet" (one past the max byte value).</summary>
    private const int NoTagYet = 256;

    private const int InitialCapacity = 16;

    private ref TWriter _writer;
    private readonly long _baseOffset;
    private long _writtenBeforeValue;
    /// <summary>Size of the Ends array (<c>firstWrittenTag + 1</c>); 0 until the first write.</summary>
    private int _count;
    /// <summary>Most recently written tag (<see cref="NoTagYet"/> before the first write).</summary>
    private int _lastTag;
    private long[]? _ends;

    public HsstDenseByteIndexBuilder(ref TWriter writer)
    {
        _writer = ref writer;
        _baseOffset = _writer.Written;
        _count = 0;
        _lastTag = NoTagYet;
    }

    public void Dispose()
    {
        if (_ends is not null) { ArrayPool<long>.Shared.Return(_ends); _ends = null; }
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
    /// <paramref name="tag"/> must be strictly less than the previously written tag
    /// (the first call accepts any byte and fixes the on-disk array size to
    /// <c>tag + 1</c>); byte positions between this tag and the previous tag are
    /// auto-filled with zero-length entries, as are positions below the lowest
    /// tag at <see cref="Build"/> time.
    /// </summary>
    public void FinishValueWrite(byte tag)
    {
        if (_lastTag == NoTagYet)
        {
            // First write fixes the array size; values are streamed high-tag → low-tag,
            // so the highest tag has prevEnd = 0 and lives at offset 0 in the data section.
            _count = tag + 1;
            EnsureCapacity(_count);
            _ends![tag] = _writer.Written - _baseOffset;
            _lastTag = tag;
            return;
        }

        if (tag >= _lastTag)
            throw new ArgumentException(
                $"Tags must be strictly descending; got 0x{tag:X2} after 0x{_lastTag:X2}", nameof(tag));

        // Gap positions (tag .. _lastTag) exclusive at both ends inherit the cumulative
        // end at the start of this new value (= end of the previously written, higher tag).
        // Reader resolves their length as Ends[i] − Ends[i + 1] = 0.
        long gapEnd = _writtenBeforeValue - _baseOffset;
        for (int i = tag + 1; i < _lastTag; i++)
            _ends![i] = gapEnd;
        _ends![tag] = _writer.Written - _baseOffset;
        _lastTag = tag;
    }

    private void EnsureCapacity(int needed)
    {
        int current = _ends?.Length ?? 0;
        if (needed <= current) return;

        int newCap = current == 0 ? InitialCapacity : current * 2;
        if (newCap < needed) newCap = needed;

        long[] newEnds = ArrayPool<long>.Shared.Rent(newCap);
        if (_ends is not null)
        {
            Array.Copy(_ends, newEnds, _count);
            ArrayPool<long>.Shared.Return(_ends);
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
    /// Append the trailer (<c>[Ends][Count][OffsetSize][IndexType]</c>). The writer is already
    /// advanced through every value and gap-fill at this point.
    /// </summary>
    public void Build()
    {
        int n = _count;
        if (n == 0)
            throw new InvalidOperationException("DenseByteIndex cannot encode an empty map; the caller must omit Build for zero-entry maps");

        // Fill below-range gap positions [0 .. _lastTag) with the smallest written tag's end
        // so they collapse to zero-length on lookup (Ends[i] − Ends[i + 1] = 0).
        long lowestEnd = _ends![_lastTag];
        for (int i = 0; i < _lastTag; i++)
            _ends![i] = lowestEnd;

        // With values streamed high-tag → low-tag, the largest cumulative end now sits at
        // Ends[0] (or anywhere ≤ _lastTag, all equal after the below-range fill).
        long valuesTotal = _ends![0];
        int offsetSize = HsstOffset.ChooseOffsetSize(valuesTotal);

        // Ends section, written at the chosen stride.
        Span<byte> endsSpan = _writer.GetSpan(n * offsetSize);
        Span<byte> scratch = stackalloc byte[8];
        for (int i = 0; i < n; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(scratch, (ulong)_ends![i]);
            scratch[..offsetSize].CopyTo(endsSpan[(i * offsetSize)..]);
        }
        _writer.Advance(n * offsetSize);

        // Trailer: Count (N - 1) + OffsetSize + IndexType.
        Span<byte> trailer = _writer.GetSpan(3);
        trailer[0] = (byte)(n - 1);
        trailer[1] = (byte)offsetSize;
        trailer[2] = (byte)IndexType.DenseByteIndex;
        _writer.Advance(3);
    }
}
