// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Nethermind.Core.Utils;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Hierarchical Static Sorted Table. A compact binary format for persisted snapshots.
/// Layout: [Version: u8 = 0x01][Data Region][Index Region (B-tree)]
/// Root index is readable from the end via MetadataLength byte (no trailer).
///
/// Entry format (value first, lengths forward-readable from MetadataStart):
///   [Value][ValueLength: LEB128][RemainingKeyLength: LEB128][RemainingKey]
/// </summary>
public readonly ref struct Hsst
{
    public const int MaxLeafEntries = 64;

    private readonly ReadOnlySpan<byte> _data;

    public ReadOnlySpan<byte> Data => _data;

    public Hsst(ReadOnlySpan<byte> data) => _data = data;

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

        // Leaf search
        if (!currentIndex.TryGetFloor(key, out ReadOnlySpan<byte> separatorKey, out ReadOnlySpan<byte> metadataBytes))
        {
            value = default;
            return false;
        }

        int metadataStart = BinaryPrimitives.ReadInt32LittleEndian(metadataBytes) + currentIndex.Metadata.BaseOffset;
        ReadEntry(_data, 1 + metadataStart, out ReadOnlySpan<byte> remainingKey, out ReadOnlySpan<byte> entryValue);

        // Verify full key matches: key == separator + remainingKey
        if (key.Length != separatorKey.Length + remainingKey.Length)
        {
            value = default;
            return false;
        }

        if (!key.StartsWith(separatorKey) ||
            (remainingKey.Length > 0 && !key.Slice(separatorKey.Length).SequenceEqual(remainingKey)))
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

    public Enumerator GetEnumerator() => new(_data);

    public ref struct Enumerator : IDisposable
    {
        private readonly ReadOnlySpan<byte> _data;
        private readonly (byte[] Separator, int MetadataStart)[] _leafEntries;
        private int _currentIndex;

        public Enumerator(ReadOnlySpan<byte> data)
        {
            _data = data;
            _currentIndex = -1;

            if (data.Length < 2)
            {
                _leafEntries = [];
                return;
            }

            HsstIndex rootIndex = HsstIndex.ReadFromEnd(data, data.Length);
            List<(byte[] Separator, int MetadataStart)> entries = new();
            CollectLeafEntries(data, rootIndex, entries);
            _leafEntries = entries.ToArray();
        }

        private static void CollectLeafEntries(ReadOnlySpan<byte> data, HsstIndex index, List<(byte[], int)> entries)
        {
            if (!index.IsIntermediate)
            {
                for (int i = 0; i < index.EntryCount; i++)
                {
                    byte[] sep = index.GetKey(i).ToArray();
                    int metaStart = index.GetIntValue(i);
                    entries.Add((sep, metaStart));
                }
            }
            else
            {
                for (int i = 0; i < index.EntryCount; i++)
                {
                    int childOffset = index.GetIntValue(i);
                    HsstIndex child = HsstIndex.ReadFromEnd(data, childOffset + 1);
                    CollectLeafEntries(data, child, entries);
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
                (byte[] separator, int metaStart) = _leafEntries[_currentIndex];

                ReadEntry(_data, 1 + metaStart, out ReadOnlySpan<byte> remainingKey, out ReadOnlySpan<byte> value);

                byte[] fullKey = new byte[separator.Length + remainingKey.Length];
                separator.CopyTo(fullKey.AsSpan());
                remainingKey.CopyTo(fullKey.AsSpan(separator.Length));

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
