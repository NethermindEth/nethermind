// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
    private readonly bool _isInline;
    private readonly bool _empty;

    // PackedArray state: a packed entry array, no b-tree walk. _flatIdx is the next entry to
    // yield; -1 means not yet started; >= _flatEntryCount means exhausted.
    private readonly bool _isFlat;
    private readonly int _flatKeySize;
    private readonly int _flatValueSize;
    private readonly int _flatEntryCount;
    private readonly long _flatDataStart;
    private int _flatIdx;

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
            _isInline = false;
            return;
        }

        // IndexType byte is the last byte of the HSST.
        Span<byte> idxType = stackalloc byte[1];
        if (!_reader.TryRead(_hsstEnd - 1, idxType))
        {
            _empty = true;
            _isInline = false;
            return;
        }
        switch ((IndexType)idxType[0])
        {
            case IndexType.BTree:
                _isInline = false;
                _rootAbsEnd = _hsstEnd - 1;
                break;
            case IndexType.BTreeInlineValue:
                _isInline = true;
                _rootAbsEnd = _hsstEnd - 1;
                break;
            case IndexType.BTreeHashIndex:
                _isInline = false;
                Span<byte> sizeBuf = stackalloc byte[4];
                if (!_reader.TryRead(_hsstEnd - 5, sizeBuf))
                {
                    _empty = true;
                    return;
                }
                uint tableSizeU = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(sizeBuf);
                if (tableSizeU == 0 || tableSizeU > int.MaxValue)
                {
                    _empty = true;
                    return;
                }
                long tableBytes = (long)tableSizeU * 4;
                _rootAbsEnd = _hsstEnd - 5 - tableBytes;
                if (_rootAbsEnd < _hsstStart)
                {
                    _empty = true;
                    return;
                }
                break;
            case IndexType.PackedArray:
                _isInline = false;
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
            default:
                _empty = true;
                _isInline = false;
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

        if (_depth < 0)
        {
            // Root node ends just before the trailing IndexType byte (BTree/Inline)
            // or just before the appended hash table (BTreeHashIndex).
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
                int childOffset = BinaryPrimitives.ReadInt32LittleEndian(childValueBytes) + node.Metadata.BaseOffset;
                currentEnd = _hsstStart + childOffset + 1;
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
                int childOffset = BinaryPrimitives.ReadInt32LittleEndian(childValueBytes) + parent.Metadata.BaseOffset;
                childEnd = _hsstStart + childOffset + 1;
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
    /// bytes into the enumerator. For inline mode the key sits inside the leaf node's pinned
    /// buffer; for non-inline mode both key and value live in the data region with metaStart
    /// as the pivot.
    /// </summary>
    private void UpdateCurrent()
    {
        if (_isInline)
        {
            ReadOnlySpan<byte> nodeBytes = _leafPin.Buffer;
            ref readonly byte nodeBytesRef = ref MemoryMarshal.GetReference(nodeBytes);

            // Key span in the leaf — point a Bound at it via leaf abs-start + intra-node offset.
            ReadOnlySpan<byte> keySpan = _leafNode.GetKey(_leafIdx);
            int keyOffsetInNode = (int)Unsafe.ByteOffset(
                ref Unsafe.AsRef(in nodeBytesRef),
                ref Unsafe.AsRef(in MemoryMarshal.GetReference(keySpan)));
            _currentKeyBound = new Bound(_leafAbsStart + keyOffsetInNode, keySpan.Length);

            ReadOnlySpan<byte> val = _leafNode.GetValue(_leafIdx);
            if (val.IsEmpty)
            {
                _currentValueBound = new Bound(0, 0);
                return;
            }
            int valOffsetInNode = (int)Unsafe.ByteOffset(
                ref Unsafe.AsRef(in nodeBytesRef),
                ref Unsafe.AsRef(in MemoryMarshal.GetReference(val)));
            _currentValueBound = new Bound(_leafAbsStart + valOffsetInNode, val.Length);
            return;
        }

        // Non-inline: leaf value is a metaStart pointer into the data region.
        ReadOnlySpan<byte> metaBytes = _leafNode.GetValue(_leafIdx);
        int metaStart = BinaryPrimitives.ReadInt32LittleEndian(metaBytes) + _leafNode.Metadata.BaseOffset;
        long absMetaStart = _hsstStart + metaStart;

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

        if (absEnd < 1) return false;

        Span<byte> oneByte = stackalloc byte[1];
        if (!_reader.TryRead(absEnd - 1, oneByte)) return false;
        int metadataLen = oneByte[0];

        long metadataAbsStart = absEnd - 1 - metadataLen;
        if (metadataAbsStart < 0) return false;

        int totalNodeSize;
        using (TPin metaPin = _reader.PinBuffer(metadataAbsStart, metadataLen))
        {
            ReadOnlySpan<byte> metaSpan = metaPin.Buffer;
            int p = 0;
            byte flags = metaSpan[p++];
            int keyCount = Leb128.Read(metaSpan, ref p);
            int keySize = Leb128.Read(metaSpan, ref p);
            int valueSize = Leb128.Read(metaSpan, ref p);
            int keyType = (flags >> 1) & 0x03;
            int valueType = (flags >> 3) & 0x03;
            int keySectionSize = keyType switch { 0 => keySize, _ => keyCount * keySize };
            int valueSectionSize = valueType switch { 0 => valueSize, _ => keyCount * valueSize };
            totalNodeSize = valueSectionSize + keySectionSize + metadataLen + 1;
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
