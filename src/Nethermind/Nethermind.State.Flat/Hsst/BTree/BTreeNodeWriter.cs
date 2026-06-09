// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.Hsst.BTree;

/// <summary>
/// Writes a B-tree index node in one call from already-laid-out caller buffers.
/// </summary>
/// <remarks>
/// Node wire layout (header, Flags bits, value-slot widths, Variable-key SoA section):
/// see <c>Hsst/FORMAT.md</c>, "B-tree index node layout" and "Keys section (Variable)".
/// When <c>CommonPrefixLen &gt; 0</c> the prefix bytes themselves are supplied by the
/// descending caller (the parent's separator), not stored in the node.
/// <para>
/// Inputs to <see cref="Write{TWriter}"/> are already in their final shape:
/// <c>fullKeys</c> is a flat <c>count * fullKeyLength</c> buffer (entry i lives at
/// <c>fullKeys[i * fullKeyLength ..][..fullKeyLength]</c>); each entry's emitted key is
/// the slice <c>[prefixLen, sepLengths[i])</c> of its full key (Variable) or
/// <c>[prefixLen, prefixLen + metadata.KeySlotSize)</c> (Uniform). <c>values</c> is a
/// flat <c>count * metadata.ValueSlotSize</c> buffer, each entry already encoded LE with
/// any <c>metadata.BaseOffset</c> subtracted.
/// </para>
/// </remarks>
internal static class BTreeNodeWriter<TWriter>
    where TWriter : IByteBufferWriter
{
    private const int HeaderSize = 12;

    /// <summary>14-bit tailOffset cap for the prefix-inlined Variable key section.</summary>
    private const int MaxVariableKeyTailBytes = (1 << 14) - 1; // 16383

    /// <summary>
    /// Write the empty-node form: header only (KeyCount = KeySize = 0, CommonPrefixLen = 0).
    /// For an empty intermediate node (single-child b-tree intermediate, no separators)
    /// <see cref="BTreeNodeMetadata.BaseOffset"/> names the lone child's absolute offset
    /// and the reader's no-floor fallback descends to it.
    /// </summary>
    public static void WriteEmpty(ref TWriter writer, in BTreeNodeMetadata metadata)
    {
        // [Flags u8][KeyCount=0 u16][KeySize=0 u16][CommonPrefixLen=0 u8][BaseOffset 6 bytes LE]
        // ValueSlotSize is encoded into the Flags byte but is meaningless when KeyCount = 0;
        // default to 2 (the smallest supported width).
        if (metadata.BaseOffset > 0xFFFF_FFFF_FFFFUL)
            throw new InvalidOperationException(
                $"BaseOffset {metadata.BaseOffset} exceeds 6-byte (48-bit) header field");
        int emptyValueSlot = metadata.ValueSlotSize == 0 ? 2 : metadata.ValueSlotSize;
        byte flags = EncodeFlags(metadata.NodeKind, keyType: 0, EncodeValueSizeCode(emptyValueSlot), keyLe: false);
        Span<byte> span = writer.GetSpan(HeaderSize);
        span[0] = flags;
        span[1..5].Clear();   // KeyCount(2) + KeySize(2) = 0
        span[5] = 0;          // CommonPrefixLen
        ulong v = metadata.BaseOffset;
        span[6] = (byte)v;
        span[7] = (byte)(v >> 8);
        span[8] = (byte)(v >> 16);
        span[9] = (byte)(v >> 24);
        span[10] = (byte)(v >> 32);
        span[11] = (byte)(v >> 40);
        writer.Advance(HeaderSize);
    }

    /// <summary>
    /// Write the full binary layout for an index node with <paramref name="count"/> entries.
    /// Keys are read from <paramref name="fullKeys"/> using stride <paramref name="fullKeyLength"/>:
    /// for Uniform (<c>metadata.KeyType == 1</c>) each entry contributes
    /// <c>metadata.KeySlotSize</c> bytes starting at <paramref name="prefixLen"/>; for
    /// Variable (<c>metadata.KeyType == 0</c>) entry <c>i</c> contributes
    /// <c>sepLengths[i] - prefixLen</c> bytes starting at <paramref name="prefixLen"/>.
    /// Values are read flat from <paramref name="values"/> with stride
    /// <c>metadata.ValueSlotSize</c>; any <c>metadata.BaseOffset</c> must already have been
    /// subtracted by the caller.
    /// </summary>
    /// <param name="sepLengths">
    /// Per-entry full slice length (key prefix included), used only when
    /// <c>metadata.KeyType == 0</c>. May be empty/<c>default</c> for Uniform.
    /// </param>
    public static void Write(
        ref TWriter writer,
        in BTreeNodeMetadata metadata,
        int count,
        scoped ReadOnlySpan<byte> fullKeys,
        int fullKeyLength,
        int prefixLen,
        scoped ReadOnlySpan<int> sepLengths,
        scoped ReadOnlySpan<byte> values,
        scoped ReadOnlySpan<byte> commonKeyPrefix)
    {
        if (count == 0)
        {
            WriteEmpty(ref writer, metadata);
            return;
        }

        // KeySize header field: per-entry slot size for Uniform; total section byte
        // count for Variable.
        int keySize = metadata.KeyType switch
        {
            1 => metadata.KeySlotSize,
            _ => ComputeVariableKeySectionSize(count, sepLengths, prefixLen),
        };

        // 1) Header.
        WriteHeader(ref writer, in metadata, count, keySize, commonKeyPrefix);

        // 2) Keys section.
        switch (metadata.KeyType)
        {
            case 1:
                WriteUniformKeys(ref writer, in metadata, count, fullKeys, fullKeyLength, prefixLen);
                break;
            default:
                WriteVariableKeys(ref writer, count, fullKeys, fullKeyLength, prefixLen, sepLengths);
                break;
        }

        // 3) Values section — always Uniform (no Variable-value shape for b-tree nodes).
        WriteUniformValues(ref writer, count, values, metadata.ValueSlotSize);

        // When the keys section uses Variable encoding, its u16 offset table cannot
        // address bytes past 64 KiB. We've already enforced that the section alone is
        // below the cap. Cap the *whole* node at 64 KiB so any future Variable-relative
        // offset reasoning stays valid.
        if (metadata.KeyType == 0)
        {
            int totalNodeSize = HeaderSize + keySize + metadata.ValueSlotSize;
            const int MaxVariableNodeSize = 64 * 1024;
            if (totalNodeSize > MaxVariableNodeSize)
                throw new InvalidOperationException(
                    $"Index node with Variable key section exceeds 64 KiB ({totalNodeSize} bytes); split before finalizing.");
        }
    }

    /// <summary>
    /// Map a <see cref="BTreeNodeMetadata.ValueSlotSize"/> to its 2-bit Flags encoding
    /// (bits 4-5): 2→00, 3→01, 4→10, 6→11. Throws if <paramref name="slot"/> is anything
    /// else — values must already be quantized by the caller (see
    /// <c>HsstValueSlot.MinBytesFor</c>).
    /// </summary>
    private static byte EncodeValueSizeCode(int slot) => slot switch
    {
        2 => 0,
        3 => 1,
        4 => 2,
        6 => 3,
        _ => throw new InvalidOperationException(
            $"Unsupported ValueSlotSize {slot}; supported widths are {{2, 3, 4, 6}}")
    };

    /// <summary>
    /// Pack the on-disk <c>Flags</c> byte. Bits 0-1 carry the <see cref="BTreeNodeKind"/>, bits
    /// 2-3 <c>KeyType</c>, bits 4-5 <c>ValueSizeCode</c>, bit 6 <c>IsKeyLittleEndian</c>; bit 7 is
    /// reserved (always 0).
    /// </summary>
    private static byte EncodeFlags(BTreeNodeKind kind, int keyType, byte valueSizeCode, bool keyLe) => (byte)(
        ((byte)kind & 0x03) |
        ((keyType & 0x03) << 2) |
        ((valueSizeCode & 0x03) << 4) |
        (keyLe ? 0x40 : 0x00));

    private static int ComputeVariableKeySectionSize(int count, scoped ReadOnlySpan<int> sepLengths, int prefixLen)
    {
        // SoA layout: [ prefixArr N×u16 ][ offsetArr N×u16 ][ remainingkeys ].
        // Each key contributes 4 bytes (prefix slot + offset slot) plus max(0, len-2) tail bytes.
        int tailBytes = 0;
        for (int i = 0; i < count; i++)
        {
            int len = sepLengths[i] - prefixLen;
            if (len > 2) tailBytes += len - 2;
        }
        if (tailBytes > MaxVariableKeyTailBytes)
            throw new InvalidOperationException(
                $"Variable key tail section ({tailBytes} bytes) exceeds 14-bit tailOffset cap (16 KiB); split before finalizing.");
        return count * 4 + tailBytes;
    }

    private static void WriteHeader(ref TWriter writer, in BTreeNodeMetadata metadata, int count, int keySize, scoped ReadOnlySpan<byte> commonKeyPrefix)
    {
        // Header fields are sized for the 64 KiB per-node cap. ValueSize is encoded as a
        // 2-bit code in Flags bits 3-4 (only {2,3,4,6} are valid); reject anything beyond
        // the encodable range up-front rather than silently truncating.
        if ((uint)count > ushort.MaxValue)
            throw new InvalidOperationException($"Index node entry count {count} exceeds u16 header field");
        if ((uint)keySize > ushort.MaxValue)
            throw new InvalidOperationException($"Index node KeySize {keySize} exceeds u16 header field (node > 64 KiB)");

        int prefixLen = commonKeyPrefix.Length;
        if ((uint)prefixLen > byte.MaxValue)
            throw new InvalidOperationException($"Common key prefix length {prefixLen} exceeds u8 header field");

        bool keyLe = ShouldEncodeKeyLittleEndian(in metadata);
        byte flags = EncodeFlags(metadata.NodeKind, metadata.KeyType, EncodeValueSizeCode(metadata.ValueSlotSize), keyLe);

        if (metadata.BaseOffset > 0xFFFF_FFFF_FFFFUL)
            throw new InvalidOperationException(
                $"BaseOffset {metadata.BaseOffset} exceeds 6-byte (48-bit) header field");

        // Fixed 12-byte header:
        //   [Flags u8][KeyCount u16][KeySize u16][CommonPrefixLen u8][BaseOffset 6 bytes LE]
        // BaseOffset sits at the end so the key-parse-critical bytes are grouped first;
        // BaseOffset is only consumed after a successful floor match.
        Span<byte> head = writer.GetSpan(HeaderSize);
        head[0] = flags;
        BinaryPrimitives.WriteUInt16LittleEndian(head[1..], (ushort)count);
        BinaryPrimitives.WriteUInt16LittleEndian(head[3..], (ushort)keySize);
        head[5] = (byte)prefixLen;
        ulong v = metadata.BaseOffset;
        head[6] = (byte)v;
        head[7] = (byte)(v >> 8);
        head[8] = (byte)(v >> 16);
        head[9] = (byte)(v >> 24);
        head[10] = (byte)(v >> 32);
        head[11] = (byte)(v >> 40);
        writer.Advance(HeaderSize);
    }

    /// <summary>
    /// Whether the keys section should be written byte-reversed (Flags bit 5). Honored only
    /// for the slot widths the SIMD/integer-compare reader path supports.
    /// </summary>
    private static bool ShouldEncodeKeyLittleEndian(in BTreeNodeMetadata metadata)
    {
        // Variable (KeyType=0) is always LE-stored: the prefixArr is unconditionally
        // 2-byte slots and the integer-compare floor-search relies on the byte-reversed
        // encoding regardless of the metadata.IsKeyLittleEndian flag set on the writer.
        if (metadata.KeyType == 0) return true;
        if (!metadata.IsKeyLittleEndian) return false;
        // Honored only for the shapes the SIMD direct-compare fast path supports: Uniform with
        // KeySlotSize ∈ {2,4,8}. GetKey returns raw stored bytes (LE-reversed) under this flag;
        // GetFullKey reverses back into a caller dest.
        return metadata.KeyType == 1 && metadata.KeySlotSize is 2 or 4 or 8;
    }

    private static void WriteUniformKeys(
        ref TWriter writer,
        in BTreeNodeMetadata metadata,
        int count,
        scoped ReadOnlySpan<byte> fullKeys,
        int fullKeyLength,
        int prefixLen)
    {
        int keyLen = metadata.KeySlotSize;
        bool reverse = ShouldEncodeKeyLittleEndian(in metadata);
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> src = fullKeys.Slice(i * fullKeyLength + prefixLen, keyLen);
            if (reverse)
            {
                Span<byte> slot = writer.GetSpan(keyLen);
                ReverseInto(src, slot[..keyLen]);
                writer.Advance(keyLen);
            }
            else
            {
                IByteBufferWriter.Copy(ref writer, src);
            }
        }
    }

    /// <summary>Copy <paramref name="src"/> reversed into <paramref name="dst"/>. Both must be the same length.</summary>
    private static void ReverseInto(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int n = src.Length;
        for (int i = 0; i < n; i++) dst[i] = src[n - 1 - i];
    }

    private static void WriteVariableKeys(
        ref TWriter writer,
        int count,
        scoped ReadOnlySpan<byte> fullKeys,
        int fullKeyLength,
        int prefixLen,
        scoped ReadOnlySpan<int> sepLengths)
    {
        // Wire layout: see Hsst/FORMAT.md, "Keys section (Variable)".
        int prefixArrSize = count * 2;
        int offsetArrSize = count * 2;
        Span<byte> prefixArr = writer.GetSpan(prefixArrSize)[..prefixArrSize];
        // We need to fill prefixArr while walking the keys, but offsetArr depends on the
        // running tail cursor that we also build during the same walk. Compute offsetArr
        // into a temp buffer first, then emit prefix bytes, then offset bytes, then tails.
        Span<ushort> offsets = stackalloc ushort[count];

        int tailCursor = 0;
        for (int i = 0; i < count; i++)
        {
            int len = sepLengths[i] - prefixLen;
            ReadOnlySpan<byte> key = fullKeys.Slice(i * fullKeyLength + prefixLen, len);

            // Prefix slot: LE-stored = byte-reversed original prefix. Original prefix
            // bytes [a, b] → stored [b, a]; LE u16 load of [b, a] = (a<<8)|b.
            byte p0 = len >= 1 ? key[0] : (byte)0;
            byte p1 = len >= 2 ? key[1] : (byte)0;
            prefixArr[i * 2] = p1;
            prefixArr[i * 2 + 1] = p0;

            // Offset slot: lenTag is the actual key length when ≤ 2, else 0b11.
            int lenTag = len <= 2 ? len : 0b11;
            offsets[i] = (ushort)((lenTag << 14) | tailCursor);
            if (len > 2) tailCursor += len - 2;
        }
        if (tailCursor > MaxVariableKeyTailBytes)
            throw new InvalidOperationException(
                $"Variable key tail section ({tailCursor} bytes) exceeds 14-bit tailOffset cap (16 KiB); split before finalizing.");
        writer.Advance(prefixArrSize);

        // Offset array.
        Span<byte> offsetArr = writer.GetSpan(offsetArrSize)[..offsetArrSize];
        for (int i = 0; i < count; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(offsetArr[(i * 2)..], offsets[i]);
        writer.Advance(offsetArrSize);

        // Tail bytes (only for keys with len > 2; in entry order).
        for (int i = 0; i < count; i++)
        {
            int len = sepLengths[i] - prefixLen;
            if (len > 2)
            {
                IByteBufferWriter.Copy(ref writer, fullKeys.Slice(i * fullKeyLength + prefixLen + 2, len - 2));
            }
        }
    }

    private static void WriteUniformValues(ref TWriter writer, int count, scoped ReadOnlySpan<byte> values, int valueSlotSize)
    {
        if (valueSlotSize <= 0) return;
        for (int i = 0; i < count; i++)
        {
            IByteBufferWriter.Copy(ref writer, values.Slice(i * valueSlotSize, valueSlotSize));
        }
    }
}
