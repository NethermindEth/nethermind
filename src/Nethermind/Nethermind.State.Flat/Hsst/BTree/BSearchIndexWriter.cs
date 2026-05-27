// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.Hsst.BTree;

/// <summary>
/// Metadata describing the format of an index node to build.
/// </summary>
internal struct BSearchIndexMetadata
{
    /// <summary>Which kind of addressable thing this is.</summary>
    /// <remarks>
    /// Encoded in the low 2 bits of the on-disk <c>Flags</c> byte. The writer emits only
    /// <see cref="BSearchNodeKind.Intermediate"/>; <see cref="BSearchNodeKind.Entry"/> is the
    /// kind used by data-region entry records and is not written here.
    /// </remarks>
    public BSearchNodeKind NodeKind;

    /// <summary>0=Variable, 1=Uniform.</summary>
    public int KeyType;
    /// <summary>
    /// Base offset subtracted from values before writing. 0 means no base offset.
    /// When non-zero, caller must subtract this from each value before calling AddKey.
    /// Encoded on disk as a fixed 6-byte LE field (max 2^48 − 1 ≈ 256 TiB).
    /// </summary>
    public ulong BaseOffset;
    /// <summary>
    /// Uniform: fixed key length or slot size.
    /// Variable: ignored.
    /// </summary>
    public int KeySlotSize;
    /// <summary>
    /// Fixed value size in bytes. The on-disk Flags byte encodes the slot width in 2 bits
    /// (bits 3-4), so only the four widths <c>{2, 3, 4, 6}</c> are valid; the writer rejects
    /// anything else. B-tree index nodes always use Uniform values; there is no
    /// Variable-value shape. Default: 4 bytes.
    /// </summary>
    public int ValueSlotSize = 4;
    /// <summary>
    /// When true, fixed-width key slots are written byte-reversed on disk so that an x86
    /// little-endian integer load of a slot equals its semantic numeric/lex value. The SIMD
    /// floor scan can then drop the per-lane byte-swap shuffle. Honored only for Uniform with
    /// <see cref="KeySlotSize"/> ∈ {2,4,8}; ignored for other shapes. Encoded as Flags bit 6
    /// in the on-disk header.
    /// </summary>
    public bool IsKeyLittleEndian = false;

    public BSearchIndexMetadata() => NodeKind = BSearchNodeKind.Intermediate;
}

