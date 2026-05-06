// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Collections;
using Nethermind.Core.Utils;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Cursor-based forward enumerator over an HSST scope, optimised for N-way merge.
/// Class-based — not a ref struct — so callers can put many of these into an array
/// and round-robin them in a sort-merge.
///
/// The constructor selects exactly one layout-specific variant based on the trailing
/// <see cref="IndexType"/> byte and stores it in a typed field; the other variant fields
/// remain null. Each public method dispatches via a <c>switch</c> on a discriminator.
///
///   - <see cref="IndexType.PackedArray"/>     → <see cref="PackedArrayVariant"/> (no offset table; fixed stride).
///   - <see cref="IndexType.ByteTagMap"/>      → <see cref="ByteTagMapVariant"/>  (no offset table; offsets via trailing Ends array).
///   - <see cref="IndexType.BTree"/> /
///     <see cref="IndexType.BTreeHashIndex"/>  → <see cref="BTreeVariant"/>       (offset table; leaves only reachable by recursing the index tree).
///
/// <see cref="MoveNext"/> consumes the data span (variants need it for LEB128 / Ends-array
/// reads) and caches the current key/value bounds. Subsequent <see cref="CurrentKey"/>
/// access is a property read; <see cref="GetCurrentValue"/> / <see cref="GetCurrentValueBound"/>
/// take <c>data</c> only to materialise spans (no decode). The enumerator stores only
/// integer offsets, never key/value bytes.
/// </summary>
public sealed class HsstMergeEnumerator : IDisposable
{
    private enum VariantKind : byte { Empty, PackedArray, ByteTagMap, BTree }

    private readonly VariantKind _kind;
    private readonly PackedArrayVariant? _packed;
    private readonly ByteTagMapVariant? _byteTag;
    private readonly BTreeVariant? _btree;
    private bool _disposed;

    public HsstMergeEnumerator(scoped ReadOnlySpan<byte> hsstData)
    {
        if (hsstData.Length < 2)
        {
            _kind = VariantKind.Empty;
            return;
        }

        // Last byte of the HSST is the IndexType byte. For BTreeHashIndex the
        // appended hash table sits between the root and the IndexType byte; the
        // BTree variant skips past it to find where the root ends.
        IndexType tag = (IndexType)hsstData[hsstData.Length - 1];
        switch (tag)
        {
            case IndexType.PackedArray:
                _packed = PackedArrayVariant.TryCreate(hsstData);
                _kind = _packed is not null ? VariantKind.PackedArray : VariantKind.Empty;
                break;
            case IndexType.ByteTagMap:
                _byteTag = ByteTagMapVariant.TryCreate(hsstData);
                _kind = _byteTag is not null ? VariantKind.ByteTagMap : VariantKind.Empty;
                break;
            case IndexType.BTree:
            case IndexType.BTreeHashIndex:
                _btree = new BTreeVariant(hsstData, tag);
                _kind = VariantKind.BTree;
                break;
            // DenseByteIndex is used for the persisted-snapshot outer + per-address
            // containers, which the merge code accesses directly via TryGet rather
            // than via this enumerator. Defensive empty enumeration: never invoked
            // in production paths but avoids crashing the BTree parser if the
            // trailer ever reaches this constructor.
            default:
                _kind = VariantKind.Empty;
                break;
        }
    }

    public int Count => _kind switch
    {
        VariantKind.PackedArray => _packed!.Count,
        VariantKind.ByteTagMap => _byteTag!.Count,
        VariantKind.BTree => _btree!.Count,
        _ => 0,
    };

    public bool MoveNext(ReadOnlySpan<byte> data) => _kind switch
    {
        VariantKind.PackedArray => _packed!.MoveNext(),
        VariantKind.ByteTagMap => _byteTag!.MoveNext(data),
        VariantKind.BTree => _btree!.MoveNext(data),
        _ => false,
    };

    /// <summary>
    /// Bound (offset + length) of the current key within the data span the caller
    /// passed to <see cref="MoveNext"/>. Slice <c>data</c> with this to materialise
    /// the key bytes for comparison.
    /// </summary>
    public Bound CurrentKey => _kind switch
    {
        VariantKind.PackedArray => _packed!.CurrentKey,
        VariantKind.ByteTagMap => _byteTag!.CurrentKey,
        VariantKind.BTree => _btree!.CurrentKey,
        _ => default,
    };

