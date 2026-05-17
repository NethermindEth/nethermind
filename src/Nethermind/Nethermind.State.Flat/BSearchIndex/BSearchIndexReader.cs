// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Utils;

namespace Nethermind.State.Flat.BSearchIndex;

/// <summary>
/// Reads a B-tree index block. An index block stores sorted key-value pairs with a
/// fixed-width metadata header at the front, followed by the keys and values sections.
///
/// Layout (low → high address):
///   [Flags: u8][KeyCount: u16 LE][KeySize: u16 LE][ValueSize: u8][BaseOffset: 6-byte LE]
///   [CommonPrefixLen: u8]?                        (only if Flags bit6 set)
///   [CommonPrefix bytes]?                         (only if Flags bit6 AND bit7 set — root only)
///   [Keys section][Values section]
///
/// Flags: bit0=IsIntermediate, bits1-2=KeyType, bits3-4=reserved (must be 0),
/// bit5=IsKeyLittleEndian, bit6=HasCommonKeyPrefix, bit7=HasInlineCommonKeyPrefix.
///
/// IsKeyLittleEndian (bit 5) marks that fixed-width key slots are stored byte-reversed so an
/// x86 LE integer load of a slot equals its semantic numeric/lex value. Set for Uniform
/// with KeySize ∈ {2,4,8}, and unconditionally for Variable (KeyType=0) where the prefixArr
/// is uniformly 2 bytes/slot — the SIMD floor scan exploits this to drop its per-lane
/// byte-swap shuffle. Stored slots are LE-reversed under this flag; <see cref="GetFullKey"/>
/// always emits lex/original-order bytes.
///
/// All header fields are fixed-width — no varint decoding on parse. With the 64 KiB
/// node-size cap, every count/size field fits in u16. Header at the front lets the hardware
/// prefetcher pull the keys/values forward into cache while the search code is still parsing
/// the header.
///
/// Values are always Uniform: each entry is a fixed-width <c>ValueSize</c>-byte LE integer
/// (1..8 bytes, with <see cref="IndexMetadata.BaseOffset"/> added on read). There is no
/// Variable-value shape for b-tree index nodes.
///
/// KeyType:
///   0 = Variable: SoA layout — [prefixArr: N×u16 LE][offsetArr: N×u16 LE][remainingkeys].
///       prefixArr[i] holds the first 2 bytes of key i, byte-reversed (LE-stored) so a
///       u16 LE load yields a value with the same unsigned-int order as a lex compare on
///       the original 2-byte prefix. offsetArr[i] = (lenTag &lt;&lt; 14) | tailOffset:
///       tag 00=len 0, 01=len 1, 10=len 2 (no tail), 11=len ≥ 3 (tail at tailOffset in
///       remainingkeys; tail length sentinel-derived from offsetArr[i+1].tailOffset, with
///       the implicit sentinel for i=N being remainingkeys.Length). Tags 00/01/10 freeze
///       the cursor (offset == next tag-11 entry's offset). 14-bit tailOffset caps
///       remainingkeys at 16 KiB per section.
///   1 = Uniform: packed fixed-width entries.
///
/// When HasCommonKeyPrefix is set, every stored key equals (CommonKeyPrefix || stored slot i);
/// the keys section holds suffixes only — use <see cref="GetFullKey"/> to reconstruct lex bytes.
///
/// When HasCommonKeyPrefix is set but HasInlineCommonKeyPrefix is clear, the prefix bytes are
/// supplied by the caller via <see cref="ReadFromStart"/>'s <c>parentSeparator</c> parameter,
/// which the descent loop derives from the parent's matched separator. The builder guarantees
/// that each separator length is at least the child's prefix length, so the first
/// <c>CommonPrefixLen</c> bytes of the parent's full separator are the child's prefix bytes.
/// </summary>
public readonly ref struct BSearchIndexReader
{
    private readonly IndexMetadata _metadata;
    private readonly ReadOnlySpan<byte> _values;
    private readonly ReadOnlySpan<byte> _keys;
    private readonly ReadOnlySpan<byte> _commonKeyPrefix;
    private readonly int _totalSize;

    private BSearchIndexReader(IndexMetadata metadata, ReadOnlySpan<byte> values, ReadOnlySpan<byte> keys, ReadOnlySpan<byte> commonKeyPrefix, int totalSize)
    {
        _metadata = metadata;
        _values = values;
        _keys = keys;
        _commonKeyPrefix = commonKeyPrefix;
        _totalSize = totalSize;
    }

    public int EntryCount => _metadata.KeyCount;
    public bool IsIntermediate => _metadata.IsIntermediate;
    public IndexMetadata Metadata => _metadata;
    /// <summary>Total bytes occupied by this index node, including header.</summary>
    public int TotalSize => _totalSize;

    /// <summary>
    /// Bytes shared by every stored key. Empty when the node was written without the
    /// common-prefix optimization. The full lex-order key for entry i is reconstructed via
    /// <see cref="GetFullKey"/>.
    /// </summary>
    public ReadOnlySpan<byte> CommonKeyPrefix => _commonKeyPrefix;

    /// <summary>
    /// Read an index block forward from <paramref name="nodeStart"/> (inclusive start position).
    /// <paramref name="parentSeparator"/> supplies the common-key-prefix bytes for nodes whose
    /// header carries only the prefix length (every non-root HSST node). Must be the full
    /// lex-order separator bytes the parent used to route into this node — the builder
    /// guarantees <c>parentSeparator.Length &gt;= CommonPrefixLen</c>. Pass <c>default</c> for
    /// the root (its prefix bytes are stored inline; flag bit 7 set).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BSearchIndexReader ReadFromStart(ReadOnlySpan<byte> data, int nodeStart, ReadOnlySpan<byte> parentSeparator = default)
    {
        // 12-byte fixed header minimum.
        if (data.Length - nodeStart < 12)
            return default;

        int pos = nodeStart;
        byte flags = data[pos];
        int keyCount = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 1)..]);
        int keySize = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 3)..]);
        int valueSize = data[pos + 5];
        ReadOnlySpan<byte> bo = data.Slice(pos + 6, 6);
        ulong baseOffset = (ulong)bo[0]
                         | ((ulong)bo[1] << 8)
                         | ((ulong)bo[2] << 16)
                         | ((ulong)bo[3] << 24)
                         | ((ulong)bo[4] << 32)
                         | ((ulong)bo[5] << 40);
        pos += 12;

        ReadOnlySpan<byte> commonKeyPrefix = default;
        if ((flags & 0x40) != 0)
        {
            int prefixLen = data[pos];
            pos += 1;
            if ((flags & 0x80) != 0)
            {
                // Root: prefix bytes inline.
                commonKeyPrefix = data.Slice(pos, prefixLen);
                pos += prefixLen;
            }
            else if (parentSeparator.Length >= prefixLen)
            {
                // Non-root: bytes supplied by caller via parent's separator.
                commonKeyPrefix = parentSeparator[..prefixLen];
            }
            // else: caller supplied no (or insufficient) parent separator. The
            // returned reader is usable for value-only operations (GetUInt64Value,
            // EntryCount, etc.) but the prefix-dependent paths (TryGetFloor,
            // GetFullKey, GetSeparatorBytes) will misbehave. Streaming enumerators
            // that only walk child offsets use this path.
        }

        IndexMetadata metadata = new()
        {
            Flags = flags,
            KeyCount = keyCount,
            KeySize = keySize,
            ValueSize = valueSize,
            BaseOffset = baseOffset
        };

        int keysStart = pos;
        int keySectionSize = metadata.KeySectionSize;
        int valuesStart = keysStart + keySectionSize;
        int valueSectionSize = metadata.ValueSectionSize;
        int totalSize = (valuesStart + valueSectionSize) - nodeStart;

        return new BSearchIndexReader(
            metadata,
            data.Slice(valuesStart, valueSectionSize),
            data.Slice(keysStart, keySectionSize),
            commonKeyPrefix,
            totalSize);
    }

    /// <summary>
    /// Raw stored slot at <paramref name="index"/>, zero-copy. Bytes are in storage order, which
    /// for Variable is the 2-byte prefix slot and for LE-stored Uniform is the byte-reversed
    /// form of the original key. Only meaningful as a comparison token in the stored encoding —
    /// external callers wanting lex-order key bytes use <see cref="GetFullKey"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<byte> GetRawSlot(int index) => _metadata.KeyType switch
    {
        // Variable: SoA layout, prefix slot is byte-reversed (LE-stored). Returning the raw
        // 2-byte slot follows the same convention as LE-stored Uniform — callers that need
        // the full key in lex order use GetFullKey with a destination buffer.
        0 => _keys.Slice(index * 2, 2),
        1 => _keys.Slice(index * _metadata.KeySize, _metadata.KeySize),
        _ => throw new InvalidDataException($"Unknown KeyType: {_metadata.KeyType}")
    };

    /// <summary>
    /// Get the value at the given entry index (raw bytes, no BaseOffset adjustment).
    /// Values are always Uniform: fixed-width <see cref="IndexMetadata.ValueSize"/> bytes per entry.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> GetValue(int index) =>
        _values.Slice(index * _metadata.ValueSize, _metadata.ValueSize);

    /// <summary>
    /// Get the unsigned integer value at the given entry index with BaseOffset applied.
    /// Reads the entry's value slot (1..8 byte LE Uniform width given by
    /// <see cref="IndexMetadata.ValueSize"/>) as a ulong and adds <see cref="IndexMetadata.BaseOffset"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong GetUInt64Value(int index)
    {
        ReadOnlySpan<byte> raw = GetValue(index);
        return ReadUInt64LE(raw) + _metadata.BaseOffset;
    }

    /// <summary>
    /// Read a 1..8 byte little-endian unsigned integer. Higher bytes are zero-extended.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong ReadUInt64LE(ReadOnlySpan<byte> src)
    {
        ulong v = 0;
        int len = src.Length;
        for (int i = 0; i < len; i++)
            v |= (ulong)src[i] << (i * 8);
        return v;
    }

    // ---- Variable KEY (SoA) helpers ----

    /// <summary>
    /// Load entry <paramref name="index"/>'s prefix slot as a u16 (LE). The slot stores the
    /// original 2-byte prefix byte-reversed, so the unsigned value returned has the same
    /// ordering as a lex compare on the original prefix bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort GetVariableKeyPrefixU16(ReadOnlySpan<byte> keys, int index) =>
        Unsafe.ReadUnaligned<ushort>(
            ref Unsafe.Add(ref MemoryMarshal.GetReference(keys), (nint)(index * 2)));

    /// <summary>
    /// Load entry <paramref name="index"/>'s offset slot. High 2 bits = lenTag (0..3),
    /// low 14 bits = tailOffset (relative to remainingkeys section start).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetVariableKeyOffsetSlot(ReadOnlySpan<byte> keys, int count, int index)
    {
        int offsetArrStart = count * 2;
        return BinaryPrimitives.ReadUInt16LittleEndian(keys[(offsetArrStart + index * 2)..]);
    }

    /// <summary>
    /// Resolve the tail bytes for entry <paramref name="index"/>. Tag &lt; 11 returns an
    /// empty span. For tag 11 the tail spans <c>[tailOffset, nextTailOffset)</c> with the
    /// sentinel for the last entry being <c>remainingkeys.Length</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> GetVariableKeyTail(ReadOnlySpan<byte> keys, int count, int index)
    {
        int offsetArrStart = count * 2;
        int tailStart = count * 4;
        int slot = BinaryPrimitives.ReadUInt16LittleEndian(keys[(offsetArrStart + index * 2)..]);
        if ((slot >>> 14) != 0b11) return default;
        int tailOffset = slot & 0x3FFF;
        int tailEnd;
        if (index + 1 < count)
        {
            int nextSlot = BinaryPrimitives.ReadUInt16LittleEndian(keys[(offsetArrStart + (index + 1) * 2)..]);
            tailEnd = nextSlot & 0x3FFF;
        }
        else
        {
            tailEnd = keys.Length - tailStart;
        }
        return keys.Slice(tailStart + tailOffset, tailEnd - tailOffset);
    }

    /// <summary>
    /// Encode the search key into the byte-reversed u16 form used by Variable prefixArr slots.
    /// Zero-pads keys shorter than 2 bytes; the caller still has to apply the lenTag-aware
    /// tie-break on prefix-equal probes (length 0/1/2 ambiguities collapse onto the same u16).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort EncodeVariableSearchPrefix(ReadOnlySpan<byte> q)
    {
        if (q.Length >= 2)
            return BinaryPrimitives.ReverseEndianness(
                Unsafe.ReadUnaligned<ushort>(ref MemoryMarshal.GetReference(q)));
        return q.Length == 1 ? (ushort)(q[0] << 8) : (ushort)0;
    }

    /// <summary>
    /// Compare query <paramref name="q"/> against entry <paramref name="index"/> using the
    /// SoA Variable layout. Returns negative, zero, or positive matching <c>SequenceCompareTo</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CompareVariableEntry(ReadOnlySpan<byte> q, ushort searchPrefix, ReadOnlySpan<byte> keys, int count, int index)
    {
        ushort midPrefix = GetVariableKeyPrefixU16(keys, index);
        if (searchPrefix != midPrefix)
            return searchPrefix > midPrefix ? 1 : -1;

        int slot = GetVariableKeyOffsetSlot(keys, count, index);
        int tag = slot >>> 14;
        if (tag != 0b11)
        {
            // Stored key length = tag (0/1/2). Prefix u16 equality (with zero padding) collapses
            // to a length tie-break: q.Length - storedLen.
            return q.Length - tag;
        }

        // Stored key has tail (length ≥ 3). q < stored if q exhausts within the prefix.
        if (q.Length <= 2) return -1;

        int tailOffset = slot & 0x3FFF;
        int offsetArrStart = count * 2;
        int tailStart = count * 4;
        int tailEnd = index + 1 < count
            ? BinaryPrimitives.ReadUInt16LittleEndian(keys[(offsetArrStart + (index + 1) * 2)..]) & 0x3FFF
            : keys.Length - tailStart;
        ReadOnlySpan<byte> tail = keys.Slice(tailStart + tailOffset, tailEnd - tailOffset);
        return q[2..].SequenceCompareTo(tail);
    }

    /// <summary>
    /// Strip the common key prefix from <paramref name="key"/>. Returns the residual span
    /// to binary-search against suffixes, or signals via <paramref name="shortcutResult"/>
    /// that the answer is determined entirely by the prefix relationship.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryStripCommonPrefix(ReadOnlySpan<byte> key, out ReadOnlySpan<byte> residual, out int shortcutResult)
    {
        if (_commonKeyPrefix.Length == 0)
        {
            residual = key;
            shortcutResult = 0;
            return true;
        }
        if (key.StartsWith(_commonKeyPrefix))
        {
            residual = key[_commonKeyPrefix.Length..];
            shortcutResult = 0;
            return true;
        }
        // key does not start with prefix — relationship to every stored key is fixed.
        residual = default;
        shortcutResult = key.SequenceCompareTo(_commonKeyPrefix) < 0
            ? -1                       // key < prefix ≤ every stored key → no floor
            : _metadata.KeyCount - 1;  // key > prefix && !StartsWith(prefix) → floor = last
        return false;
    }

    /// <summary>
    /// Find the index of the largest entry whose key is &lt;= searchKey.
    /// Returns -1 if key is less than all entries.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int FindFloorIndex(ReadOnlySpan<byte> key)
    {
        if (!TryStripCommonPrefix(key, out ReadOnlySpan<byte> q, out int shortcut))
            return shortcut;

        int count = _metadata.KeyCount;
        if (count == 0) return -1;

        // q is the search key with CommonKeyPrefix stripped; _keys holds the matching
        // stripped separators, so the lexicographic compare is consistent.
        bool keyLe = _metadata.IsKeyLittleEndian;
        int keySize = _metadata.KeySize;
        return _metadata.KeyType switch
        {
            1 => keyLe
                ? keySize switch
                {
                    2 => UniformKeySearch.Uniform2LE(q, _keys, count),
                    3 => UniformKeySearch.Uniform3LE(q, _keys, count),
                    4 => UniformKeySearch.Uniform4LE(q, _keys, count),
                    8 => UniformKeySearch.Uniform8LE(q, _keys, count),
                    _ => throw new InvalidDataException($"Invalid LE keySize: {keySize}")
                }
                : UniformKeySearch.UniformBE(q, _keys, count, keySize),
            0 => FindFloorIndexVariable(q, _keys, count),
            _ => throw new InvalidDataException($"Unknown KeyType: {_metadata.KeyType}")
        };
    }

    /// <summary>
    /// Find the largest entry whose key is &lt;= searchKey (floor lookup).
    /// Returns true and sets floorKey/floorValue if found. <paramref name="floorKey"/> is
    /// the per-entry suffix; the full stored key is <see cref="CommonKeyPrefix"/> followed
    /// by <paramref name="floorKey"/>.
    /// </summary>
    public bool TryGetFloor(ReadOnlySpan<byte> key, out ReadOnlySpan<byte> floorKey, out ReadOnlySpan<byte> floorValue)
    {
        // FindFloorIndex handles both the empty-node early-return and the
        // CommonKeyPrefix strip + KeyType dispatch.
        int result = FindFloorIndex(key);
        if (result < 0)
        {
            floorKey = default;
            floorValue = default;
            return false;
        }

        floorKey = GetRawSlot(result);
        floorValue = GetValue(result);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindFloorIndexVariable(ReadOnlySpan<byte> key, ReadOnlySpan<byte> keys, int count)
    {
        ushort searchPrefix = EncodeVariableSearchPrefix(key);
        int result = -1;
        int lo = 0, hi = count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            int cmp = CompareVariableEntry(key, searchPrefix, keys, count, mid);
            if (cmp >= 0) { result = mid; lo = mid + 1; }
            else { hi = mid - 1; }
        }
        return result;
    }

    /// <summary>
    /// Copy the full key (common prefix + per-entry suffix) for entry <paramref name="index"/>
    /// into <paramref name="dest"/>. Always emits bytes in original (lex) order, byte-swapping
    /// the per-entry suffix when <see cref="IndexMetadata.IsKeyLittleEndian"/> is set.
    /// Returns the total number of bytes written.
    /// </summary>
    public int GetFullKey(int index, Span<byte> dest)
    {
        if (_metadata.KeyType == 0)
        {
            // Variable: prefix slot is byte-reversed; tail (if tag 11) lives in remainingkeys.
            int slot = GetVariableKeyOffsetSlot(_keys, _metadata.KeyCount, index);
            int tag = slot >>> 14;
            ReadOnlySpan<byte> tail = tag == 0b11
                ? GetVariableKeyTail(_keys, _metadata.KeyCount, index)
                : default;
            int suffixLen = tag == 0b11 ? 2 + tail.Length : tag;
            int total = _commonKeyPrefix.Length + suffixLen;
            if (dest.Length < total)
                throw new ArgumentException("Destination too small for full key", nameof(dest));
            _commonKeyPrefix.CopyTo(dest);
            Span<byte> suffixDst = dest.Slice(_commonKeyPrefix.Length, suffixLen);
            // Un-reverse prefix slot bytes [b, a] → lex [a, b] up to suffixLen.
            if (suffixLen >= 1) suffixDst[0] = _keys[index * 2 + 1];
            if (suffixLen >= 2) suffixDst[1] = _keys[index * 2];
            if (tag == 0b11) tail.CopyTo(suffixDst[2..]);
            return total;
        }

        ReadOnlySpan<byte> suffix = GetRawSlot(index);
        int totalLegacy = _commonKeyPrefix.Length + suffix.Length;
        if (dest.Length < totalLegacy)
            throw new ArgumentException("Destination too small for full key", nameof(dest));
        _commonKeyPrefix.CopyTo(dest);
        Span<byte> suffixDstLegacy = dest.Slice(_commonKeyPrefix.Length, suffix.Length);
        if (_metadata.IsKeyLittleEndian)
        {
            // Stored slots for KeyType ∈ {1,2} with LE flag are byte-reversed on disk.
            // Reverse back into dest to recover the original lex/numeric byte order.
            int n = suffix.Length;
            for (int i = 0; i < n; i++) suffixDstLegacy[i] = suffix[n - 1 - i];
        }
        else
        {
            suffix.CopyTo(suffixDstLegacy);
        }
        return totalLegacy;
    }

    /// <summary>
    /// Copy entry <paramref name="index"/>'s full lex-order separator bytes (common prefix +
    /// per-entry suffix) into <paramref name="dest"/>. Returns the number of bytes written.
    /// Equivalent to <see cref="GetFullKey"/> — callers descending into a child node use this
    /// to materialize the bytes that the child's header omits.
    /// </summary>
    public int GetSeparatorBytes(int index, Span<byte> dest) => GetFullKey(index, dest);

    /// <summary>
    /// Enumerate all key-value pairs in order.
    /// </summary>
    public Enumerator GetEnumerator() => new(this);

    public ref struct Enumerator
    {
        private readonly BSearchIndexReader _index;
        private int _current;

        public Enumerator(BSearchIndexReader index)
        {
            _index = index;
            _current = -1;
        }

        public bool MoveNext() => ++_current < _index.EntryCount;

        public readonly IndexEntry Current => new(_index.GetRawSlot(_current), _index.GetValue(_current));
    }

    public readonly ref struct IndexEntry(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        public ReadOnlySpan<byte> Key { get; } = key;
        public ReadOnlySpan<byte> Value { get; } = value;
    }

    /// <summary>
    /// Metadata for a B-tree index block, parsed from the Metadata section.
    /// </summary>
    public readonly struct IndexMetadata
    {
        public byte Flags { get; init; }
        public int KeyCount { get; init; }
        /// <summary>KeyType=0: section size. KeyType=1: fixed key length.</summary>
        public int KeySize { get; init; }
        /// <summary>Fixed value length (1..8 for Uniform offsets). Values are always Uniform.</summary>
        public int ValueSize { get; init; }
        /// <summary>Base offset added to every Uniform value read. 0 when absent. Encoded on disk as 6-byte LE.</summary>
        public ulong BaseOffset { get; init; }

        public bool IsIntermediate => (Flags & 0x01) != 0;
        public int KeyType => (Flags >> 1) & 0x03;
        /// <summary>
        /// True when fixed-width key slots are stored byte-reversed (Flags bit 5). Honored by
        /// readers for Uniform with <see cref="KeySize"/> ∈ {2,4,8}, and unconditionally for
        /// Variable (<see cref="KeyType"/>=0) where the prefixArr slot is uniformly 2 bytes.
        /// See <see cref="BSearchIndexReader"/> docs for details.
        /// </summary>
        public bool IsKeyLittleEndian => (Flags & 0x20) != 0;
        public bool HasCommonKeyPrefix => (Flags & 0x40) != 0;
        /// <summary>
        /// True when the prefix bytes are stored inline in this node's header (root only).
        /// When false (every non-root node), the prefix bytes were supplied by the caller
        /// to <see cref="ReadFromStart"/> via the parent's separator.
        /// </summary>
        public bool HasInlineCommonKeyPrefix => (Flags & 0x80) != 0;

        /// <summary>Total byte size of the Keys section.</summary>
        public int KeySectionSize => KeyType switch
        {
            0 => KeySize,              // Variable: KeySize IS the section size
            1 => KeyCount * KeySize,   // Uniform: count * fixed length
            _ => throw new InvalidDataException()
        };

        /// <summary>Total byte size of the Values section. Always Uniform: count × fixed width.</summary>
        public int ValueSectionSize => KeyCount * ValueSize;
    }
}
