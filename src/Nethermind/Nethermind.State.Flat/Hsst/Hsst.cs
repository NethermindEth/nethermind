// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Utils;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Hierarchical Static Sorted Table. A compact binary format for persisted snapshots.
///
/// Normal layout: [Version: u8 = 0x01][Data Region][Index Region (B-tree)]
/// Inline layout: [Version: u8 = 0x81][Index Region (B-tree)]
///
/// Root index is readable from the end via MetadataLength byte (no trailer).
///
/// Normal entry format (value first, lengths forward-readable from MetadataStart):
///   [Value][ValueLength: LEB128][RemainingKeyLength: LEB128][RemainingKey]
///
/// Inline: no data section; leaf values stored directly in B-tree index nodes.
/// Separators ARE the full keys.
/// </summary>
public readonly ref struct Hsst
{
    public const int MaxLeafEntries = 64;

    private readonly ReadOnlySpan<byte> _data;

    public ReadOnlySpan<byte> Data => _data;

    public Hsst(ReadOnlySpan<byte> data) => _data = data;

    private bool IsInline => _data.Length >= 1 && (_data[0] & 0x80) != 0;

    public int EntryCount
    {
        get
        {
            if (_data.Length < 2) return 0;
            HsstIndex rootIndex = HsstIndex.ReadFromEnd(_data, _data.Length);
            return CountEntries(rootIndex);
        }
    }

    private int CountEntries(HsstIndex index)
    {
        if (!index.IsIntermediate)
            return index.EntryCount;

        int total = 0;
        for (int i = 0; i < index.EntryCount; i++)
        {
            int childOffset = index.GetIntValue(i);
            HsstIndex child = HsstIndex.ReadFromEnd(_data, childOffset + 1);
            total += CountEntries(child);
        }
        return total;
    }

    public bool TryGet(scoped ReadOnlySpan<byte> key, out ReadOnlySpan<byte> value)
    {
        if (_data.Length < 2)
        {
            value = default;
            return false;
        }

        bool isInline = IsInline;

        HsstIndex currentIndex = HsstIndex.ReadFromEnd(_data, _data.Length);

        // B-tree traversal through intermediate nodes
        while (currentIndex.IsIntermediate)
        {
            if (!currentIndex.TryGetFloor(key, out _, out ReadOnlySpan<byte> childValueBytes))
            {
                value = default;
                return false;
            }
            int childOffset = BinaryPrimitives.ReadInt32LittleEndian(childValueBytes) + currentIndex.Metadata.BaseOffset;
            currentIndex = HsstIndex.ReadFromEnd(_data, childOffset + 1);
        }

        if (isInline)
        {
            // Inline: separator IS the full key, value is the leaf value
            int floorIdx = currentIndex.FindFloorIndex(key);
            if (floorIdx < 0)
            {
                value = default;
                return false;
            }
            if (!key.SequenceEqual(currentIndex.GetKey(floorIdx)))
            {
                value = default;
                return false;
            }
            // Re-derive value span from _data to satisfy ref safety (leafVal references _data memory)
            ReadOnlySpan<byte> leafVal = currentIndex.GetValue(floorIdx);
            value = RederiveFromData(_data, leafVal);
            return true;
        }

        // Non-inline: leaf search
        if (!currentIndex.TryGetFloor(key, out ReadOnlySpan<byte> sepKey, out ReadOnlySpan<byte> metadataBytes))
        {
            value = default;
            return false;
        }

        int metadataStart = BinaryPrimitives.ReadInt32LittleEndian(metadataBytes) + currentIndex.Metadata.BaseOffset;
        ReadEntry(_data, 1 + metadataStart, out ReadOnlySpan<byte> remainingKey, out ReadOnlySpan<byte> entryValue);

        // Verify full key matches: key == separator + remainingKey
        if (key.Length != sepKey.Length + remainingKey.Length)
        {
            value = default;
            return false;
        }

        if (!key.StartsWith(sepKey) ||
            (remainingKey.Length > 0 && !key.Slice(sepKey.Length).SequenceEqual(remainingKey)))
        {
            value = default;
            return false;
        }

        value = entryValue;
        return true;
    }

    /// <summary>
    /// Read a key-value entry given the MetadataStart in the data span.
    /// Entry format: [Value: V bytes][ValueLength: LEB128][RemainingKeyLength: LEB128][RemainingKey: K bytes]
    /// MetadataStart points to the start of the ValueLength LEB128.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReadEntry(ReadOnlySpan<byte> data, int metadataStart, out ReadOnlySpan<byte> remainingKey, out ReadOnlySpan<byte> value)
    {
        int pos = metadataStart;
        int valueLength = Leb128.Read(data, ref pos);
        int keyLength = Leb128.Read(data, ref pos);
        remainingKey = data.Slice(pos, keyLength);
        value = data.Slice(metadataStart - valueLength, valueLength);
    }

    /// <summary>
    /// Re-derive a sub-span from _data to satisfy compiler ref safety rules.
    /// The sub-span must already reference memory within data.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> RederiveFromData(ReadOnlySpan<byte> data, ReadOnlySpan<byte> subSpan)
    {
        if (subSpan.IsEmpty) return default;
        nint offset = Unsafe.ByteOffset(
            ref Unsafe.AsRef(in MemoryMarshal.GetReference(data)),
            ref Unsafe.AsRef(in MemoryMarshal.GetReference(subSpan)));
        return data.Slice((int)offset, subSpan.Length);
    }

    public Enumerator GetEnumerator() => new(_data);

    public ref struct Enumerator : IDisposable
    {
        private readonly ReadOnlySpan<byte> _data;
        private readonly bool _isInline;
        private readonly (byte[] Key, int MetadataStart, byte[]? InlineValue)[] _leafEntries;
        private int _currentIndex;

        public Enumerator(ReadOnlySpan<byte> data)
        {
            _data = data;
            _currentIndex = -1;
            _isInline = data.Length >= 1 && (data[0] & 0x80) != 0;

            if (data.Length < 2)
            {
                _leafEntries = [];
                return;
            }

            HsstIndex rootIndex = HsstIndex.ReadFromEnd(data, data.Length);
            List<(byte[] Key, int MetadataStart, byte[]? InlineValue)> entries = new();
            CollectLeafEntries(data, rootIndex, entries, _isInline);
            _leafEntries = entries.ToArray();
        }

        private static void CollectLeafEntries(ReadOnlySpan<byte> data, HsstIndex index,
            List<(byte[], int, byte[]?)> entries, bool isInline)
        {
            if (!index.IsIntermediate)
            {
                for (int i = 0; i < index.EntryCount; i++)
                {
                    byte[] key = index.GetKey(i).ToArray();
                    if (isInline)
                    {
                        byte[] value = index.GetValue(i).ToArray();
                        entries.Add((key, 0, value));
                    }
                    else
                    {
                        int metaStart = index.GetIntValue(i);
                        entries.Add((key, metaStart, null));
                    }
                }
            }
            else
            {
                for (int i = 0; i < index.EntryCount; i++)
                {
                    int childOffset = index.GetIntValue(i);
                    HsstIndex child = HsstIndex.ReadFromEnd(data, childOffset + 1);
                    CollectLeafEntries(data, child, entries, isInline);
                }
            }
        }

        public bool MoveNext()
        {
            _currentIndex++;
            return _currentIndex < _leafEntries.Length;
        }

        /// <summary>
        /// The byte offset within the HSST data span where the current entry's ValueLength LEB128 starts.
        /// Used by NodeRef to reference an entry's value without copying it.
        /// </summary>
        public readonly int CurrentMetadataStart => 1 + _leafEntries[_currentIndex].MetadataStart;

        public readonly KeyValueEntry Current
        {
            get
            {
                (byte[] key, int metaStart, byte[]? inlineValue) = _leafEntries[_currentIndex];

                if (inlineValue is not null)
                    return new KeyValueEntry(key, inlineValue);

                ReadEntry(_data, 1 + metaStart, out ReadOnlySpan<byte> remainingKey, out ReadOnlySpan<byte> value);

                byte[] fullKey = new byte[key.Length + remainingKey.Length];
                key.CopyTo(fullKey.AsSpan());
                remainingKey.CopyTo(fullKey.AsSpan(key.Length));

                return new KeyValueEntry(fullKey, value);
            }
        }

        public void Dispose() { }
    }

    /// <summary>
    /// Non-ref-struct cursor-based enumerator for N-way merge.
    /// Stores only int offsets per leaf entry — zero heap byte[] allocations per entry.
    /// Reads keys and values on demand from the span passed to MoveNext/GetCurrentValue.
    /// </summary>
    internal sealed class MergeEnumerator : IDisposable
    {
        // Per-leaf-entry: separator offset+length in data, and metadata/value offset+length
        private readonly (int SepOffset, int SepLength, int MetaOrValOffset, int ValLength)[] _entries;
        private readonly bool _isInline;
        private int _index = -1;

        // Single reusable key buffer
        private readonly byte[] _keyBuffer;
        private int _keyLength;

        public MergeEnumerator(ReadOnlySpan<byte> hsstData, bool isInline, int maxKeyLength = 64)
        {
            _keyBuffer = new byte[maxKeyLength];
            _isInline = isInline;

            if (hsstData.Length < 2)
            {
                _entries = [];
                return;
            }

            HsstIndex rootIndex = HsstIndex.ReadFromEnd(hsstData, hsstData.Length);
            List<(int, int, int, int)> entries = new();
            CollectLeafOffsets(hsstData, rootIndex, entries, _isInline);
            _entries = entries.ToArray();
        }

        private static void CollectLeafOffsets(ReadOnlySpan<byte> data, HsstIndex index,
            List<(int, int, int, int)> entries, bool isInline)
        {
            if (!index.IsIntermediate)
            {
                for (int i = 0; i < index.EntryCount; i++)
                {
                    ReadOnlySpan<byte> sep = index.GetKey(i);
                    int sepOffset = SpanOffset(data, sep);
                    if (isInline)
                    {
                        ReadOnlySpan<byte> val = index.GetValue(i);
                        int valOffset = val.IsEmpty ? 0 : SpanOffset(data, val);
                        entries.Add((sepOffset, sep.Length, valOffset, val.Length));
                    }
                    else
                    {
                        int metaStart = index.GetIntValue(i);
                        entries.Add((sepOffset, sep.Length, metaStart, 0));
                    }
                }
            }
            else
            {
                for (int i = 0; i < index.EntryCount; i++)
                {
                    int childOffset = index.GetIntValue(i);
                    HsstIndex child = HsstIndex.ReadFromEnd(data, childOffset + 1);
                    CollectLeafOffsets(data, child, entries, isInline);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int SpanOffset(ReadOnlySpan<byte> outer, ReadOnlySpan<byte> inner) =>
            (int)Unsafe.ByteOffset(
                ref Unsafe.AsRef(in MemoryMarshal.GetReference(outer)),
                ref Unsafe.AsRef(in MemoryMarshal.GetReference(inner)));

        public int Count => _entries.Length;

        public bool MoveNext(ReadOnlySpan<byte> data)
        {
            if (++_index >= _entries.Length) return false;
            (int sepOff, int sepLen, int metaOrValOff, _) = _entries[_index];
            data.Slice(sepOff, sepLen).CopyTo(_keyBuffer.AsSpan());
            if (_isInline)
            {
                _keyLength = sepLen;
            }
            else
            {
                ReadEntry(data, 1 + metaOrValOff, out ReadOnlySpan<byte> remainingKey, out _);
                remainingKey.CopyTo(_keyBuffer.AsSpan(sepLen));
                _keyLength = sepLen + remainingKey.Length;
            }
            return true;
        }

        public ReadOnlySpan<byte> CurrentKey => _keyBuffer.AsSpan(0, _keyLength);

        public ReadOnlySpan<byte> GetCurrentValue(ReadOnlySpan<byte> data)
        {
            (_, _, int metaOrValOff, int valLen) = _entries[_index];
            if (_isInline) return valLen == 0 ? ReadOnlySpan<byte>.Empty : data.Slice(metaOrValOff, valLen);
            ReadEntry(data, 1 + metaOrValOff, out _, out ReadOnlySpan<byte> value);
            return value;
        }

        public int CurrentMetadataStart => 1 + _entries[_index].MetaOrValOffset;

        public void Dispose() { }
    }

    public readonly ref struct KeyValueEntry(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        public ReadOnlySpan<byte> Key { get; } = key;
        public ReadOnlySpan<byte> Value { get; } = value;

        public void Deconstruct(out ReadOnlySpan<byte> key, out ReadOnlySpan<byte> value)
        {
            key = Key;
            value = Value;
        }
    }
}