    /// <summary>Convenience: <c>data.Slice(CurrentKey.Offset, CurrentKey.Length)</c>.</summary>
    public ReadOnlySpan<byte> GetCurrentKey(ReadOnlySpan<byte> data)
    {
        Bound b = CurrentKey;
        return data.Slice((int)b.Offset, b.Length);
    }

    public ReadOnlySpan<byte> GetCurrentValue(ReadOnlySpan<byte> data)
    {
        Bound b = CurrentValue;
        return b.Length == 0 ? [] : data.Slice((int)b.Offset, b.Length);
    }

    public Bound CurrentValue => _kind switch
    {
        VariantKind.PackedArray => _packed!.CurrentValue,
        VariantKind.ByteTagMap => _byteTag!.CurrentValue,
        VariantKind.BTree => _btree!.CurrentValue,
        _ => default,
    };

    public (int Offset, int Length) GetCurrentValueBound(ReadOnlySpan<byte> data)
    {
        Bound b = CurrentValue;
        return ((int)b.Offset, b.Length);
    }

    public int CurrentMetadataStart => _kind switch
    {
        VariantKind.PackedArray => _packed!.CurrentMetadataStart,
        VariantKind.ByteTagMap => _byteTag!.CurrentMetadataStart,
        VariantKind.BTree => _btree!.CurrentMetadataStart,
        _ => 0,
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _btree?.Dispose();
    }

    // -----------------------------------------------------------------------
    // PackedArray: fixed key/value stride. No offset table — compute on the fly.
    // -----------------------------------------------------------------------

    private sealed class PackedArrayVariant
    {
        private readonly int _dataStart;
        private readonly int _keySize;
        private readonly int _valueSize;
        private readonly int _stride;
        private readonly int _count;
        private int _index = -1;
        private int _currentEntryStart;

        public static PackedArrayVariant? TryCreate(scoped ReadOnlySpan<byte> hsstData)
        {
            SpanByteReader spanReader = new(hsstData);
            if (!HsstPackedArrayReader.TryReadLayout<SpanByteReader, NoOpPin>(
                    in spanReader, new Bound(0, hsstData.Length), out HsstPackedArrayReader.Layout layout))
            {
                return null;
            }
            return new PackedArrayVariant(layout);
        }

        private PackedArrayVariant(HsstPackedArrayReader.Layout layout)
        {
            _dataStart = (int)layout.DataStart;
            _keySize = layout.KeySize;
            _valueSize = layout.ValueSize;
            _stride = layout.EntryStride;
            _count = layout.EntryCount;
        }

        public int Count => _count;

        public bool MoveNext()
        {
            if (++_index >= _count) return false;
            _currentEntryStart = _dataStart + _index * _stride;
            return true;
        }

        public Bound CurrentKey => new(_currentEntryStart, _keySize);
        public Bound CurrentValue => new(_currentEntryStart + _keySize, _valueSize);
        public int CurrentMetadataStart => _currentEntryStart + _keySize;
    }

    // -----------------------------------------------------------------------
    // ByteTagMap: 1-byte keys, variable-length values driven by the trailing
    // Ends array. No offset table — derive each entry's offsets in MoveNext.
    // -----------------------------------------------------------------------

    private sealed class ByteTagMapVariant
    {
        private readonly int _count;
        private readonly int _tagsStart;
        private readonly int _endsStart;
        private int _index = -1;
        private int _prevEnd;
        private int _currentValStart;
        private int _currentValLen;

        public static ByteTagMapVariant? TryCreate(scoped ReadOnlySpan<byte> hsstData)
        {
            // Trailer layout: [Ends: N×u32 LE][Tags: N×u8][Count: u8 = N - 1][IndexType: u8]
            if (hsstData.Length < 2) return null;
            int n = hsstData[hsstData.Length - 2] + 1;
            int trailerLen = 2 + n + n * 4;
            if (trailerLen > hsstData.Length) return null;
            int tagsStart = hsstData.Length - 2 - n;
            int endsStart = tagsStart - n * 4;
            return new ByteTagMapVariant(n, tagsStart, endsStart);
        }

        private ByteTagMapVariant(int count, int tagsStart, int endsStart)
        {
            _count = count;
            _tagsStart = tagsStart;
            _endsStart = endsStart;
        }

