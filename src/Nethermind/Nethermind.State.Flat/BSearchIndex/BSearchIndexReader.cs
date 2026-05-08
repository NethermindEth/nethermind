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
///   [CommonPrefixLen: u8][CommonPrefix bytes]?     (only if Flags bit6 set)
///   [Keys section][Values section]
///
/// Flags: bit0=IsIntermediate, bits1-2=KeyType, bits3-4=ValueType, bit5=IsKeyLittleEndian, bit6=HasCommonKeyPrefix.
///
/// IsKeyLittleEndian (bit 5) marks that fixed-width key slots are stored byte-reversed so an
/// x86 LE integer load of a slot equals its semantic numeric/lex value. Set only for Uniform
/// with KeySize ∈ {2,4,8} — the SIMD floor scan exploits this to drop its per-lane byte-swap
/// shuffle. <see cref="GetKey"/> returns raw stored bytes (LE-reversed under this flag);
/// <see cref="GetFullKey"/> always emits lex/original-order bytes.
///
/// All header fields are fixed-width — no varint decoding on parse. With the 64 KiB
/// node-size cap, every count/size field fits in u16. Header at the front lets the hardware
/// prefetcher pull the keys/values forward into cache while the search code is still parsing
/// the header.
///
/// KeyType/ValueType:
///   0 = Variable: raw entry bytes concatenated, then a sentinel u16 offset
///       table of (count+1) entries at the end of the section. Length(i) =
///       offsets[i+1] - offsets[i] — no per-entry length prefix.
///   1 = Uniform: packed fixed-width entries
///   2 = UniformWithLen: fixed slot size, last byte = actual length
///
/// When HasCommonKeyPrefix is set, every stored key equals (CommonKeyPrefix || GetKey(i));
/// the keys section holds suffixes only.
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
    /// common-prefix optimization. Stored keys equal <see cref="CommonKeyPrefix"/> followed
    /// by <see cref="GetKey"/>(i).
    /// </summary>
    public ReadOnlySpan<byte> CommonKeyPrefix => _commonKeyPrefix;

    /// <summary>
    /// Read an index block forward from <paramref name="nodeStart"/> (inclusive start position).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BSearchIndexReader ReadFromStart(ReadOnlySpan<byte> data, int nodeStart)
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
            commonKeyPrefix = data.Slice(pos, prefixLen);
            pos += prefixLen;
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
    /// Get the key at the given entry index — raw stored bytes, no allocation.
    /// When <see cref="IndexMetadata.IsKeyLittleEndian"/> is set, the returned bytes are the
    /// byte-reversed form of the original key for slot widths 2/4/8 (Uniform) or 4 (UniformWithLen).
    /// Use <see cref="GetFullKey"/> to obtain lex/original-order key bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> GetKey(int index) => _metadata.KeyType switch
    {
        0 => GetVariableEntry(_keys, index, _metadata.KeyCount),
        1 => _keys.Slice(index * _metadata.KeySize, _metadata.KeySize),
        2 => GetUniformWithLenEntry(_keys, index, _metadata.KeySize),
        _ => throw new InvalidDataException($"Unknown KeyType: {_metadata.KeyType}")
    };

    /// <summary>
    /// Get the value at the given entry index (raw bytes, no BaseOffset adjustment).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> GetValue(int index) => _metadata.ValueType switch
    {
        0 => GetVariableEntry(_values, index, _metadata.KeyCount),
        1 => _values.Slice(index * _metadata.ValueSize, _metadata.ValueSize),
        2 => GetUniformWithLenEntry(_values, index, _metadata.ValueSize),
        _ => throw new InvalidDataException($"Unknown ValueType: {_metadata.ValueType}")
    };

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> GetVariableEntry(ReadOnlySpan<byte> section, int index, int count)
    {
        // Sentinel offset table at end of section: (count+1) u16 entries, offsets
        // relative to section start. Length(i) = offsets[i+1] - offsets[i] —
        // load both as a single u32 to halve the per-compare load count.
        int tableStart = section.Length - (count + 1) * 2;
        uint pair = BinaryPrimitives.ReadUInt32LittleEndian(section[(tableStart + index * 2)..]);
        int start = (int)(ushort)pair;
        int end = (int)(ushort)(pair >> 16);
        return section.Slice(start, end - start);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> GetUniformWithLenEntry(ReadOnlySpan<byte> section, int index, int slotSize)
    {
        int slotStart = index * slotSize;
        int actualLen = section[slotStart + slotSize - 1]; // Last byte is actual length
        return section.Slice(slotStart, actualLen);
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
    /// Runtime toggle: when true, FindFloorIndex uses branchless binary search variants
    /// (cmov-style updates on lo/n) instead of the default branchful while-loop. The
    /// benchmark flips this for A/B comparison; default is the branchful path because
    /// the JIT-emitted cmov has not yet been spot-checked across all architectures.
    /// </summary>
    public static bool BranchlessSearch = false;

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
        if (BranchlessSearch)
        {
            return _metadata.KeyType switch
            {
                1 => keyLe
                    ? FindFloorIndexUniformBranchlessLe(q, _keys, count, _metadata.KeySize)
                    : FindFloorIndexUniformBranchless(q, _keys, count, _metadata.KeySize),
                2 => FindFloorIndexUniformWithLenBranchless(q, _keys, count, _metadata.KeySize),
                0 => FindFloorIndexVariableBranchless(q, _keys, count),
                _ => throw new InvalidDataException($"Unknown KeyType: {_metadata.KeyType}")
            };
        }

        return _metadata.KeyType switch
        {
            1 => FindFloorIndexUniform(q, _keys, count, _metadata.KeySize, keyLe),
            2 => FindFloorIndexUniformWithLen(q, _keys, count, _metadata.KeySize),
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

        floorKey = GetKey(result);
        floorValue = GetValue(result);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindFloorIndexUniform(ReadOnlySpan<byte> key, ReadOnlySpan<byte> keys, int count, int keySize, bool isLittleEndian)
    {
        // Small Uniform fan-out: SIMD-batched scan beats binary search by avoiding
        // log-N branch mispredicts and bounds-check setup per iteration.
        if (BSearchIndexReaderSimd.TryFindFloorIndexUniformSimd(key, keys, count, keySize, isLittleEndian, out int simdResult))
            return simdResult;

        // LE-stored fixed-width keys with keySize ∈ {2,4,8}: use direct unsigned integer compare
        // instead of SequenceCompareTo (which would compare the byte-reversed bytes lexically and
        // give the wrong order). The search key arrives in lex order; flip its endianness once
        // so its native LE-load value matches the stored slots' native LE-load values.
        // key.Length may exceed keySize at intermediate-node descents — use the first keySize
        // bytes; an equal prefix with a longer search key correctly yields "search >= stored".
        if (isLittleEndian && key.Length >= keySize && keySize is 2 or 4 or 8)
            return FindFloorIndexUniformLe(key, keys, count, keySize);

        int result = -1;
        int lo = 0, hi = count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            ReadOnlySpan<byte> midKey = keys.Slice(mid * keySize, keySize);
            int cmp = key.SequenceCompareTo(midKey);
            if (cmp >= 0) { result = mid; lo = mid + 1; }
            else { hi = mid - 1; }
        }
        return result;
    }

    /// <summary>
    /// Floor-index binary search for LE-stored fixed-width keys (keySize ∈ {2,4,8}). Stored
    /// slots and the (one-time-byteswapped) search key compare as unsigned native integers.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindFloorIndexUniformLe(ReadOnlySpan<byte> key, ReadOnlySpan<byte> keys, int count, int keySize)
    {
        switch (keySize)
        {
            case 2:
                {
                    ushort search = BinaryPrimitives.ReverseEndianness(
                        Unsafe.ReadUnaligned<ushort>(ref MemoryMarshal.GetReference(key)));
                    int result = -1;
                    int lo = 0, hi = count - 1;
                    while (lo <= hi)
                    {
                        int mid = (lo + hi) >>> 1;
                        ushort midKey = Unsafe.ReadUnaligned<ushort>(
                            ref Unsafe.Add(ref MemoryMarshal.GetReference(keys), (nint)(mid * 2)));
                        if (search >= midKey) { result = mid; lo = mid + 1; }
                        else { hi = mid - 1; }
                    }
                    return result;
                }
            case 4:
                {
                    uint search = BinaryPrimitives.ReverseEndianness(
                        Unsafe.ReadUnaligned<uint>(ref MemoryMarshal.GetReference(key)));
                    int result = -1;
                    int lo = 0, hi = count - 1;
                    while (lo <= hi)
                    {
                        int mid = (lo + hi) >>> 1;
                        uint midKey = Unsafe.ReadUnaligned<uint>(
                            ref Unsafe.Add(ref MemoryMarshal.GetReference(keys), (nint)(mid * 4)));
                        if (search >= midKey) { result = mid; lo = mid + 1; }
                        else { hi = mid - 1; }
                    }
                    return result;
                }
            default: // 8
                {
                    ulong search = BinaryPrimitives.ReverseEndianness(
                        Unsafe.ReadUnaligned<ulong>(ref MemoryMarshal.GetReference(key)));
                    int result = -1;
                    int lo = 0, hi = count - 1;
                    while (lo <= hi)
                    {
                        int mid = (lo + hi) >>> 1;
                        ulong midKey = Unsafe.ReadUnaligned<ulong>(
                            ref Unsafe.Add(ref MemoryMarshal.GetReference(keys), (nint)(mid * 8)));
                        if (search >= midKey) { result = mid; lo = mid + 1; }
                        else { hi = mid - 1; }
                    }
                    return result;
                }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindFloorIndexUniformWithLen(ReadOnlySpan<byte> key, ReadOnlySpan<byte> keys, int count, int slotSize)
    {
        // SIMD fast path for the common slotSize=4 case (3-byte payload + 1-byte length).
        if (BSearchIndexReaderSimd.TryFindFloorIndexUniformWithLenSimd(key, keys, count, slotSize, out int simdResult))
            return simdResult;

        int result = -1;
        int lo = 0, hi = count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            int slotStart = mid * slotSize;
            int actualLen = keys[slotStart + slotSize - 1];
            ReadOnlySpan<byte> midKey = keys.Slice(slotStart, actualLen);
            int cmp = key.SequenceCompareTo(midKey);
            if (cmp >= 0) { result = mid; lo = mid + 1; }
            else { hi = mid - 1; }
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindFloorIndexVariable(ReadOnlySpan<byte> key, ReadOnlySpan<byte> keys, int count)
    {
        int result = -1;
        int lo = 0, hi = count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            ReadOnlySpan<byte> midKey = GetVariableEntry(keys, mid, count);
            int cmp = key.SequenceCompareTo(midKey);
            if (cmp >= 0) { result = mid; lo = mid + 1; }
            else { hi = mid - 1; }
        }
        return result;
    }

    // -------- Branchless variants (cmov-style; loop iterates exactly ceil(log2(count))) --------
    //
    // lower_bound style: find the smallest position `lo` where keys[lo] > searchKey, then
    // floor index = lo - 1. The pair of conditional updates on lo and n compile to
    // `cmov` on x86 / `csel` on ARM (verified empirically; if the JIT regresses, force
    // with a sign-bit mask: `int mask = -(uint)(cmp >> 31) >> 31;` and bitwise-select).

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindFloorIndexUniformBranchless(ReadOnlySpan<byte> key, ReadOnlySpan<byte> keys, int count, int keySize)
    {
        int lo = 0;
        int n = count;
        while (n > 0)
        {
            int half = n >> 1;
            int probe = lo + half;
            ReadOnlySpan<byte> probeKey = keys.Slice(probe * keySize, keySize);
            // probeKey <= key (cmp >= 0) → advance lo past probe
            bool advance = key.SequenceCompareTo(probeKey) >= 0;
            lo = advance ? probe + 1 : lo;
            n = advance ? n - half - 1 : half;
        }
        return lo - 1;
    }

    /// <summary>
    /// LE-stored counterpart of <see cref="FindFloorIndexUniformBranchless"/>: integer-compare
    /// path for keySize ∈ {2,4,8}. Falls back to the lex variant for other slot widths.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindFloorIndexUniformBranchlessLe(ReadOnlySpan<byte> key, ReadOnlySpan<byte> keys, int count, int keySize)
    {
        if (key.Length < keySize || keySize is not (2 or 4 or 8))
            return FindFloorIndexUniformBranchless(key, keys, count, keySize);

        ref byte src = ref MemoryMarshal.GetReference(keys);
        int lo = 0;
        int n = count;
        switch (keySize)
        {
            case 2:
                {
                    ushort search = BinaryPrimitives.ReverseEndianness(
                        Unsafe.ReadUnaligned<ushort>(ref MemoryMarshal.GetReference(key)));
                    while (n > 0)
                    {
                        int half = n >> 1;
                        int probe = lo + half;
                        ushort probeKey = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref src, (nint)(probe * 2)));
                        bool advance = search >= probeKey;
                        lo = advance ? probe + 1 : lo;
                        n = advance ? n - half - 1 : half;
                    }
                    return lo - 1;
                }
            case 4:
                {
                    uint search = BinaryPrimitives.ReverseEndianness(
                        Unsafe.ReadUnaligned<uint>(ref MemoryMarshal.GetReference(key)));
                    while (n > 0)
                    {
                        int half = n >> 1;
                        int probe = lo + half;
                        uint probeKey = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref src, (nint)(probe * 4)));
                        bool advance = search >= probeKey;
                        lo = advance ? probe + 1 : lo;
                        n = advance ? n - half - 1 : half;
                    }
                    return lo - 1;
                }
            default: // 8
                {
                    ulong search = BinaryPrimitives.ReverseEndianness(
                        Unsafe.ReadUnaligned<ulong>(ref MemoryMarshal.GetReference(key)));
                    while (n > 0)
                    {
                        int half = n >> 1;
                        int probe = lo + half;
                        ulong probeKey = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref src, (nint)(probe * 8)));
                        bool advance = search >= probeKey;
                        lo = advance ? probe + 1 : lo;
                        n = advance ? n - half - 1 : half;
                    }
                    return lo - 1;
                }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindFloorIndexUniformWithLenBranchless(ReadOnlySpan<byte> key, ReadOnlySpan<byte> keys, int count, int slotSize)
    {
        int lo = 0;
        int n = count;
        while (n > 0)
        {
            int half = n >> 1;
            int probe = lo + half;
            int slotStart = probe * slotSize;
            int actualLen = keys[slotStart + slotSize - 1];
            ReadOnlySpan<byte> probeKey = keys.Slice(slotStart, actualLen);
            bool advance = key.SequenceCompareTo(probeKey) >= 0;
            lo = advance ? probe + 1 : lo;
            n = advance ? n - half - 1 : half;
        }
        return lo - 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindFloorIndexVariableBranchless(ReadOnlySpan<byte> key, ReadOnlySpan<byte> keys, int count)
    {
        int lo = 0;
        int n = count;
        while (n > 0)
        {
            int half = n >> 1;
            int probe = lo + half;
            ReadOnlySpan<byte> probeKey = GetVariableEntry(keys, probe, count);
            bool advance = key.SequenceCompareTo(probeKey) >= 0;
            lo = advance ? probe + 1 : lo;
            n = advance ? n - half - 1 : half;
        }
        return lo - 1;
    }

    /// <summary>
    /// Copy the full key (common prefix + per-entry suffix) for entry <paramref name="index"/>
    /// into <paramref name="dest"/>. Always emits bytes in original (lex) order, byte-swapping
    /// the per-entry suffix when <see cref="IndexMetadata.IsKeyLittleEndian"/> is set.
    /// Returns the total number of bytes written.
    /// </summary>
    public int GetFullKey(int index, Span<byte> dest)
    {
        ReadOnlySpan<byte> suffix = GetKey(index);
        int total = _commonKeyPrefix.Length + suffix.Length;
        if (dest.Length < total)
            throw new ArgumentException("Destination too small for full key", nameof(dest));
        _commonKeyPrefix.CopyTo(dest);
        Span<byte> suffixDst = dest.Slice(_commonKeyPrefix.Length, suffix.Length);
        if (_metadata.IsKeyLittleEndian)
        {
            // Stored slots for KeyType=1 with KeySize ∈ {2,4,8} are byte-reversed on disk.
            // Reverse back into dest to recover the original lex/numeric byte order.
            int n = suffix.Length;
            for (int i = 0; i < n; i++) suffixDst[i] = suffix[n - 1 - i];
        }
        else
        {
            suffix.CopyTo(suffixDst);
        }
        return total;
    }

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

        public readonly IndexEntry Current => new(_index.GetKey(_current), _index.GetValue(_current));
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
        /// <summary>KeyType=0: section size. KeyType=1: fixed key length. KeyType=2: slot size.</summary>
        public int KeySize { get; init; }
        /// <summary>ValueType=0: section size. ValueType=1: fixed value length (1..8 for offsets). ValueType=2: slot size.</summary>
        public int ValueSize { get; init; }
        /// <summary>Base offset added to every Uniform value read. 0 when absent. Encoded on disk as 6-byte LE.</summary>
        public ulong BaseOffset { get; init; }

        public bool IsIntermediate => (Flags & 0x01) != 0;
        public int KeyType => (Flags >> 1) & 0x03;
        public int ValueType => (Flags >> 3) & 0x03;
        /// <summary>
        /// True when fixed-width key slots are stored byte-reversed (Flags bit 5). Honored by
        /// readers only for Uniform with <see cref="KeySize"/> ∈ {2,4,8} and UniformWithLen with
        /// <see cref="KeySize"/> = 4. See <see cref="BSearchIndexReader"/> docs for details.
        /// </summary>
        public bool IsKeyLittleEndian => (Flags & 0x20) != 0;
        public bool HasCommonKeyPrefix => (Flags & 0x40) != 0;

        /// <summary>Total byte size of the Keys section.</summary>
        public int KeySectionSize => KeyType switch
        {
            0 => KeySize,              // Variable: KeySize IS the section size
            1 => KeyCount * KeySize,   // Uniform: count * fixed length
            2 => KeyCount * KeySize,   // UniformWithLen: count * slot size
            _ => throw new InvalidDataException()
        };

        /// <summary>Total byte size of the Values section.</summary>
        public int ValueSectionSize => ValueType switch
        {
            0 => ValueSize,              // Variable: ValueSize IS the section size
            1 => KeyCount * ValueSize,   // Uniform: count * fixed length
            2 => KeyCount * ValueSize,   // UniformWithLen: count * slot size
            _ => throw new InvalidDataException()
        };
    }
}
