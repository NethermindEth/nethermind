// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Core.Utils;
using Nethermind.State.Flat.BSearchIndex;

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
/// returned by <see cref="CurrentValue"/> are reader-absolute. The current key is
/// only exposed via <see cref="CurrentKeyLength"/> + <see cref="CopyCurrentLogicalKey"/>
/// so callers cannot accidentally consume the on-disk LE-stored layout (see PackedArray
/// LE-stored note on <see cref="HsstPackedArrayBuilder{TWriter}"/>).
///
/// The constructor selects exactly one layout-specific variant based on the trailing
/// <see cref="IndexType"/> byte and stores it in a typed field; the other variant fields
/// remain null. Each public method dispatches via a <c>switch</c> on a discriminator.
///
///   - <see cref="IndexType.PackedArray"/>     → <c>PackedArrayVariant</c> (no offset table; fixed stride).
///   - <see cref="IndexType.BTree"/>           → <c>BTreeVariant</c>       (offset table; leaves only reachable by recursing the index tree).
///
/// <see cref="MoveNext"/> consumes the reader (variants need it for LEB128 / Ends-array
/// reads) and caches the current key/value bounds. Subsequent <see cref="CurrentKeyLength"/>
/// access is a property read; <see cref="GetCurrentValue"/> takes the reader only to
/// materialise a pinned span (no decode). The enumerator stores only integer offsets,
/// never key/value bytes.
/// </summary>
public struct HsstEnumerator<TReader, TPin> : IDisposable
    where TPin : struct, IBufferPin, allows ref struct
    where TReader : IHsstByteReader<TPin>, allows ref struct
{
    private enum VariantKind : byte { Empty, PackedArray, BTree, BTreeKeyFirst, TwoByteSlotValue, TwoByteSlotValueLarge }

    // Struct envelope: only thing that needs to live on the value is the
    // discriminator and the variant references. All mutable
    // iteration state lives on the heap-allocated variant objects, so copies
    // of this struct (e.g. via ArrayPoolList<T>'s by-value indexer) still
    // observe / advance the same underlying cursor.
    private readonly VariantKind _kind;
    private readonly PackedArrayVariant? _packed;
    private readonly BTreeVariant? _btree;
    private readonly TwoByteSlotValueVariant? _tbsv;
    private readonly TwoByteSlotValueLargeVariant? _tbsvLarge;

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
            case IndexType.BTree:
                _btree = new BTreeVariant(in reader, scope, keyFirst: false);
                _kind = VariantKind.BTree;
                break;
            case IndexType.BTreeKeyFirst:
                _btree = new BTreeVariant(in reader, scope, keyFirst: true);
                _kind = VariantKind.BTreeKeyFirst;
                break;
            case IndexType.TwoByteSlotValue:
                _tbsv = TwoByteSlotValueVariant.TryCreate(in reader, scope);
                _kind = _tbsv is not null ? VariantKind.TwoByteSlotValue : VariantKind.Empty;
                break;
            case IndexType.TwoByteSlotValueLarge:
                _tbsvLarge = TwoByteSlotValueLargeVariant.TryCreate(in reader, scope);
                _kind = _tbsvLarge is not null ? VariantKind.TwoByteSlotValueLarge : VariantKind.Empty;
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
        VariantKind.BTree => _btree!.Count,
        VariantKind.BTreeKeyFirst => _btree!.Count,
        VariantKind.TwoByteSlotValue => _tbsv!.Count,
        VariantKind.TwoByteSlotValueLarge => _tbsvLarge!.Count,
        _ => 0,
    };

    public bool MoveNext(scoped in TReader reader) => _kind switch
    {
        VariantKind.PackedArray => _packed!.MoveNext(),
        VariantKind.BTree => _btree!.MoveNext(in reader),
        VariantKind.BTreeKeyFirst => _btree!.MoveNext(in reader),
        VariantKind.TwoByteSlotValue => _tbsv!.MoveNext(in reader),
        VariantKind.TwoByteSlotValueLarge => _tbsvLarge!.MoveNext(in reader),
        _ => false,
    };

    /// <summary>
    /// Reader-absolute bound of the current key. Private: callers must go through
    /// <see cref="CopyCurrentLogicalKey"/> so the LE-stored PackedArray layout
    /// stays an internal concern of this enumerator.
    /// </summary>
    private Bound CurrentKey => _kind switch
    {
        VariantKind.PackedArray => _packed!.CurrentKey,
        VariantKind.BTree => _btree!.CurrentKey,
        VariantKind.BTreeKeyFirst => _btree!.CurrentKey,
        VariantKind.TwoByteSlotValue => _tbsv!.CurrentKey,
        VariantKind.TwoByteSlotValueLarge => _tbsvLarge!.CurrentKey,
        _ => default,
    };

    /// <summary>Length of the current key in bytes. Use to size the <c>dst</c> buffer for <see cref="CopyCurrentLogicalKey"/>.</summary>
    public long CurrentKeyLength => CurrentKey.Length;

    /// <summary>
    /// Copy the current key in its LOGICAL (lex/BE) form into <paramref name="dst"/> and
    /// return that slice. For BTree and BE-stored PackedArray the stored
    /// bytes already match logical form, so this is a straight copy. For LE-stored
    /// PackedArray (auto-enabled at <c>keySize ∈ {2,4,8}</c>) the on-disk bytes are
    /// byte-reversed and this method un-reverses them — callers see the same lex/BE
    /// bytes that were originally <c>Add</c>ed to the builder, regardless of layout.
    /// <paramref name="dst"/> must be at least <see cref="CurrentKeyLength"/> long.
    /// </summary>
    public ReadOnlySpan<byte> CopyCurrentLogicalKey(scoped in TReader reader, Span<byte> dst)
    {
        Bound b = CurrentKey;
        int len = (int)b.Length;
        Span<byte> outSpan = dst[..len];
        using TPin pin = reader.PinBuffer(b.Offset, b.Length);
        ReadOnlySpan<byte> stored = pin.Buffer;
        // LE-stored variants byte-reverse on the way out so callers see the original
        // BE/lex input bytes. PackedArray opts in via IsLittleEndian; the two
        // TwoByteSlotValue formats always store LE.
        bool reverse = (_kind == VariantKind.PackedArray && _packed!.IsLittleEndian)
            || _kind == VariantKind.TwoByteSlotValue
            || _kind == VariantKind.TwoByteSlotValueLarge;
        if (reverse)
        {
            for (int i = 0; i < len; i++) outSpan[i] = stored[len - 1 - i];
        }
        else
        {
            stored.CopyTo(outSpan);
        }
        return outSpan;
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
        VariantKind.BTree => _btree!.CurrentValue,
        VariantKind.BTreeKeyFirst => _btree!.CurrentValue,
        VariantKind.TwoByteSlotValue => _tbsv!.CurrentValue,
        VariantKind.TwoByteSlotValueLarge => _tbsvLarge!.CurrentValue,
        _ => default,
    };

    public long CurrentMetadataStart => _kind switch
    {
        VariantKind.PackedArray => _packed!.CurrentMetadataStart,
        VariantKind.BTree => _btree!.CurrentMetadataStart,
        VariantKind.BTreeKeyFirst => _btree!.CurrentMetadataStart,
        VariantKind.TwoByteSlotValue => _tbsv!.CurrentMetadataStart,
        VariantKind.TwoByteSlotValueLarge => _tbsvLarge!.CurrentMetadataStart,
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
        private readonly bool _isLittleEndian;
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
            _isLittleEndian = layout.IsLittleEndian;
        }

        public long Count => _count;
        public bool IsLittleEndian => _isLittleEndian;

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
        // Fixed key length read from the BTree trailer. Every entry in the HSST has a
        // key of exactly this many bytes — the data-section entry no longer repeats it.
        private readonly int _keyLength;
        // True for IndexType.BTreeKeyFirst: per-entry layout is [FullKey][LEB128][Value]
        // with the index pointer at FullKey byte 0. False for IndexType.BTree:
        // [Value][LEB128][FullKey] with the pointer at the LEB128 byte.
        private readonly bool _keyFirst;
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

        // Root prefix bytes parsed from the HSST trailer at construction. Seeded as
        // parentSeparator when DescendToLeaf loads the root; non-root descents pass
        // `default` and rely on the value-only fast path in the reader (the enumerator
        // never touches prefix-dependent BSearchIndex APIs — only GetUInt64Value /
        // EntryCount / IsIntermediate / BaseOffset).
        private readonly byte[] _rootPrefix;
        private readonly long _trailerLen;

        public BTreeVariant(scoped in TReader reader, Bound scope, bool keyFirst)
        {
            _scopeStart = scope.Offset;
            _scopeEnd = scope.Offset + scope.Length;
            _keyFirst = keyFirst;
            _rootPrefix = [];
            // BTree trailer: [RootPrefix bytes][RootPrefixLen u8][RootSize u16 LE][KeyLength u8][IndexType u8].
            // Root starts at scopeEnd - 5 - rootPrefixLen - rootSize.
            // Smallest valid HSST: trailer (5 bytes) + root header (12 bytes).
            if (scope.Length >= 5 + 12)
            {
                Span<byte> tailBuf = stackalloc byte[5];
                if (reader.TryRead(_scopeEnd - 5, tailBuf))
                {
                    int rootPrefixLen = tailBuf[0];
                    int rootSize = tailBuf[1] | (tailBuf[2] << 8);
                    _keyLength = tailBuf[3];
                    _trailerLen = 5L + rootPrefixLen;
                    _rootAbsStart = _scopeEnd - _trailerLen - rootSize;
                    if (rootPrefixLen > 0)
                    {
                        _rootPrefix = new byte[rootPrefixLen];
                        if (!reader.TryRead(_scopeEnd - 5 - rootPrefixLen, _rootPrefix))
                        {
                            _rootAbsStart = -1;
                        }
                    }
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
        /// returns false if a node fails to load or the tree exceeds MaxDepth. The root
        /// node gets its prefix bytes from <see cref="_rootPrefix"/>; deeper nodes are
        /// loaded with an empty parentSeparator since the enumerator only consumes value
        /// slots (the reader tolerates an absent prefix for value-only callers).
        /// </summary>
        private bool DescendToLeaf(scoped in TReader reader, long absStart, int depthHint)
        {
            long currentStart = absStart;
            int depth = depthHint;
            long scopeEndMinusTrailer = _scopeEnd - _trailerLen;
            Span<byte> flagBuf = stackalloc byte[1];
            while (depth < MaxDepth)
            {
                // Peek the flag byte to detect Entry-kind children (an entry record sitting
                // directly under an intermediate, via the direct-flush path in the builder).
                // Entries have no header, so we can't pass them to TryLoadNode — treat the
                // record as a single-entry virtual leaf at this depth.
                if (!reader.TryRead(currentStart, flagBuf)) return false;
                if ((BSearchNodeKind)(flagBuf[0] & 0x03) == BSearchNodeKind.Entry)
                {
                    _depth = depth;
                    if (_leafMetaStarts.Length < 1)
                        _leafMetaStarts = new long[16];
                    _leafMetaStarts[0] = currentStart;
                    _leafCount = 1;
                    _leafIdx = 0;
                    return true;
                }

                ReadOnlySpan<byte> parentSeparator = depth == 0 ? _rootPrefix : default;
                if (!HsstBTreeReader.TryLoadNode<TReader, TPin>(in reader, currentStart, scopeEndMinusTrailer, parentSeparator, out HsstIndex node, out TPin pin))
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
                    // With phantom slot 0 restored the keys/values array carries one
                    // entry per child (EntryCount == N); slot 0's value is the leftmost
                    // child's relative offset (= 0 since BaseOffset names children[0]).
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
            long scopeEndMinusTrailer = _scopeEnd - _trailerLen;
            while (_depth > 0)
            {
                _depth--;
                ref Ancestor anc = ref _ancestors[_depth];
                anc.LastIdx++;

                ReadOnlySpan<byte> parentSeparator = _depth == 0 ? _rootPrefix : default;
                if (!HsstBTreeReader.TryLoadNode<TReader, TPin>(in reader, anc.AbsStart, scopeEndMinusTrailer, parentSeparator, out HsstIndex parent, out TPin parentPin))
                {
                    _depth = -2;
                    return false;
                }
                long childAbsStart;
                using (parentPin)
                {
                    // LastIdx is the semantic child index (0..N-1). With phantom slot 0
                    // restored each child has its own slot, so EntryCount == N and the
                    // exhaustion check is LastIdx >= EntryCount. Value[LastIdx] gives
                    // the relative offset for children[LastIdx].
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
        /// Read entry _leafIdx's index pointer from the buffered leaf table, then pin a
        /// small window to decode the value length. Sets _currentKeyOffset/Length and
        /// _currentValueOffset/Length to absolute reader-space bounds.
        ///
        /// In both layouts the pointer aims at the entry's leading flag byte; the
        /// LEB128 (key-after-value) or FullKey (key-first) starts at <c>entryPos + 1</c>.
        /// Key-after-value mode (<c>_keyFirst = false</c>): MetadataStart = FlagByte,
        /// LEB128 at +1, value sits just before (<c>entryPos − valueLength</c>), key after.
        /// Key-first mode (<c>_keyFirst = true</c>): EntryStart = FlagByte, key at +1,
        /// LEB128 follows the key, value follows the LEB128.
        /// </summary>
        private bool LoadCurrentEntry(scoped in TReader reader)
        {
            long entryPos = _leafMetaStarts[_leafIdx];

            // Long LEB128 occupies up to 10 bytes; the key length comes from the trailer.
            const int ValueLenMaxBytes = 10;

            if (_keyFirst)
            {
                long keyStart = entryPos + 1;
                long lebStart = keyStart + _keyLength;
                int lebWindow = (int)Math.Min(ValueLenMaxBytes, _scopeEnd - lebStart);
                int pos;
                long valueLength;
                using (TPin lebPin = reader.PinBuffer(lebStart, lebWindow))
                {
                    ReadOnlySpan<byte> leb = lebPin.Buffer;
                    pos = 0;
                    valueLength = Leb128.Read(leb, ref pos);
                }

                _currentMetaStart = entryPos;
                _currentKeyOffset = keyStart;
                _currentKeyLength = _keyLength;
                _currentValueOffset = lebStart + pos;
                _currentValueLength = valueLength;
                return true;
            }
            else
            {
                long lebStart = entryPos + 1;
                int lebWindow = (int)Math.Min(ValueLenMaxBytes, _scopeEnd - lebStart);
                int pos;
                long valueLength;
                using (TPin lebPin = reader.PinBuffer(lebStart, lebWindow))
                {
                    ReadOnlySpan<byte> leb = lebPin.Buffer;
                    pos = 0;
                    valueLength = Leb128.Read(leb, ref pos);
                }

                _currentMetaStart = entryPos;
                _currentKeyOffset = lebStart + pos;
                _currentKeyLength = _keyLength;
                _currentValueOffset = entryPos - valueLength;
                _currentValueLength = valueLength;
                return true;
            }
        }
    }

    // -----------------------------------------------------------------------
    // TwoByteSlotValue: fixed 2-byte keys, variable values, keys-first wire
    // shape with the offsets section between keys and values. Forward iteration
    // is a flat index walk; bounds derived from a single u16 offset read per
    // entry (or zero / values-end for the endpoints).
    // -----------------------------------------------------------------------

    private sealed class TwoByteSlotValueVariant
    {
        private readonly HsstTwoByteSlotValueReader.Layout _layout;
        private int _index = -1;
        private long _currentValueStart;
        private long _currentValueEnd;

        public static TwoByteSlotValueVariant? TryCreate(scoped in TReader reader, Bound scope)
        {
            if (!HsstTwoByteSlotValueReader.TryReadLayout<TReader, TPin>(in reader, scope, out HsstTwoByteSlotValueReader.Layout layout))
                return null;
            return new TwoByteSlotValueVariant(layout);
        }

        private TwoByteSlotValueVariant(HsstTwoByteSlotValueReader.Layout layout) => _layout = layout;

        public long Count => _layout.Count;

        public bool MoveNext(scoped in TReader reader)
        {
            int next = _index + 1;
            if (next >= _layout.Count) return false;
            _index = next;
            // Start of this entry: 0 if first, else Offset_{index} stored at offsetsStart + 2*(index-1).
            long start = _index == 0 ? 0L : ReadU16LE(in reader, _layout.OffsetsStart + (long)(_index - 1) * 2);
            // End of this entry: values-section end if last, else Offset_{index+1} stored at offsetsStart + 2*index.
            long end = _index == _layout.Count - 1
                ? _layout.ValuesEnd - _layout.ValuesStart
                : ReadU16LE(in reader, _layout.OffsetsStart + (long)_index * 2);
            _currentValueStart = _layout.ValuesStart + start;
            _currentValueEnd = _layout.ValuesStart + end;
            return true;
        }

        public Bound CurrentKey => new(_layout.KeysStart + (long)_index * HsstTwoByteSlotValueReader.KeyLength, HsstTwoByteSlotValueReader.KeyLength);
        public Bound CurrentValue => new(_currentValueStart, _currentValueEnd - _currentValueStart);
        public long CurrentMetadataStart => _currentValueEnd;

        private static long ReadU16LE(scoped in TReader reader, long offset)
        {
            Span<byte> buf = stackalloc byte[2];
            reader.TryRead(offset, buf);
            return BinaryPrimitives.ReadUInt16LittleEndian(buf);
        }
    }

    // -----------------------------------------------------------------------
    // TwoByteSlotValueLarge: wider sibling of TwoByteSlotValue. Same iteration
    // shape but reads u24 (3-byte LE) start offsets instead of u16.
    // -----------------------------------------------------------------------

    private sealed class TwoByteSlotValueLargeVariant
    {
        private readonly HsstTwoByteSlotValueLargeReader.Layout _layout;
        private int _index = -1;
        private long _currentValueStart;
        private long _currentValueEnd;

        public static TwoByteSlotValueLargeVariant? TryCreate(scoped in TReader reader, Bound scope)
        {
            if (!HsstTwoByteSlotValueLargeReader.TryReadLayout<TReader, TPin>(in reader, scope, out HsstTwoByteSlotValueLargeReader.Layout layout))
                return null;
            return new TwoByteSlotValueLargeVariant(layout);
        }

        private TwoByteSlotValueLargeVariant(HsstTwoByteSlotValueLargeReader.Layout layout) => _layout = layout;

        public long Count => _layout.Count;

        public bool MoveNext(scoped in TReader reader)
        {
            int next = _index + 1;
            if (next >= _layout.Count) return false;
            _index = next;
            long start = _index == 0 ? 0L : HsstTwoByteSlotValueLargeReader.ReadU24LE<TReader, TPin>(in reader, _layout.OffsetsStart + (long)(_index - 1) * HsstTwoByteSlotValueLargeReader.OffsetSize);
            long end = _index == _layout.Count - 1
                ? _layout.ValuesEnd - _layout.ValuesStart
                : HsstTwoByteSlotValueLargeReader.ReadU24LE<TReader, TPin>(in reader, _layout.OffsetsStart + (long)_index * HsstTwoByteSlotValueLargeReader.OffsetSize);
            _currentValueStart = _layout.ValuesStart + start;
            _currentValueEnd = _layout.ValuesStart + end;
            return true;
        }

        public Bound CurrentKey => new(_layout.KeysStart + (long)_index * HsstTwoByteSlotValueLargeReader.KeyLength, HsstTwoByteSlotValueLargeReader.KeyLength);
        public Bound CurrentValue => new(_currentValueStart, _currentValueEnd - _currentValueStart);
        public long CurrentMetadataStart => _currentValueEnd;
    }
}

