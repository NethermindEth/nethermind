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
