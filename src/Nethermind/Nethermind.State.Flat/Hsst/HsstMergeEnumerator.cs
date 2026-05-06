// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Collections;
using Nethermind.Core.Utils;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Cursor-based forward enumerator over an HSST scope, optimised for N-way merge.
/// Materialises the offset table for every leaf entry up-front (zero per-entry heap
/// allocations during the merge), then iterates by index. Class-based — not a ref struct —
/// so callers can put many of these into an array and round-robin them in a sort-merge.
///
/// The data span is passed externally to <see cref="MoveNext"/>/<see cref="GetCurrentValue"/>/
/// <see cref="GetCurrentValueBound"/>: the enumerator only stores integer offsets.
/// </summary>
public sealed class HsstMergeEnumerator : IDisposable
{
    // Per-leaf-entry: separator offset+length in data, and metadata/value offset+length.
    // Backed by NativeMemoryList so the per-merge enumerator allocations sit off the managed heap.
    private readonly NativeMemoryList<(int SepOffset, int SepLength, int MetaOrValOffset, int ValLength)> _entries;
    // True when each tuple's slots point directly at (keyOffset, keyLen, valueOffset, valueLen)
    // — no further data-region decoding needed (ByteTagMap, PackedArray).
    // False when the second pair is a metaStart pointer that needs LEB128 decoding to recover
    // the full key and value (BTree, BTreeHashIndex).
    private bool _directEntries;
    private int _index = -1;

    // Single reusable key buffer (NativeMemoryList, disposed in Dispose()).
    private readonly NativeMemoryList<byte> _keyBufferList;
    private int _keyLength;
    private bool _disposed;

    public HsstMergeEnumerator(scoped ReadOnlySpan<byte> hsstData, int maxKeyLength = 64)
    {
        _keyBufferList = new NativeMemoryList<byte>(maxKeyLength, maxKeyLength);

        if (hsstData.Length < 2)
        {
            _entries = new NativeMemoryList<(int, int, int, int)>(0);
            return;
        }

        // Last byte of the HSST is the IndexType byte. For hash-index variants the
        // appended hash table sits between the root and the IndexType byte; skip
        // past it to find where the root ends.
        IndexType tag = (IndexType)hsstData[hsstData.Length - 1];
        if (tag == IndexType.ByteTagMap)
        {
            // ByteTagMap: key (1 byte) lives in the tags section, value at a known absolute offset.
            _directEntries = true;
            _entries = new NativeMemoryList<(int, int, int, int)>(8);
            CollectByteTagMap(hsstData, _entries);
            return;
        }

        if (tag == IndexType.DenseByteIndex)
        {
            // DenseByteIndex is used for the persisted-snapshot outer + per-address
            // containers, which the merge code accesses directly via TryGet rather than
            // via this enumerator. Defensive empty enumeration: never invoked in
            // production paths but avoids crashing the BTree parser if the trailer
            // ever reaches this constructor.
            _entries = new NativeMemoryList<(int, int, int, int)>(0);
            return;
        }

        if (tag == IndexType.PackedArray)
        {
            // PackedArray's data section is a packed [key|value][key|value]... array. Both
            // key and value sit at fixed offsets.
            _directEntries = true;
            SpanByteReader spanReader = new(hsstData);
            if (HsstPackedArrayReader.TryReadLayout<SpanByteReader, NoOpPin>(
                    in spanReader, new Bound(0, hsstData.Length), out HsstPackedArrayReader.Layout layout))
            {
                _entries = new NativeMemoryList<(int, int, int, int)>(Math.Max(layout.EntryCount, 1));
                int dataStart = (int)layout.DataStart;
                int stride = layout.KeySize + layout.ValueSize;
                for (int i = 0; i < layout.EntryCount; i++)
                {
                    int entryStart = dataStart + i * stride;
                    _entries.Add((entryStart, layout.KeySize, entryStart + layout.KeySize, layout.ValueSize));
                }
            }
            else
            {
                _entries = new NativeMemoryList<(int, int, int, int)>(0);
            }
            return;
        }

        int rootEnd = hsstData.Length - 1;
        if (tag == IndexType.BTreeHashIndex)
        {
            // [HashTable: N * 4 bytes][TableSize: u32 LE][IndexType: u8]
            uint tableSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                hsstData[(hsstData.Length - 5)..(hsstData.Length - 1)]);
            rootEnd = hsstData.Length - 5 - (int)tableSize * 4;
        }

