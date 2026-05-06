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
/// Generic on <typeparamref name="TReader"/> / <typeparamref name="TPin"/> so the
/// enumerator can address scopes anywhere in a long-offset reader (e.g. an mmap
/// view spanning more than 2 GiB) without losing precision. Internal offsets are
/// stored as <see cref="long"/> absolute positions; public <see cref="Bound"/>s
/// returned by <see cref="CurrentKey"/> / <see cref="CurrentValue"/> are
/// reader-absolute.
///
/// The constructor selects exactly one layout-specific variant based on the trailing
/// <see cref="IndexType"/> byte and stores it in a typed field; the other variant fields
/// remain null. Each public method dispatches via a <c>switch</c> on a discriminator.
///
///   - <see cref="IndexType.PackedArray"/>     → <c>PackedArrayVariant</c> (no offset table; fixed stride).
///   - <see cref="IndexType.ByteTagMap"/>      → <c>ByteTagMapVariant</c>  (no offset table; offsets via trailing Ends array).
///   - <see cref="IndexType.BTree"/>           → <c>BTreeVariant</c>       (offset table; leaves only reachable by recursing the index tree).
///
/// <see cref="MoveNext"/> consumes the reader (variants need it for LEB128 / Ends-array
/// reads) and caches the current key/value bounds. Subsequent <see cref="CurrentKey"/>
/// access is a property read; <see cref="GetCurrentValue"/> takes the reader only to
/// materialise a pinned span (no decode). The enumerator stores only integer offsets,
/// never key/value bytes.
/// </summary>
public sealed class HsstMergeEnumerator<TReader, TPin> : IDisposable
    where TPin : struct, IBufferPin, allows ref struct
    where TReader : IHsstByteReader<TPin>, allows ref struct
{
    private enum VariantKind : byte { Empty, PackedArray, ByteTagMap, BTree }

    private readonly Bound _scope;
    private readonly VariantKind _kind;
    private readonly PackedArrayVariant? _packed;
    private readonly ByteTagMapVariant? _byteTag;
    private readonly BTreeVariant? _btree;
    private bool _disposed;

    public HsstMergeEnumerator(scoped in TReader reader, Bound scope)
    {
        _scope = scope;
        if (scope.Length < 2)
        {
            _kind = VariantKind.Empty;
            return;
        }

        // Last byte of the HSST is the IndexType byte.
        IndexType tag;
        using (TPin tagPin = reader.PinBuffer(scope.Offset + scope.Length - 1, 1))
        {
            tag = (IndexType)tagPin.Buffer[0];
        }


        switch (tag)
        {
            case IndexType.PackedArray:
                _packed = PackedArrayVariant.TryCreate(in reader, scope);
                _kind = _packed is not null ? VariantKind.PackedArray : VariantKind.Empty;
                break;
            case IndexType.ByteTagMap:
                _byteTag = ByteTagMapVariant.TryCreate(in reader, scope);
                _kind = _byteTag is not null ? VariantKind.ByteTagMap : VariantKind.Empty;
                break;
            case IndexType.BTree:
                _btree = new BTreeVariant(in reader, scope);
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

    public bool MoveNext(scoped in TReader reader) => _kind switch
    {
        VariantKind.PackedArray => _packed!.MoveNext(),
        VariantKind.ByteTagMap => _byteTag!.MoveNext(in reader),
        VariantKind.BTree => _btree!.MoveNext(in reader),
        _ => false,
    };

    /// <summary>
    /// Reader-absolute bound of the current key. Pin it via the reader to materialise bytes.
    /// </summary>
    public Bound CurrentKey => _kind switch
    {
        VariantKind.PackedArray => _packed!.CurrentKey,
        VariantKind.ByteTagMap => _byteTag!.CurrentKey,
        VariantKind.BTree => _btree!.CurrentKey,
        _ => default,
    };

    /// <summary>Pin the current key bytes via <paramref name="reader"/>.</summary>
    public TPin GetCurrentKey(scoped in TReader reader)
    {
        Bound b = CurrentKey;
        return reader.PinBuffer(b.Offset, b.Length);
    }

    /// <summary>Pin the current value bytes via <paramref name="reader"/>; empty pin when length is 0.</summary>
    public TPin GetCurrentValue(scoped in TReader reader)
    {
        Bound b = CurrentValue;
        return reader.PinBuffer(b.Offset, b.Length);
    }

    public Bound CurrentValue => _kind switch
    {
        VariantKind.PackedArray => _packed!.CurrentValue,
        VariantKind.ByteTagMap => _byteTag!.CurrentValue,
        VariantKind.BTree => _btree!.CurrentValue,
        _ => default,
    };

    public (long Offset, int Length) GetCurrentValueBound()
    {
        Bound b = CurrentValue;
        return (b.Offset, b.Length);
    }

    public long CurrentMetadataStart => _kind switch
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
        private readonly long _dataStart;
        private readonly int _keySize;
        private readonly int _valueSize;
        private readonly int _stride;
        private readonly int _count;
        private int _index = -1;
        private long _currentEntryStart;

        public static PackedArrayVariant? TryCreate(scoped in TReader reader, Bound scope)
        {
            if (!HsstPackedArrayReader.TryReadLayout<TReader, TPin>(in reader, scope, out HsstPackedArrayReader.Layout layout))
            {
                return null;
            }
            return new PackedArrayVariant(layout);
        }

        private PackedArrayVariant(HsstPackedArrayReader.Layout layout)
        {
            _dataStart = layout.DataStart;
            _keySize = layout.KeySize;
            _valueSize = layout.ValueSize;
            _stride = layout.EntryStride;
            _count = layout.EntryCount;
        }

        public int Count => _count;

        public bool MoveNext()
        {
            if (++_index >= _count) return false;
            _currentEntryStart = _dataStart + (long)_index * _stride;
            return true;
        }

        public Bound CurrentKey => new(_currentEntryStart, _keySize);
        public Bound CurrentValue => new(_currentEntryStart + _keySize, _valueSize);
        public long CurrentMetadataStart => _currentEntryStart + _keySize;
    }

    // -----------------------------------------------------------------------
    // ByteTagMap: 1-byte keys, variable-length values driven by the trailing
    // Ends array. No offset table — derive each entry's offsets in MoveNext.
    // -----------------------------------------------------------------------

    private sealed class ByteTagMapVariant
    {
        private readonly long _scopeStart;
        private readonly int _count;
        private readonly long _tagsStart;
        private readonly long _endsStart;
        private int _index = -1;
        private int _prevEnd;
        private long _currentValStart;
        private int _currentValLen;

        public static ByteTagMapVariant? TryCreate(scoped in TReader reader, Bound scope)
        {
            // Trailer layout: [Ends: N×u32 LE][Tags: N×u8][Count: u8 = N - 1][IndexType: u8]
            if (scope.Length < 2) return null;

            // Pin the trailing Count byte to compute N. n ≤ 256, so trailer is ≤ ~1.3 KiB —
            // pin it whole for the construction so we can read the Tags block contiguously.
            int n;
            using (TPin tailByte = reader.PinBuffer(scope.Offset + scope.Length - 2, 1))
            {
                n = tailByte.Buffer[0] + 1;
            }
            int trailerLen = 2 + n + n * 4;
            if (trailerLen > scope.Length) return null;
            long tagsStart = scope.Offset + scope.Length - 2 - n;
            long endsStart = tagsStart - n * 4;
            return new ByteTagMapVariant(scope.Offset, n, tagsStart, endsStart);
        }

        private ByteTagMapVariant(long scopeStart, int count, long tagsStart, long endsStart)
        {
            _scopeStart = scopeStart;
            _count = count;
            _tagsStart = tagsStart;
            _endsStart = endsStart;
            _currentValStart = scopeStart;
        }

        public int Count => _count;

        public bool MoveNext(scoped in TReader reader)
        {
            int next = _index + 1;
            if (next >= _count) return false;
            _index = next;

            int thisEnd;
            using (TPin endPin = reader.PinBuffer(_endsStart + next * 4, 4))
            {
                thisEnd = (int)BinaryPrimitives.ReadUInt32LittleEndian(endPin.Buffer);
            }
            // Ends are scope-relative offsets; convert to absolute.
            _currentValStart = _scopeStart + _prevEnd;
            _currentValLen = thisEnd - _prevEnd;
            _prevEnd = thisEnd;
            return true;
        }

        public Bound CurrentKey => new(_tagsStart + _index, 1);
        public Bound CurrentValue => new(_currentValStart, _currentValLen);
        public long CurrentMetadataStart => _currentValStart;
    }

    // -----------------------------------------------------------------------
    // BTree: indirect entries reachable only by recursing the index tree.
    // Materialises an offset table once in the ctor; each MoveNext does a
    // small LEB128 decode to populate the current-key/value bounds.
    // -----------------------------------------------------------------------

    private sealed class BTreeVariant : IDisposable
    {
        // Per-leaf-entry: (separator absolute offset, separator length, metadata absolute pointer).
        // metaStart points at the entry's ValueLength LEB128.
        private readonly NativeMemoryList<(long SepOffset, int SepLength, long MetaStart)> _entries;
        private readonly long _scopeEnd;
        private int _index = -1;
        private long _currentKeyOffset;
        private int _currentKeyLength;
        private long _currentValueOffset;
        private int _currentValueLength;
        private long _currentMetaStart;
        private bool _disposed;

        public BTreeVariant(scoped in TReader reader, Bound scope)
        {
            _scopeEnd = scope.Offset + scope.Length;
            // The BTree index walk is span-based (HsstIndex / BSearchIndexReader operate on
            // a contiguous span). Pin the entire scope for the duration of construction;
            // afterwards we hold only long offsets, so the pin can be released.
            using TPin scopePin = reader.PinBuffer(scope.Offset, scope.Length);
            ReadOnlySpan<byte> hsstData = scopePin.Buffer;

            int rootEnd = hsstData.Length - 1;
            HsstIndex rootIndex = HsstIndex.ReadFromEnd(hsstData, rootEnd);
            _entries = new NativeMemoryList<(long, int, long)>(16);
            CollectLeafOffsets(hsstData, scope.Offset, rootIndex, _entries);
        }

        public int Count => _entries.Count;

        public bool MoveNext(scoped in TReader reader)
        {
            if (++_index >= _entries.Count) return false;
            // SepOffset/SepLength are the index separator (a prefix of the full key); not
            // surfaced through this enumerator because callers compare/copy the FullKey.
            // Kept on the entry tuple for future sharded lookups.
            long metaStart = _entries[_index].MetaStart;

            // Entry layout: [Value][ValueLength: LEB128][KeyLength: LEB128][FullKey].
            // metaStart points at the ValueLength LEB128 — value sits before, lengths + key after.
            // LEB128 has a forward-only terminator so it can't be reliably read backward.
            // Each LEB128 is at most 5 bytes for an int; pin a 10-byte window covering both
            // length prefixes (the FullKey itself stays addressed by absolute offset).
            const int LebPairMaxBytes = 10;
            int lebWindow = (int)Math.Min(LebPairMaxBytes, _scopeEnd - metaStart);
            int pos;
            int valueLength;
            int keyLength;
            using (TPin lebPin = reader.PinBuffer(metaStart, lebWindow))
            {
                ReadOnlySpan<byte> leb = lebPin.Buffer;
                pos = 0;
                valueLength = Leb128.Read(leb, ref pos);
                keyLength = Leb128.Read(leb, ref pos);
            }

            _currentMetaStart = metaStart;
            _currentKeyOffset = metaStart + pos;
            _currentKeyLength = keyLength;
            _currentValueOffset = metaStart - valueLength;
            _currentValueLength = valueLength;
            return true;
        }

        public Bound CurrentKey => new(_currentKeyOffset, _currentKeyLength);
        public Bound CurrentValue => new(_currentValueOffset, _currentValueLength);
        public long CurrentMetadataStart => _currentMetaStart;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _entries.Dispose();
        }

        private static void CollectLeafOffsets(ReadOnlySpan<byte> data, long scopeStart, HsstIndex index,
            NativeMemoryList<(long, int, long)> entries)
        {
            if (!index.IsIntermediate)
            {
                for (int i = 0; i < index.EntryCount; i++)
                {
                    ReadOnlySpan<byte> sep = index.GetKey(i);
                    int sepRelOffset = SpanOffset(data, sep);
                    long metaStart = scopeStart + (long)index.GetUInt64Value(i);
                    entries.Add((scopeStart + sepRelOffset, sep.Length, metaStart));
                }
            }
            else
            {
                for (int i = 0; i < index.EntryCount; i++)
                {
                    int childOffset = checked((int)index.GetUInt64Value(i));
                    HsstIndex child = HsstIndex.ReadFromEnd(data, childOffset + 1);
                    CollectLeafOffsets(data, scopeStart, child, entries);
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

