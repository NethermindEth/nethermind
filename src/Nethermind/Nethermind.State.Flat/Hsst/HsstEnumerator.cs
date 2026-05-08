// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
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
public struct HsstEnumerator<TReader, TPin> : IDisposable
    where TPin : struct, IBufferPin, allows ref struct
    where TReader : IHsstByteReader<TPin>, allows ref struct
{
    private enum VariantKind : byte { Empty, PackedArray, ByteTagMap, BTree }

    // Struct envelope: only thing that needs to live on the value is the
    // discriminator and the three nullable variant references. All mutable
    // iteration state lives on the heap-allocated variant objects, so copies
    // of this struct (e.g. via ArrayPoolList<T>'s by-value indexer) still
    // observe / advance the same underlying cursor.
    private readonly VariantKind _kind;
    private readonly PackedArrayVariant? _packed;
    private readonly ByteTagMapVariant? _byteTag;
    private readonly BTreeVariant? _btree;

    public HsstEnumerator(scoped in TReader reader, Bound scope)
    {
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

    public long Count => _kind switch
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

    public long CurrentMetadataStart => _kind switch
    {
        VariantKind.PackedArray => _packed!.CurrentMetadataStart,
        VariantKind.ByteTagMap => _byteTag!.CurrentMetadataStart,
        VariantKind.BTree => _btree!.CurrentMetadataStart,
        _ => 0,
    };

    // Variants currently hold no resources that need release (BTreeVariant's
    // leaf buffer is plain managed memory). Kept on IDisposable so callers
    // can stay on `using` without rewriting; if a variant later acquires
    // resources, plumb the release through here.
    public void Dispose() { }

    // -----------------------------------------------------------------------
    // PackedArray: fixed key/value stride. No offset table — compute on the fly.
    // -----------------------------------------------------------------------

    private sealed class PackedArrayVariant
    {
        private readonly long _dataStart;
        private readonly int _keySize;
        private readonly int _valueSize;
        private readonly int _stride;
        private readonly long _count;
        private long _index = -1;
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

        public long Count => _count;

        public bool MoveNext()
        {
            if (++_index >= _count) return false;
            _currentEntryStart = _dataStart + _index * _stride;
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
        private const int OffsetSize = 2;

        private readonly long _scopeStart;
        private readonly int _count;
        private readonly long _tagsStart;
        private readonly long _endsStart;
        private int _index = -1;
        private int _prevEnd;
        private long _currentValStart;
        private long _currentValLen;

        public static ByteTagMapVariant? TryCreate(scoped in TReader reader, Bound scope)
        {
            // Trailer layout:
            //   [Ends: N×u16 LE][Tags: N×u8][Count: u8 = N - 1][IndexType: u8]
            if (scope.Length < 2) return null;

            int n;
            using (TPin hdrPin = reader.PinBuffer(scope.Offset + scope.Length - 2, 1))
            {
                n = hdrPin.Buffer[0] + 1;
            }
            long trailerLen = 2L + n + (long)n * OffsetSize;
            if (trailerLen > scope.Length) return null;
            long tagsStart = scope.Offset + scope.Length - 2 - n;
            long endsStart = tagsStart - (long)n * OffsetSize;
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

        public long Count => _count;

        public bool MoveNext(scoped in TReader reader)
        {
            int next = _index + 1;
            if (next >= _count) return false;
            _index = next;

            int thisEnd;
            using (TPin endPin = reader.PinBuffer(_endsStart + (long)next * OffsetSize, OffsetSize))
            {
                thisEnd = BinaryPrimitives.ReadUInt16LittleEndian(endPin.Buffer);
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
    // Streams the walk: keeps an ancestor stack of (AbsStart, LastIdx) frames
    // and the current leaf's metaStart values buffered in a reusable array.
    // Pinning a node isn't free for non-mmap readers, so each leaf is loaded
    // exactly once — every entry's metaStart is copied into _leafMetaStarts
    // up front, then MoveNext only pins the small LEB+key-length window per
    // entry. Memory is O(tree depth) for the ancestor stack plus one leaf's
    // worth of long offsets (typically a few hundred at most).
    // -----------------------------------------------------------------------

    private sealed class BTreeVariant
    {
        private const int MaxDepth = 16;

        private struct Ancestor { public long AbsStart; public int LastIdx; }

        private readonly long _scopeStart;
        private readonly long _scopeEnd;
        private readonly long _rootAbsStart;
        private readonly Ancestor[] _ancestors = new Ancestor[MaxDepth];

        // Current leaf state. _depth: -1 = not started, -2 = exhausted, ≥0 = leaf depth in tree.
        // _leafMetaStarts is sized to fit the current leaf and reused across leaves.
        private int _depth = -1;
        private long[] _leafMetaStarts = [];
        private int _leafCount;
        private int _leafIdx;

        // Current entry — populated by LoadCurrentEntry after positioning at a leaf.
        private long _currentKeyOffset;
        private long _currentKeyLength;
        private long _currentValueOffset;
        private long _currentValueLength;
        private long _currentMetaStart;

        public BTreeVariant(scoped in TReader reader, Bound scope)
        {
            _scopeStart = scope.Offset;
            _scopeEnd = scope.Offset + scope.Length;
            // BTree trailer is [RootSize u16 LE][IndexType u8]; root starts at scopeEnd - 3 - rootSize.
            if (scope.Length >= 3 + 12)
            {
                Span<byte> sizeBuf = stackalloc byte[2];
                if (reader.TryRead(_scopeEnd - 3, sizeBuf))
                {
                    int rootSize = sizeBuf[0] | (sizeBuf[1] << 8);
                    _rootAbsStart = _scopeEnd - 3 - rootSize;
                }
                else
                {
                    _rootAbsStart = -1;
                }
            }
            else
            {
                _rootAbsStart = -1;
            }
        }

        // Streaming variant: total entry count is unknown without a full walk. Not used by
        // any caller today — keep the property for variant-shape parity but return -1.
        public long Count => -1;

        public bool MoveNext(scoped in TReader reader)
        {
            if (_depth == -2) return false;
            if (_depth == -1)
            {
                if (_rootAbsStart < 0)
                {
                    _depth = -2;
                    return false;
                }
                // First call: descend leftmost from root.
                if (!DescendToLeaf(in reader, _rootAbsStart, depthHint: 0))
                {
                    _depth = -2;
                    return false;
                }
                return LoadCurrentEntry(in reader);
            }

            _leafIdx++;
            if (_leafIdx < _leafCount)
            {
                return LoadCurrentEntry(in reader);
            }
            // Leaf exhausted — ascend until we find a sibling subtree.
            return AscendAndDescend(in reader);
        }

        public Bound CurrentKey => new(_currentKeyOffset, _currentKeyLength);
        public Bound CurrentValue => new(_currentValueOffset, _currentValueLength);
        public long CurrentMetadataStart => _currentMetaStart;

        /// <summary>
        /// Descend leftmost from the node starting at <paramref name="absStart"/> down to a leaf,
        /// pushing (AbsStart, LastIdx=0) ancestor frames as we cross intermediate levels. On
        /// success, _depth and the leaf metaStart buffer are populated with _leafIdx=0;
        /// returns false if a node fails to load or the tree exceeds MaxDepth.
        /// </summary>
        private bool DescendToLeaf(scoped in TReader reader, long absStart, int depthHint)
        {
            long currentStart = absStart;
            int depth = depthHint;
            while (depth < MaxDepth)
            {
                if (!HsstBTreeReader.TryLoadNode<TReader, TPin>(in reader, currentStart, _scopeEnd - 3, out HsstIndex node, out TPin pin))
                    return false;

                using (pin)
                {
                    if (!node.IsIntermediate)
                    {
                        _depth = depth;
                        BufferLeaf(node);
                        _leafIdx = 0;
                        if (_leafCount == 0)
                        {
                            // Empty leaf shouldn't normally happen; fall through to ascent.
                            return AscendAndDescend(in reader);
                        }
                        return true;
                    }

                    // Intermediate: push frame for this level, follow leftmost child.
                    ref Ancestor frame = ref _ancestors[depth];
                    frame.AbsStart = currentStart;
                    frame.LastIdx = 0;
                    long childRelStart = (long)node.GetUInt64Value(0);
                    currentStart = _scopeStart + childRelStart;
                }
                depth++;
            }
            return false;
        }

        /// <summary>
        /// Copy each entry's metaStart into the reusable buffer. Called once per leaf
        /// transition while the leaf pin is still live; subsequent in-leaf MoveNext
        /// calls index the array directly with no further node pinning.
        /// </summary>
        private void BufferLeaf(HsstIndex leaf)
        {
            int n = leaf.EntryCount;
            if (_leafMetaStarts.Length < n)
            {
                int cap = Math.Max(16, _leafMetaStarts.Length);
                while (cap < n) cap *= 2;
                _leafMetaStarts = new long[cap];
            }
            for (int i = 0; i < n; i++)
            {
                _leafMetaStarts[i] = _scopeStart + (long)leaf.GetUInt64Value(i);
            }
            _leafCount = n;
        }

        /// <summary>
        /// Pop ancestors looking for a frame with another child to advance into; on success,
        /// descend leftmost from that child and load the first entry. Sets _depth=-2 when
        /// the whole tree is exhausted.
        /// </summary>
        private bool AscendAndDescend(scoped in TReader reader)
        {
            while (_depth > 0)
            {
                _depth--;
                ref Ancestor anc = ref _ancestors[_depth];
                anc.LastIdx++;

                if (!HsstBTreeReader.TryLoadNode<TReader, TPin>(in reader, anc.AbsStart, _scopeEnd - 3, out HsstIndex parent, out TPin parentPin))
                {
                    _depth = -2;
                    return false;
                }
                long childAbsStart;
                using (parentPin)
                {
                    if (anc.LastIdx >= parent.EntryCount) continue;
                    long childRelStart = (long)parent.GetUInt64Value(anc.LastIdx);
                    childAbsStart = _scopeStart + childRelStart;
                }
                if (!DescendToLeaf(in reader, childAbsStart, depthHint: _depth + 1))
                {
                    _depth = -2;
                    return false;
                }
                return LoadCurrentEntry(in reader);
            }
            _depth = -2;
            return false;
        }

        /// <summary>
        /// Read entry _leafIdx's metaStart from the buffered leaf table, then pin a small
        /// window at metaStart to decode value/key lengths. Sets _currentKeyOffset/Length and
        /// _currentValueOffset/Length to absolute reader-space bounds.
        /// </summary>
        private bool LoadCurrentEntry(scoped in TReader reader)
        {
            long metaStart = _leafMetaStarts[_leafIdx];

            // Entry layout: [Value][ValueLength: LEB128][KeyLength: u8][FullKey].
            // metaStart points at the ValueLength LEB128 — value sits before, lengths + key after.
            // Long LEB128 occupies up to 10 bytes; KeyLength is a single u8, so the worst-case
            // length-prefix window is 11 bytes.
            const int LenPrefixMaxBytes = 11;
            int lebWindow = (int)Math.Min(LenPrefixMaxBytes, _scopeEnd - metaStart);
            int pos;
            long valueLength;
            int keyLength;
            using (TPin lebPin = reader.PinBuffer(metaStart, lebWindow))
            {
                ReadOnlySpan<byte> leb = lebPin.Buffer;
                pos = 0;
                valueLength = Leb128.Read(leb, ref pos);
                keyLength = leb[pos++];
            }

            _currentMetaStart = metaStart;
            _currentKeyOffset = metaStart + pos;
            _currentKeyLength = keyLength;
            _currentValueOffset = metaStart - valueLength;
            _currentValueLength = valueLength;
            return true;
        }
    }
}

