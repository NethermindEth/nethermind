// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Nethermind.Core.Utils;

namespace Nethermind.State.Flat.BSearchIndex;

/// <summary>
/// Reads a B-tree index block. An index block stores sorted key-value pairs with separate
/// sections for values and keys, and metadata at the end for backward reading.
///
/// Layout: [Values section][Keys section][Metadata][MetadataLength: u8]
///
/// Metadata: [Flags][KeyCount: LEB128][KeySize: LEB128][ValueSize: LEB128][BaseOffset: LEB128 optional][CommonPrefixLen: u8 + bytes optional]
/// Flags: bit0=IsIntermediate, bits1-2=KeyType, bits3-4=ValueType, bit5=HasBaseOffset, bit6=HasCommonKeyPrefix
///
/// KeyType/ValueType:
///   0 = Variable: length-prefixed entries followed by a u16 offset table at
///       the end of the section (offsets relative to section start)
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

    private BSearchIndexReader(IndexMetadata metadata, ReadOnlySpan<byte> values, ReadOnlySpan<byte> keys, ReadOnlySpan<byte> commonKeyPrefix)
    {
        _metadata = metadata;
        _values = values;
        _keys = keys;
        _commonKeyPrefix = commonKeyPrefix;
    }

    public int EntryCount => _metadata.KeyCount;
    public bool IsIntermediate => _metadata.IsIntermediate;
    public IndexMetadata Metadata => _metadata;

    /// <summary>
    /// Bytes shared by every stored key. Empty when the node was written without the
    /// common-prefix optimization. Stored keys equal <see cref="CommonKeyPrefix"/> followed
    /// by <see cref="GetKey"/>(i).
    /// </summary>
    public ReadOnlySpan<byte> CommonKeyPrefix => _commonKeyPrefix;

    /// <summary>
    /// Read an index block backward from indexEnd (exclusive end position in data).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BSearchIndexReader ReadFromEnd(ReadOnlySpan<byte> data, int indexEnd)
    {
        if (indexEnd <= 0)
            return default;

        // 1. Read MetadataLength from last byte
        int metadataLen = data[indexEnd - 1];

        // 2. Read metadata section forward
        int metadataStart = indexEnd - 1 - metadataLen;
        IndexMetadata metadata = ReadMetadata(data, metadataStart, out ReadOnlySpan<byte> commonKeyPrefix);

        // 3. Compute section boundaries
        int keysEnd = metadataStart;
        int keysStart = keysEnd - metadata.KeySectionSize;
        int valuesEnd = keysStart;
        int valuesStart = valuesEnd - metadata.ValueSectionSize;

        return new BSearchIndexReader(
            metadata,
            data.Slice(valuesStart, metadata.ValueSectionSize),
            data.Slice(keysStart, metadata.KeySectionSize),
            commonKeyPrefix);
    }

    private static IndexMetadata ReadMetadata(ReadOnlySpan<byte> data, int start, out ReadOnlySpan<byte> commonKeyPrefix)
    {
        int pos = start;
        byte flags = data[pos++];
        int keyCount = Leb128.Read(data, ref pos);
        int keySize = Leb128.Read(data, ref pos);
        int valueSize = Leb128.Read(data, ref pos);
        int baseOffset = 0;
        if ((flags & 0x20) != 0)
            baseOffset = Leb128.Read(data, ref pos);

        commonKeyPrefix = default;
        if ((flags & 0x40) != 0)
        {
            int prefixLen = data[pos++];
            commonKeyPrefix = data.Slice(pos, prefixLen);
        }

        return new IndexMetadata
        {
            Flags = flags,
            KeyCount = keyCount,
            KeySize = keySize,
            ValueSize = valueSize,
            BaseOffset = baseOffset
        };
    }

    /// <summary>
    /// Get the key at the given entry index.
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
    /// Get the integer value at the given entry index with BaseOffset applied.
    /// For Uniform 4-byte values (typical for offsets).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetIntValue(int index)
    {
        ReadOnlySpan<byte> raw = GetValue(index);
        int value = BinaryPrimitives.ReadInt32LittleEndian(raw);
        return value + _metadata.BaseOffset;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> GetVariableEntry(ReadOnlySpan<byte> section, int index, int count)
    {
        // Offset table: count * 2 bytes at end of section; offsets relative to section start
        int tableStart = section.Length - count * 2;
        int relativeOffset = BinaryPrimitives.ReadUInt16LittleEndian(section[(tableStart + index * 2)..]);
        int pos = relativeOffset;
        int len = Leb128.Read(section, ref pos);
        return section.Slice(pos, len);
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

        // Specialise on KeyType once at entry so the per-iteration switch in GetKey
        // is hoisted out of the binary-search loop. The JIT can then constant-fold
        // the slice arithmetic when keySize is known and inline the comparison.
        // q is the search key with CommonKeyPrefix stripped; _keys holds the matching
        // stripped separators, so the lexicographic compare is consistent.
        return _metadata.KeyType switch
        {
            1 => FindFloorIndexUniform(q, _keys, count, _metadata.KeySize),
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
    private static int FindFloorIndexUniform(ReadOnlySpan<byte> key, ReadOnlySpan<byte> keys, int count, int keySize)
    {
        // Small Uniform fan-out: SIMD-batched scan beats binary search by avoiding
        // log-N branch mispredicts and bounds-check setup per iteration.
        if (BSearchIndexReaderSimd.TryFindFloorIndexUniformSimd(key, keys, count, keySize, out int simdResult))
            return simdResult;

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindFloorIndexUniformWithLen(ReadOnlySpan<byte> key, ReadOnlySpan<byte> keys, int count, int slotSize)
    {
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

    /// <summary>
    /// Copy the full key (common prefix + per-entry suffix) for entry <paramref name="index"/>
    /// into <paramref name="dest"/>. Returns the total number of bytes written.
    /// </summary>
    public int GetFullKey(int index, Span<byte> dest)
    {
        ReadOnlySpan<byte> suffix = GetKey(index);
        int total = _commonKeyPrefix.Length + suffix.Length;
        if (dest.Length < total)
            throw new ArgumentException("Destination too small for full key", nameof(dest));
        _commonKeyPrefix.CopyTo(dest);
        suffix.CopyTo(dest[_commonKeyPrefix.Length..]);
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
        /// <summary>ValueType=0: section size. ValueType=1: fixed value length. ValueType=2: slot size.</summary>
        public int ValueSize { get; init; }
        public int BaseOffset { get; init; }

        public bool IsIntermediate => (Flags & 0x01) != 0;
        public int KeyType => (Flags >> 1) & 0x03;
        public int ValueType => (Flags >> 3) & 0x03;
        public bool HasBaseOffset => (Flags & 0x20) != 0;
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