/// <summary>
/// Writes B-tree index nodes using an AddKey/Finalize builder pattern.
///
/// Index node layout (low → high address):
///   [Flags: u8][KeyCount: u16 LE][KeySize: u16 LE][CommonPrefixLen: u8][BaseOffset: 6-byte LE]
///   [Keys section][Values section]
///
/// Header is a fixed 12 bytes. <c>BaseOffset</c> sits at the end of the header so that the
/// fields needed to parse the keys section (KeyCount, KeySize, KeyType / IsKeyLittleEndian
/// from Flags, CommonPrefixLen) live in the first 6 bytes; the cold-cache parse of the
/// key-section layout completes before paying for the BaseOffset read, which is only
/// consumed by value resolution after a successful floor match. The trailing
/// <c>CommonPrefixLen</c> may be 0 — meaning no prefix optimization for this node. When
/// non-zero, the actual prefix bytes are supplied by the descending caller (via the
/// parent's separator — the builder guarantees every separator length ≥ the matching
/// child's prefix length). Readers parse forward from the first byte; the parent stores
/// the child's first-byte offset. Putting the metadata header before the keys/values
/// section lets the hardware prefetcher pull the entry data into L1/L2 while the search
/// code is still parsing the header.
///
/// The <c>Flags</c> byte is shared with the data-region's per-entry flag byte; bits 0-1 carry a
/// <see cref="BSearchNodeKind"/> (Entry or Intermediate) so the BTree reader's dispatch loop
/// can recognize what kind of thing it is sitting on from a single byte read. For
/// <see cref="BSearchNodeKind.Intermediate"/>, bits 2-3 carry <c>KeyType</c>, bits 4-5
/// <c>ValueSizeCode</c>, bit 6 <c>IsKeyLittleEndian</c>, and bit 7 is reserved.
/// <see cref="BSearchNodeKind.Entry"/> uses bits 2-7 as reserved zero.
///
/// Values are always Uniform: each entry's value slot is a fixed-width LE integer whose
/// width is one of <c>{2, 3, 4, 6}</c> — encoded as the 2-bit field at Flags bits 4-5
/// (00→2, 01→3, 10→4, 11→6). There is no Variable-value shape in b-tree index nodes.
///
/// Variable-encoded KEYS (KeyType=0) use a Structure-of-Arrays layout that inlines the
/// first 2 bytes of every key for cache-friendly binary search:
///   [ prefixArr: N × u16 LE ][ offsetArr: N × u16 LE ][ remainingkeys bytes ]
/// where each <c>offsetArr[i]</c> packs <c>(lenTag &lt;&lt; 14) | tailOffset</c>:
///   tag 00 = key length 0, tag 01 = length 1, tag 10 = length 2 (no tail),
///   tag 11 = length ≥ 3 (tail bytes start at <c>tailOffset</c> in remainingkeys).
/// Tail length for tag 11 is sentinel-derived: <c>offsetArr[i+1].tailOffset - offsetArr[i].tailOffset</c>
/// (the implicit sentinel for i = N is <c>remainingkeys.Length</c>). Tags 00/01/10 don't
/// advance the tail cursor, so their offset equals the next tag-11 entry's offset.
/// Prefixes are byte-reversed on disk (Flags bit 6 / IsKeyLittleEndian set unconditionally
/// for KeyType=0) so a u16 LE load yields a value with the same ordering as a lex compare
/// on the original 2 bytes — feeding the existing 2-byte SIMD floor-scan path.
/// The 14-bit tailOffset caps remainingkeys at 16 KiB per section.
///
/// Usage: create with writer + metadata + key/value scratch buffers, call AddKey(key, value)
/// for each entry in sorted key order, call FinalizeNode() to flush the binary layout.
///
/// <paramref name="keyBuffer"/> holds intermediate key data during build. Required size:
/// sum of (2 + key.Length) for each entry. <paramref name="valueBuffer"/> mirrors that for
/// values: sum of (2 + value.Length). Both are sized by the caller from the known per-node
/// upper bound and reused across nodes.
/// </summary>
internal ref struct BSearchIndexWriter<TWriter>
    where TWriter : IByteBufferWriter
{
    private ref TWriter _writer;
    private readonly BSearchIndexMetadata _metadata;
    private readonly Span<byte> _keyBuf;
    private readonly Span<byte> _valueBuf;
    private readonly ReadOnlySpan<byte> _commonKeyPrefix;
    private int _count;
    private int _keyPos;    // grows forward from 0 in _keyBuf
    private int _valuePos;  // grows forward from 0 in _valueBuf

    public BSearchIndexWriter(
        ref TWriter writer,
        BSearchIndexMetadata metadata,
        Span<byte> keyBuffer,
        Span<byte> valueBuffer,
        ReadOnlySpan<byte> commonKeyPrefix = default)
    {
        _writer = ref writer;
        _metadata = metadata;
        _keyBuf = keyBuffer;
        _valueBuf = valueBuffer;
        _commonKeyPrefix = commonKeyPrefix;
        _count = 0;
        _keyPos = 0;
        _valuePos = 0;
    }

    /// <summary>
    /// Add a key-value pair. Must be called in sorted key order.
    /// If <see cref="BSearchIndexMetadata.BaseOffset"/> is non-zero, value bytes must already
    /// have the base offset subtracted before calling AddKey.
    /// </summary>
    public void AddKey(scoped ReadOnlySpan<byte> key, scoped ReadOnlySpan<byte> value)
    {
        // Buffer value: [u16 length][value bytes]
        BinaryPrimitives.WriteUInt16LittleEndian(_valueBuf[_valuePos..], (ushort)value.Length);
        _valuePos += 2;
        value.CopyTo(_valueBuf[_valuePos..]);
        _valuePos += value.Length;

        // Store key in keyBuf: [u16 length][key bytes]
        BinaryPrimitives.WriteUInt16LittleEndian(_keyBuf[_keyPos..], (ushort)key.Length);
        _keyPos += 2;
        key.CopyTo(_keyBuf[_keyPos..]);
        _keyPos += key.Length;

        _count++;
    }

    /// <summary>
    /// Write the final binary layout. The ref writer is already advanced.
    ///
    /// <see cref="BSearchIndexMetadata.KeyType"/>, <see cref="BSearchIndexMetadata.KeySlotSize"/>,
    /// and the common-key-prefix passed at construction are taken as-is — the writer does
    /// not auto-detect or adjust. Callers (e.g. <c>HsstBTreeBuilder</c>) decide both jointly
    /// via <see cref="BSearchIndexLayoutPlanner.Plan"/> and pre-strip prefix bytes from
    /// each <see cref="AddKey"/> call so that <see cref="_keyBuf"/> already holds suffixes.
    /// </summary>
    public void FinalizeNode()
    {
        if (_count == 0)
        {
            WriteEmptyNode();
            return;
        }

        // Section sizes are known from the buffered scratches without writing yet.
        int keySize = _metadata.KeyType switch
        {
            1 => _metadata.KeySlotSize,
            2 => _metadata.KeySlotSize,
            _ => ComputeVariableKeySectionSize(),
        };
        int valueSize = _metadata.ValueSlotSize;

        // 1) Header.
        WriteHeader(keySize, valueSize, _commonKeyPrefix);

        // 2) Keys section.
        switch (_metadata.KeyType)
        {
            case 1: WriteUniformKeys(); break;
            default: WriteVariableKeys(); break;
        }

        // 3) Values section — always Uniform (no Variable-value shape for b-tree nodes).
        WriteUniformValues();

        // When the keys section uses Variable encoding, its u16 offset table cannot
        // address bytes past 64 KiB. We've already enforced that the section alone is
        // below the cap. Cap the *whole* node at 64 KiB so any future Variable-relative
        // offset reasoning stays valid.
        if (_metadata.KeyType == 0)
        {
            int header = HeaderSize();
            int totalNodeSize = header + keySize + valueSize;
            const int MaxVariableNodeSize = 64 * 1024;
            if (totalNodeSize > MaxVariableNodeSize)
                throw new InvalidOperationException(
                    $"Index node with Variable key section exceeds 64 KiB ({totalNodeSize} bytes); split before finalizing.");
        }
    }

    private int HeaderSize() => 12;

    /// <summary>
    /// Map a <see cref="BSearchIndexMetadata.ValueSlotSize"/> to its 2-bit Flags encoding
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
    /// Pack the on-disk <c>Flags</c> byte. Bits 0-1 carry the <see cref="BSearchNodeKind"/>, bits
    /// 2-3 <c>KeyType</c>, bits 4-5 <c>ValueSizeCode</c>, bit 6 <c>IsKeyLittleEndian</c>; bit 7 is
    /// reserved (always 0).
    /// </summary>
    private static byte EncodeFlags(BSearchNodeKind kind, int keyType, byte valueSizeCode, bool keyLe) => (byte)(
        ((byte)kind & 0x03) |
        ((keyType & 0x03) << 2) |
        ((valueSizeCode & 0x03) << 4) |
        (keyLe ? 0x40 : 0x00));

    private void WriteEmptyNode()
    {
        // Empty header: flags only (leaf/intermediate), KeyCount = KeySize = 0,
        // CommonPrefixLen = 0. BaseOffset is preserved from the caller — for an
        // empty intermediate node (single-child b-tree intermediate, no separators)
        // BaseOffset names the lone child's absolute offset and the reader's
        // no-floor fallback descends to it. ValueSlotSize is encoded into the flags
        // byte but is meaningless when KeyCount = 0; default to 2 (the smallest
        // supported width).
        // [Flags u8][KeyCount=0 u16][KeySize=0 u16][CommonPrefixLen=0 u8][BaseOffset 6 bytes LE]
        if (_metadata.BaseOffset > 0xFFFF_FFFF_FFFFUL)
            throw new InvalidOperationException(
                $"BaseOffset {_metadata.BaseOffset} exceeds 6-byte (48-bit) header field");
        int emptyValueSlot = _metadata.ValueSlotSize == 0 ? 2 : _metadata.ValueSlotSize;
        byte flags = EncodeFlags(_metadata.NodeKind, keyType: 0, EncodeValueSizeCode(emptyValueSlot), keyLe: false);
        Span<byte> span = _writer.GetSpan(12);
        span[0] = flags;
        span[1..5].Clear();   // KeyCount(2) + KeySize(2) = 0
        span[5] = 0;          // CommonPrefixLen
        ulong v = _metadata.BaseOffset;
        span[6] = (byte)v;
        span[7] = (byte)(v >> 8);
        span[8] = (byte)(v >> 16);
        span[9] = (byte)(v >> 24);
        span[10] = (byte)(v >> 32);
        span[11] = (byte)(v >> 40);
        _writer.Advance(12);
    }

    /// <summary>14-bit tailOffset cap for the prefix-inlined Variable key section.</summary>
    private const int MaxVariableKeyTailBytes = (1 << 14) - 1; // 16383

    private int ComputeVariableKeySectionSize()
    {
        // SoA layout: [ prefixArr N×u16 ][ offsetArr N×u16 ][ remainingkeys ].
        // Each key contributes 4 bytes (prefix slot + offset slot) plus max(0, len-2) tail bytes.
        int tailBytes = 0;
        int keySrc = 0;
        for (int i = 0; i < _count; i++)
        {
            int len = BinaryPrimitives.ReadUInt16LittleEndian(_keyBuf[keySrc..]);
            keySrc += 2 + len;
            if (len > 2) tailBytes += len - 2;
        }
        if (tailBytes > MaxVariableKeyTailBytes)
            throw new InvalidOperationException(
                $"Variable key tail section ({tailBytes} bytes) exceeds 14-bit tailOffset cap (16 KiB); split before finalizing.");
        return _count * 4 + tailBytes;
    }

    private void WriteHeader(int keySize, int valueSize, scoped ReadOnlySpan<byte> commonKeyPrefix)
    {
        // Header fields are sized for the 64 KiB per-node cap. ValueSize is encoded as a
        // 2-bit code in Flags bits 3-4 (only {2,3,4,6} are valid); reject anything beyond
        // the encodable range up-front rather than silently truncating.
        if ((uint)_count > ushort.MaxValue)
            throw new InvalidOperationException($"Index node entry count {_count} exceeds u16 header field");
        if ((uint)keySize > ushort.MaxValue)
            throw new InvalidOperationException($"Index node KeySize {keySize} exceeds u16 header field (node > 64 KiB)");

        int prefixLen = commonKeyPrefix.Length;
        if ((uint)prefixLen > byte.MaxValue)
            throw new InvalidOperationException($"Common key prefix length {prefixLen} exceeds u8 header field");

        bool keyLe = ShouldEncodeKeyLittleEndian();
        byte flags = EncodeFlags(_metadata.NodeKind, _metadata.KeyType, EncodeValueSizeCode(valueSize), keyLe);

        if (_metadata.BaseOffset > 0xFFFF_FFFF_FFFFUL)
            throw new InvalidOperationException(
                $"BaseOffset {_metadata.BaseOffset} exceeds 6-byte (48-bit) header field");

        // Fixed 12-byte header:
        //   [Flags u8][KeyCount u16][KeySize u16][CommonPrefixLen u8][BaseOffset 6 bytes LE]
        // BaseOffset sits at the end so the key-parse-critical bytes are grouped first;
        // BaseOffset is only consumed after a successful floor match.
        Span<byte> head = _writer.GetSpan(12);
        head[0] = flags;
        BinaryPrimitives.WriteUInt16LittleEndian(head[1..], (ushort)_count);
        BinaryPrimitives.WriteUInt16LittleEndian(head[3..], (ushort)keySize);
        head[5] = (byte)prefixLen;
        ulong v = _metadata.BaseOffset;
        head[6] = (byte)v;
        head[7] = (byte)(v >> 8);
        head[8] = (byte)(v >> 16);
        head[9] = (byte)(v >> 24);
        head[10] = (byte)(v >> 32);
        head[11] = (byte)(v >> 40);
        _writer.Advance(12);
    }

    /// <summary>
    /// Whether the keys section should be written byte-reversed (Flags bit 5). Honored only
    /// for the slot widths the SIMD/integer-compare reader path supports.
    /// </summary>
    private bool ShouldEncodeKeyLittleEndian()
    {
        // Variable (KeyType=0) is always LE-stored: the prefixArr is unconditionally
        // 2-byte slots and the integer-compare floor-search relies on the byte-reversed
        // encoding regardless of the metadata.IsKeyLittleEndian flag set on the writer.
        if (_metadata.KeyType == 0) return true;
        if (!_metadata.IsKeyLittleEndian) return false;
        // Honored only for the shapes the SIMD direct-compare fast path supports: Uniform with
        // KeySlotSize ∈ {2,4,8}. GetKey returns raw stored bytes (LE-reversed) under this flag;
        // GetFullKey reverses back into a caller dest.
        return _metadata.KeyType == 1 && _metadata.KeySlotSize is 2 or 4 or 8;
    }

    private void WriteUniformKeys()
    {
        int keyLen = _metadata.KeySlotSize;
        bool reverse = ShouldEncodeKeyLittleEndian();
        int keySrc = 0;
        for (int i = 0; i < _count; i++)
        {
            keySrc += 2; // skip u16 length (known from keyLen)
            ReadOnlySpan<byte> src = _keyBuf.Slice(keySrc, keyLen);
            if (reverse)
            {
                Span<byte> slot = _writer.GetSpan(keyLen);
                ReverseInto(src, slot[..keyLen]);
                _writer.Advance(keyLen);
            }
            else
            {
                IByteBufferWriter.Copy(ref _writer, src);
            }
            keySrc += keyLen;
        }
    }

    /// <summary>Copy <paramref name="src"/> reversed into <paramref name="dst"/>. Both must be the same length.</summary>
    private static void ReverseInto(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int n = src.Length;
        for (int i = 0; i < n; i++) dst[i] = src[n - 1 - i];
    }

    private void WriteVariableKeys()
    {
        // SoA layout: [ prefixArr N×u16 LE ][ offsetArr N×u16 LE ][ remainingkeys ].
        //
        // prefixArr[i]: first 2 bytes of key i, byte-reversed (LE-stored). A u16 LE
        // load of the slot yields a value whose unsigned numeric order matches the
        // lex order of the original 2-byte prefix. Keys < 2 bytes pad with 0; the
        // length tag in offsetArr disambiguates from a real 0x00 byte.
        //
        // offsetArr[i]: u16 LE = (lenTag << 14) | tailOffset.
        //   tag 00 = length 0, 01 = length 1, 10 = length 2, 11 = length ≥ 3.
        //   tailOffset is the cumulative byte position into remainingkeys; tags
        //   00/01/10 freeze the cursor (offset == next tag-11 entry's offset).
        //   Tail length for tag 11 = offsetArr[i+1].tailOffset - offsetArr[i].tailOffset
        //   (sentinel for i=N is remainingkeys.Length).

        int prefixArrSize = _count * 2;
        int offsetArrSize = _count * 2;
        Span<byte> prefixArr = _writer.GetSpan(prefixArrSize)[..prefixArrSize];
        // We need to fill prefixArr while walking _keyBuf, but offsetArr depends on the
        // running tail cursor that we also build during the same walk. Compute offsetArr
        // into a temp buffer first, then emit prefix bytes, then offset bytes, then tails.
        Span<ushort> offsets = stackalloc ushort[_count];

        int keySrc = 0;
        int tailCursor = 0;
        for (int i = 0; i < _count; i++)
        {
            int len = BinaryPrimitives.ReadUInt16LittleEndian(_keyBuf[keySrc..]);
            keySrc += 2;
            ReadOnlySpan<byte> key = _keyBuf.Slice(keySrc, len);
            keySrc += len;

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
        _writer.Advance(prefixArrSize);

        // Offset array.
        Span<byte> offsetArr = _writer.GetSpan(offsetArrSize)[..offsetArrSize];
        for (int i = 0; i < _count; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(offsetArr[(i * 2)..], offsets[i]);
        _writer.Advance(offsetArrSize);

        // Tail bytes (only for keys with len > 2; in entry order).
        keySrc = 0;
        for (int i = 0; i < _count; i++)
        {
            int len = BinaryPrimitives.ReadUInt16LittleEndian(_keyBuf[keySrc..]);
            keySrc += 2;
            if (len > 2)
            {
                IByteBufferWriter.Copy(ref _writer, _keyBuf.Slice(keySrc + 2, len - 2));
            }
            keySrc += len;
        }
    }

    private void WriteUniformValues()
    {
        int valLen = _metadata.ValueSlotSize;
        int valSrc = 0;
        for (int i = 0; i < _count; i++)
        {
            valSrc += 2; // skip u16 length
            if (valLen > 0)
            {
                IByteBufferWriter.Copy(ref _writer, _valueBuf.Slice(valSrc, valLen));
            }
            valSrc += valLen;
        }
    }

}