        public int Count => _count;

        public bool MoveNext(ReadOnlySpan<byte> data)
        {
            int next = _index + 1;
            if (next >= _count) return false;
            _index = next;

            int thisEnd = (int)BinaryPrimitives.ReadUInt32LittleEndian(
                data.Slice(_endsStart + next * 4, 4));
            _currentValStart = _prevEnd;
            _currentValLen = thisEnd - _prevEnd;
            _prevEnd = thisEnd;
            return true;
        }

        public Bound CurrentKey => new(_tagsStart + _index, 1);
        public Bound CurrentValue => new(_currentValStart, _currentValLen);
        public int CurrentMetadataStart => _currentValStart;
    }

    // -----------------------------------------------------------------------
    // BTree / BTreeHashIndex: indirect entries reachable only by recursing
    // the index tree. Materialises an offset table once in the ctor; each
    // MoveNext does a small LEB128 decode to populate the current-key/value bounds.
    // -----------------------------------------------------------------------

    private sealed class BTreeVariant : IDisposable
    {
        // Per-leaf-entry: (separator offset, separator length, metadata pointer).
        // metaStart points at the entry's ValueLength LEB128.
        private readonly NativeMemoryList<(int SepOffset, int SepLength, int MetaStart)> _entries;
        private int _index = -1;
        private int _currentKeyOffset;
        private int _currentKeyLength;
        private int _currentValueOffset;
        private int _currentValueLength;
        private int _currentMetaStart;
        private bool _disposed;

        public BTreeVariant(scoped ReadOnlySpan<byte> hsstData, IndexType tag)
        {
            int rootEnd = hsstData.Length - 1;
            if (tag == IndexType.BTreeHashIndex)
            {
                // [HashTable: N * 4 bytes][TableSize: u32 LE][IndexType: u8]
                uint tableSize = BinaryPrimitives.ReadUInt32LittleEndian(
                    hsstData[(hsstData.Length - 5)..(hsstData.Length - 1)]);
                rootEnd = hsstData.Length - 5 - (int)tableSize * 4;
            }

            HsstIndex rootIndex = HsstIndex.ReadFromEnd(hsstData, rootEnd);
            _entries = new NativeMemoryList<(int, int, int)>(16);
            CollectLeafOffsets(hsstData, rootIndex, _entries);
        }

        public int Count => _entries.Count;

        public bool MoveNext(ReadOnlySpan<byte> data)
        {
            if (++_index >= _entries.Count) return false;
            int metaStart = _entries[_index].MetaStart;
            // Entry layout: [Value][ValueLength: LEB128][KeyLength: LEB128][FullKey].
            // metaStart points at the ValueLength LEB128 — value sits before, lengths + key after.
            // LEB128 has a forward-only terminator so it can't be reliably read backward.
            int pos = metaStart;
            int valueLength = Leb128.Read(data, ref pos);
            int keyLength = Leb128.Read(data, ref pos);
            _currentMetaStart = metaStart;
            _currentKeyOffset = pos;
            _currentKeyLength = keyLength;
            _currentValueOffset = metaStart - valueLength;
            _currentValueLength = valueLength;
            return true;
        }

        public Bound CurrentKey => new(_currentKeyOffset, _currentKeyLength);
        public Bound CurrentValue => new(_currentValueOffset, _currentValueLength);
        public int CurrentMetadataStart => _currentMetaStart;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _entries.Dispose();
        }

        private static void CollectLeafOffsets(ReadOnlySpan<byte> data, HsstIndex index,
            NativeMemoryList<(int, int, int)> entries)
        {
            if (!index.IsIntermediate)
            {
                for (int i = 0; i < index.EntryCount; i++)
                {
                    ReadOnlySpan<byte> sep = index.GetKey(i);
                    int sepOffset = SpanOffset(data, sep);
                    int metaStart = checked((int)index.GetUInt64Value(i));
                    entries.Add((sepOffset, sep.Length, metaStart));
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int SpanOffset(ReadOnlySpan<byte> outer, ReadOnlySpan<byte> inner) =>
            (int)Unsafe.ByteOffset(
                ref Unsafe.AsRef(in MemoryMarshal.GetReference(outer)),
                ref Unsafe.AsRef(in MemoryMarshal.GetReference(inner)));
    }
}
