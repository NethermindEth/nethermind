// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Nethermind.Core.Utils;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Forward-only B-tree walker over an HSST scope. Yields entries in sorted key order.
/// Generic over the same <typeparamref name="TReader"/>/<typeparamref name="TPin"/> as
/// <see cref="HsstReader{TReader,TPin}"/>; constructed from a <see cref="Bound"/> that
/// scopes which HSST is being enumerated. The enumerator owns one pin (the current leaf
/// node) at a time; ancestors are re-loaded via the reader when ascending, so peak memory
/// is one pinned node plus a small ancestor-end stack.
///
/// Both <c>Current.KeyBound</c> and <c>Current.ValueBound</c> are absolute reader offsets;
/// callers slice them out of their own data span (or pin them via the reader). The
/// enumerator never materialises the key into an internal buffer — the data-region entry
/// already carries the full key and the bound points straight at it.
/// </summary>
public ref struct HsstEnumerator<TReader, TPin> : IDisposable
    where TPin : struct, IBufferPin, allows ref struct
    where TReader : IHsstByteReader<TPin>, allows ref struct
{
    /// <summary>Maximum supported B-tree depth. Realistic trees stay ≤4; 16 is a hard ceiling.</summary>
    private const int MaxDepth = 16;

    [InlineArray(MaxDepth)]
    private struct AncestorStack { private Ancestor _e0; }

    private struct Ancestor
    {
        public long AbsEnd;
        public int LastIdx;
    }

    private TReader _reader;
    private readonly long _hsstStart;
    private readonly long _hsstEnd;
    private readonly long _rootAbsEnd;
    private readonly bool _empty;

    // PackedArray state: a packed entry array, no b-tree walk. _flatIdx is the next entry to
    // yield; -1 means not yet started; >= _flatEntryCount means exhausted.
    private readonly bool _isFlat;
    private readonly int _flatKeySize;
    private readonly int _flatValueSize;
    private readonly int _flatEntryCount;
    private readonly long _flatDataStart;
    private int _flatIdx;

    // VarPackedArray state: fixed-stride key+offset table over a packed values section.
    // _varIdx is the next entry to yield; -1 = not yet started; >= _varEntryCount = exhausted.
    private readonly bool _isVar;
    private readonly int _varKeySize;
    private readonly int _varOffsetSize;
    private readonly int _varEntryCount;
    private readonly long _varKeyOffsetsStart;
    private readonly long _varValuesStart;
    private long _varPrevEnd;
    private int _varIdx;

    // ByteTagMap state: tiny single-byte-keyed map; no b-tree walk. _tagIdx tracks next entry.
    private readonly bool _isTagMap;
    private readonly int _tagMapCount;
    private readonly int _tagMapOffsetSize;
    private readonly long _tagMapDataStart;
    private readonly long _tagMapEndsStart;
    private readonly long _tagMapTagsStart;
    private int _tagIdx;
    private long _tagPrevEnd;

    private AncestorStack _ancestors;
    /// <summary>Depth of the current leaf in the tree (0 = root). −1 = not yet started.</summary>
    private int _depth;

    // Current leaf state
    private TPin _leafPin;
    private HsstIndex _leafNode;
    private long _leafAbsStart;
    private int _leafIdx;

    // Current entry — both bounds are absolute reader offsets (Bound.Offset = reader-space).
    private Bound _currentKeyBound;
    private Bound _currentValueBound;

    public HsstEnumerator(scoped in TReader reader, Bound bound)
    {
        _reader = reader;
        _hsstStart = bound.Offset;
        _hsstEnd = bound.Offset + bound.Length;
        _depth = -1;

        if (bound.Length < 2)
        {
            _empty = true;
            return;
        }

        // IndexType byte is the last byte of the HSST.
        Span<byte> idxType = stackalloc byte[1];
        if (!_reader.TryRead(_hsstEnd - 1, idxType))
        {
            _empty = true;
            return;
        }
        switch ((IndexType)idxType[0])
        {
            case IndexType.BTree:
                _rootAbsEnd = _hsstEnd - 1;
                break;
            case IndexType.PackedArray:
                if (!HsstPackedArrayReader.TryReadLayout<TReader, TPin>(in _reader, bound, out HsstPackedArrayReader.Layout flatLayout))
                {
                    _empty = true;
                    return;
                }
                _isFlat = true;
                _flatKeySize = flatLayout.KeySize;
                _flatValueSize = flatLayout.ValueSize;
                _flatEntryCount = flatLayout.EntryCount;
                _flatDataStart = flatLayout.DataStart;
                _flatIdx = -1;
                if (flatLayout.EntryCount == 0)
                {
                    _empty = true;
                    return;
                }
                break;
            case IndexType.VarPackedArray:
                if (!HsstVarPackedArrayReader.TryReadLayout<TReader, TPin>(in _reader, bound, out HsstVarPackedArrayReader.Layout varLayout))
                {
                    _empty = true;
                    return;
                }
                _isVar = true;
                _varKeySize = varLayout.KeySize;
                _varOffsetSize = varLayout.OffsetSize;
                _varEntryCount = varLayout.EntryCount;
                _varKeyOffsetsStart = varLayout.KeyOffsetsStart;
                _varValuesStart = varLayout.ValuesStart;
                _varPrevEnd = 0;
                _varIdx = -1;
                if (varLayout.EntryCount == 0)
                {
                    _empty = true;
                    return;
                }
                break;
            case IndexType.ByteTagMap:
                if (!HsstByteTagMapReader.TryReadLayout<TReader, TPin>(in _reader, bound, out HsstByteTagMapReader.Layout tagLayout))
                {
                    _empty = true;
                    return;
                }
                _isTagMap = true;
                _tagMapCount = tagLayout.Count;
                _tagMapOffsetSize = tagLayout.OffsetSize;
                _tagMapDataStart = tagLayout.DataStart;
                _tagMapEndsStart = tagLayout.EndsStart;
                _tagMapTagsStart = tagLayout.TagsStart;
                _tagIdx = -1;
                _tagPrevEnd = 0;
                if (tagLayout.Count == 0)
                {
                    _empty = true;
                    return;
                }
                break;
            default:
                _empty = true;
                return;
        }
        _empty = false;
    }

    public bool MoveNext()
    {
        if (_empty) return false;

        if (_isFlat)
        {
            int next = _flatIdx + 1;
            if ((uint)next >= (uint)_flatEntryCount) return false;
            _flatIdx = next;
            int stride = _flatKeySize + _flatValueSize;
            long entryAbsStart = _flatDataStart + (long)next * stride;
            _currentKeyBound = new Bound(entryAbsStart, _flatKeySize);
            _currentValueBound = new Bound(entryAbsStart + _flatKeySize, _flatValueSize);
            return true;
        }

        if (_isVar)
        {
            int next = _varIdx + 1;
            if ((uint)next >= (uint)_varEntryCount) return false;
            int stride = _varKeySize + _varOffsetSize;
            long entryAbsStart = _varKeyOffsetsStart + (long)next * stride;
            Span<byte> endBuf = stackalloc byte[8];
            endBuf.Clear();
            if (!_reader.TryReadWithReadahead(entryAbsStart + _varKeySize, endBuf[.._varOffsetSize])) return false;
            long thisEnd = (long)BinaryPrimitives.ReadUInt64LittleEndian(endBuf);
            long prevEnd = next == 0 ? 0 : _varPrevEnd;
            if (thisEnd < prevEnd) return false;
            _varIdx = next;
            _currentKeyBound = new Bound(entryAbsStart, _varKeySize);
            _currentValueBound = new Bound(_varValuesStart + prevEnd, thisEnd - prevEnd);
            _varPrevEnd = thisEnd;
            return true;
        }

        if (_isTagMap)
        {
            int next = _tagIdx + 1;
            if ((uint)next >= (uint)_tagMapCount) return false;
            Span<byte> endBuf = stackalloc byte[8];
            endBuf.Clear();
            if (!_reader.TryRead(_tagMapEndsStart + (long)next * _tagMapOffsetSize, endBuf[.._tagMapOffsetSize])) return false;
            long thisEnd = (long)BinaryPrimitives.ReadUInt64LittleEndian(endBuf);
            long prev = next == 0 ? 0L : _tagPrevEnd;
            if (thisEnd < prev) return false;
            _tagIdx = next;
            _currentKeyBound = new Bound(_tagMapTagsStart + next, 1);
            _currentValueBound = new Bound(_tagMapDataStart + prev, thisEnd - prev);
            _tagPrevEnd = thisEnd;
            return true;
        }

        if (_depth < 0)
        {
            // Root node ends just before the trailing IndexType byte.
            return DescendToLeaf(_rootAbsEnd);
        }

        _leafIdx++;
        if (_leafIdx < _leafNode.EntryCount)
        {
            UpdateCurrent();
            return true;
        }

        // Leaf exhausted; release pin and ascend.
        _leafPin.Dispose();
        _leafPin = default;
        return AscendAndDescend();
    }

    public readonly KeyValueEntry Current => new(_currentKeyBound, _currentValueBound);

    public void Dispose()
    {
        _leafPin.Dispose();
        _leafPin = default;
    }

    /// <summary>
    /// Descend from the node ending at <paramref name="absEnd"/> down to the leftmost leaf,
    /// pushing ancestor (absEnd, lastIdx=0) frames as we go. On success, the leaf's pin is held
    /// and the first entry is materialised. Returns false on tree-too-deep or load failure.
    /// </summary>
    private bool DescendToLeaf(long absEnd)
    {
        long currentEnd = absEnd;
        int depth = (_depth < 0) ? 0 : _depth;
        while (depth < MaxDepth)
        {
            if (!TryLoadNode(currentEnd, out HsstIndex node, out long nodeAbsStart, out TPin pin))
                return false;

            if (!node.IsIntermediate)
            {
                _leafNode = node;
                _leafAbsStart = nodeAbsStart;
                _leafPin = pin;
                _leafIdx = 0;
                _depth = depth;
                if (_leafNode.EntryCount == 0)
                {
                    _leafPin.Dispose();
                    _leafPin = default;
                    return AscendAndDescend();
                }
                UpdateCurrent();
                return true;
            }

            // Intermediate: read child[0], descend.
            ref Ancestor frame = ref _ancestors[depth];
            frame.AbsEnd = currentEnd;
            frame.LastIdx = 0;
            using (pin)
            {
                ReadOnlySpan<byte> childValueBytes = node.GetValue(0);
                ulong childOffset = BSearchIndex.BSearchIndexReader.ReadUInt64LE(childValueBytes) + node.Metadata.BaseOffset;
                currentEnd = _hsstStart + (long)childOffset + 1;
            }
            depth++;
        }
        return false;
    }

    /// <summary>
    /// Pop ancestors until we find one with a sibling child to advance into; on success descend
    /// from there back down to the next leaf. Returns false when the whole tree is exhausted.
    /// </summary>
    private bool AscendAndDescend()
    {
        while (_depth > 0)
        {
            _depth--;
            ref Ancestor anc = ref _ancestors[_depth];
            anc.LastIdx++;

            if (!TryLoadNode(anc.AbsEnd, out HsstIndex parent, out _, out TPin parentPin))
                return false;
            long childEnd;
            using (parentPin)
            {
                if (anc.LastIdx >= parent.EntryCount)
                {
                    // Exhausted at this level; keep ascending.
                    continue;
                }
                ReadOnlySpan<byte> childValueBytes = parent.GetValue(anc.LastIdx);
                ulong childOffset = BSearchIndex.BSearchIndexReader.ReadUInt64LE(childValueBytes) + parent.Metadata.BaseOffset;
                childEnd = _hsstStart + (long)childOffset + 1;
            }
            _depth++;
            return DescendToLeaf(childEnd);
        }
        // Root exhausted.
        _depth = -2;
        return false;
    }

    /// <summary>
    /// Materialise the current leaf entry: compute the (key, value) bounds without copying any
    /// bytes into the enumerator. Key and value live in the data region with metaStart as the
    /// pivot.
    /// </summary>
    private void UpdateCurrent()
    {
        // Leaf value is a metaStart pointer into the data region.
        ReadOnlySpan<byte> metaBytes = _leafNode.GetValue(_leafIdx);
        ulong metaStart = BSearchIndex.BSearchIndexReader.ReadUInt64LE(metaBytes) + _leafNode.Metadata.BaseOffset;
        long absMetaStart = _hsstStart + (long)metaStart;

        // Read ValueLength (LEB128, ≤5 bytes) + KeyLength (u8, 1 byte). This is the leading
        // sequential read for each entry during enumeration, so use the readahead variant —
        // paged/mmap readers can prefetch the next window here.
        Span<byte> lebBuf = stackalloc byte[6];
        int available = (int)Math.Min(6, _hsstEnd - absMetaStart);
        if (available <= 0 || !_reader.TryReadWithReadahead(absMetaStart, lebBuf[..available])) return;
        int pos = 0;
        int valueLength = Leb128.Read(lebBuf, ref pos);
        if (pos >= available) return;
        int keyLength = lebBuf[pos++];
        long keyAbsStart = absMetaStart + pos;

        _currentKeyBound = new Bound(keyAbsStart, keyLength);
        _currentValueBound = new Bound(absMetaStart - valueLength, valueLength);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryLoadNode(long absEnd, out HsstIndex node, out long nodeAbsStart, out TPin pin)
    {
        node = default;
        nodeAbsStart = 0;
        pin = default;

        if (absEnd < 12) return false;

        // BSearchIndex node footer is fixed-width; pin a bounded window covering
        // the worst-case footer (6 base bytes + mandatory 6-byte baseOffset + optional
        // common-prefix block ≤ 128 bytes) and parse backwards from the flags byte.
        const int MaxFooterBytes = 6 + 1 + 128 + 6;
        long footerStart = Math.Max(0, absEnd - MaxFooterBytes);
        int footerLen = (int)(absEnd - footerStart);

        int totalNodeSize;
        using (TPin metaPin = _reader.PinBuffer(footerStart, footerLen))
        {
            ReadOnlySpan<byte> metaSpan = metaPin.Buffer;
            byte flags = metaSpan[footerLen - 1];
            int valueSize = metaSpan[footerLen - 6];
            int keySize = BinaryPrimitives.ReadUInt16LittleEndian(metaSpan[(footerLen - 5)..]);
            int keyCount = BinaryPrimitives.ReadUInt16LittleEndian(metaSpan[(footerLen - 3)..]);
            int keyType = (flags >> 1) & 0x03;
            int valueType = (flags >> 3) & 0x03;
            int keySectionSize = keyType switch { 0 => keySize, _ => keyCount * keySize };
            int valueSectionSize = valueType switch { 0 => valueSize, _ => keyCount * valueSize };
            int extraFooter = 6; // mandatory BaseOffset
            if ((flags & 0x40) != 0)
                extraFooter += 1 + metaSpan[footerLen - 7];
            totalNodeSize = valueSectionSize + keySectionSize + 6 + extraFooter;
        }

        nodeAbsStart = absEnd - totalNodeSize;
        if (nodeAbsStart < 0) return false;

        pin = _reader.PinBuffer(nodeAbsStart, totalNodeSize);
        node = HsstIndex.ReadFromEnd(pin.Buffer, totalNodeSize);
        return true;
    }
}

/// <summary>
/// One key/value pair yielded by <see cref="HsstEnumerator{TReader,TPin}.Current"/>. Both
/// fields are absolute reader offset+length tuples; callers slice them out of the underlying
/// data span (or pin via the reader). Both bounds stay valid for the reader's lifetime —
/// no per-MoveNext invalidation, since neither involves enumerator-owned storage.
/// </summary>
public readonly ref struct KeyValueEntry(Bound keyBound, Bound valueBound)
{
    public Bound KeyBound { get; } = keyBound;
    public Bound ValueBound { get; } = valueBound;
}