        HsstIndex rootIndex = HsstIndex.ReadFromEnd(hsstData, rootEnd);
        _entries = new NativeMemoryList<(int, int, int, int)>(16);
        CollectLeafOffsets(hsstData, rootIndex, _entries);
    }

    private static void CollectLeafOffsets(ReadOnlySpan<byte> data, HsstIndex index,
        NativeMemoryList<(int, int, int, int)> entries)
    {
        if (!index.IsIntermediate)
        {
            for (int i = 0; i < index.EntryCount; i++)
            {
                ReadOnlySpan<byte> sep = index.GetKey(i);
                int sepOffset = SpanOffset(data, sep);
                int metaStart = checked((int)index.GetUInt64Value(i));
                entries.Add((sepOffset, sep.Length, metaStart, 0));
            }
        }
        else
        {
            for (int i = 0; i < index.EntryCount; i++)
            {
                int childOffset = checked((int)index.GetUInt64Value(i));
                HsstIndex child = HsstIndex.ReadFromEnd(data, childOffset + 1);
                CollectLeafOffsets(data, child, entries);
            }
        }
    }

    /// <summary>
    /// Materialise (sepOffset, sepLength=1, valOffset, valLength) tuples for a ByteTagMap
    /// HSST. Each tag byte's offset within the data span becomes the "separator" (it IS
    /// the key); each value's start/length are derived from the trailing Ends array.
    /// </summary>
    private static void CollectByteTagMap(ReadOnlySpan<byte> data,
        NativeMemoryList<(int, int, int, int)> entries)
    {
        // Trailer layout: [Ends: N×u32 LE][Tags: N×u8][Count: u8 = N - 1][IndexType: u8 = 0x08]
        if (data.Length < 2) return;
        int n = data[data.Length - 2] + 1;
        int trailerLen = 2 + n + n * 4;
        if (trailerLen > data.Length) return;
        int tagsStart = data.Length - 2 - n;
        int endsStart = tagsStart - n * 4;

        uint prev = 0;
        for (int i = 0; i < n; i++)
        {
            uint thisEnd = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                data.Slice(endsStart + i * 4, 4));
            int valLen = (int)(thisEnd - prev);
            entries.Add((tagsStart + i, 1, (int)prev, valLen));
            prev = thisEnd;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SpanOffset(ReadOnlySpan<byte> outer, ReadOnlySpan<byte> inner) =>
        (int)Unsafe.ByteOffset(
            ref Unsafe.AsRef(in MemoryMarshal.GetReference(outer)),
            ref Unsafe.AsRef(in MemoryMarshal.GetReference(inner)));

    /// <summary>
    /// Decode an entry's <c>(fullKey, value)</c> at <paramref name="metadataStart"/> within
    /// <paramref name="data"/>. Entry format: <c>[Value][ValueLength: LEB128][KeyLength: LEB128][FullKey]</c>.
    /// metaStart points at the <c>ValueLength</c> LEB128 (value sits before, lengths + key sit
    /// after) — LEB128 has a forward-only terminator so it can't be reliably read backward.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReadEntry(ReadOnlySpan<byte> data, int metadataStart,
        out ReadOnlySpan<byte> fullKey, out ReadOnlySpan<byte> value)
    {
        int pos = metadataStart;
        int valueLength = Leb128.Read(data, ref pos);
        int keyLength = Leb128.Read(data, ref pos);
        fullKey = data.Slice(pos, keyLength);
        value = data.Slice(metadataStart - valueLength, valueLength);
    }

    public int Count => _entries.Count;

    public bool MoveNext(ReadOnlySpan<byte> data)
    {
        if (++_index >= _entries.Count) return false;
        (int sepOff, int sepLen, int metaOrValOff, _) = _entries[_index];
        if (_directEntries)
        {
            // First pair IS the full-key bound; copy directly.
            data.Slice(sepOff, sepLen).CopyTo(_keyBufferList.AsSpan());
            _keyLength = sepLen;
        }
        else
        {
            // metaStart points into a data-region entry that carries the full key.
            ReadEntry(data, metaOrValOff, out ReadOnlySpan<byte> fullKey, out _);
            fullKey.CopyTo(_keyBufferList.AsSpan());
            _keyLength = fullKey.Length;
        }
        return true;
    }

    public ReadOnlySpan<byte> CurrentKey => _keyBufferList.AsSpan().Slice(0, _keyLength);

    public ReadOnlySpan<byte> GetCurrentValue(ReadOnlySpan<byte> data)
    {
        (_, _, int metaOrValOff, int valLen) = _entries[_index];
        if (_directEntries) return valLen == 0 ? [] : data.Slice(metaOrValOff, valLen);
        ReadEntry(data, metaOrValOff, out _, out ReadOnlySpan<byte> value);
        return value;
    }

    public (int Offset, int Length) GetCurrentValueBound(ReadOnlySpan<byte> data)
    {
        (_, _, int metaOrValOff, int valLen) = _entries[_index];
        if (_directEntries) return (metaOrValOff, valLen);
        int pos = metaOrValOff;
        int valueLength = Leb128.Read(data, ref pos);
        return (metaOrValOff - valueLength, valueLength);
    }

    public int CurrentMetadataStart => _entries[_index].MetaOrValOffset;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _entries.Dispose();
        _keyBufferList.Dispose();
    }
}
