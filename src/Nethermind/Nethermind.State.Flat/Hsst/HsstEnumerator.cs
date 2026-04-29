// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
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
/// </summary>
public ref struct HsstEnumerator<TReader, TPin> : IDisposable
    where TPin : struct, IBufferPin, allows ref struct
    where TReader : IHsstByteReader<TPin>, allows ref struct
{
    /// <summary>Maximum supported B-tree depth. Realistic trees stay ≤4; 16 is a hard ceiling.</summary>
    private const int MaxDepth = 16;
    /// <summary>Inline buffer for reconstructed keys. Real-world HSST keys are ≤33 bytes; the
    /// generous 1 KiB ceiling keeps the enumerator allocation-free for any realistic load while
    /// still bounding the per-instance footprint.</summary>
    private const int InlineKeyBytes = 1024;

    [InlineArray(MaxDepth)]
    private struct AncestorStack { private Ancestor _e0; }

    private struct Ancestor
    {
        public long AbsEnd;
        public int LastIdx;
    }

    [InlineArray(InlineKeyBytes)]
    private struct InlineKeyBuf { private byte _e0; }

    private TReader _reader;
    private readonly long _hsstStart;
    private readonly long _hsstEnd;
    private readonly bool _isInline;
    private readonly bool _empty;

    private AncestorStack _ancestors;
    /// <summary>Depth of the current leaf in the tree (0 = root). −1 = not yet started.</summary>
    private int _depth;

    // Current leaf state
    private TPin _leafPin;
    private HsstIndex _leafNode;
    private long _leafAbsStart;
    private int _leafIdx;

    // Reconstructed current entry
    private InlineKeyBuf _keyBuf;
    private int _keyLen;
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

        Span<byte> vb = stackalloc byte[1];
        if (!_reader.TryRead(_hsstStart, vb))
        {
            _empty = true;
            _isInline = false;
            return;
        }
        _isInline = (vb[0] & 0x80) != 0;
        _empty = false;
    }

    public bool MoveNext()
    {
        if (_empty) return false;

        if (_depth < 0)
        {
            return DescendToLeaf(_hsstEnd);
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

    [UnscopedRef]
    public readonly KeyValueEntry Current => new(KeySpan, _currentValueBound);

    [UnscopedRef]
    private readonly ReadOnlySpan<byte> KeySpan
    {
        get
        {
            ref readonly byte first = ref _keyBuf[0];
            return MemoryMarshal.CreateReadOnlySpan(in first, _keyLen);
        }
    }

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
    /// Materialise the current leaf entry: reconstruct the full key into <c>_keyBuf</c>
    /// (separator + remainingKey for non-inline; full key for inline) and compute the value
    /// bound (absolute offset+length within the reader).
    /// </summary>
    private void UpdateCurrent()
    {
        ReadOnlySpan<byte> separator = _leafNode.GetKey(_leafIdx);

        if (_isInline)
        {
            // Inline: leaf stores the full key + value directly. Copy key into buffer.
            CopyKey(separator, default);
            ReadOnlySpan<byte> val = _leafNode.GetValue(_leafIdx);
            if (val.IsEmpty)
            {
                _currentValueBound = new Bound(0, 0);
                return;
            }
            ReadOnlySpan<byte> nodeBytes = _leafPin.Buffer;
            int offsetInNode = (int)Unsafe.ByteOffset(
                ref Unsafe.AsRef(in MemoryMarshal.GetReference(nodeBytes)),
                ref Unsafe.AsRef(in MemoryMarshal.GetReference(val)));
            _currentValueBound = new Bound(_leafAbsStart + offsetInNode, val.Length);
            return;
        }

        // Non-inline: leaf value is a metaStart pointer into the data region.
        ReadOnlySpan<byte> metaBytes = _leafNode.GetValue(_leafIdx);
        int metaStart = BinaryPrimitives.ReadInt32LittleEndian(metaBytes) + _leafNode.Metadata.BaseOffset;
        long absMetaStart = _hsstStart + 1 + metaStart;

        // Read ValueLength + RemainingKeyLength LEB128s (max 5 bytes each). This is the leading
        // sequential read for each entry during enumeration, so use the readahead variant —
        // paged/mmap readers can prefetch the next window here.
        Span<byte> lebBuf = stackalloc byte[10];
        int available = (int)Math.Min(10, _hsstEnd - absMetaStart);
        if (available <= 0 || !_reader.TryReadWithReadahead(absMetaStart, lebBuf[..available])) return;
        int pos = 0;
        int valueLength = Leb128.Read(lebBuf, ref pos);
        int remainingKeyLength = Leb128.Read(lebBuf, ref pos);
        long remainingKeyAbsStart = absMetaStart + pos;

        ReadRemainingKey(separator, remainingKeyAbsStart, remainingKeyLength);

        _currentValueBound = new Bound(absMetaStart - valueLength, valueLength);
    }

    private void CopyKey(ReadOnlySpan<byte> separator, ReadOnlySpan<byte> remaining)
    {
        int total = separator.Length + remaining.Length;
        if (total > InlineKeyBytes) ThrowKeyTooLarge();
        Span<byte> target = MemoryMarshal.CreateSpan(ref _keyBuf[0], InlineKeyBytes);
        separator.CopyTo(target);
        if (!remaining.IsEmpty)
            remaining.CopyTo(target[separator.Length..]);
        _keyLen = total;
    }

    private void ReadRemainingKey(ReadOnlySpan<byte> separator, long remainingKeyAbsStart, int remainingKeyLength)
    {
        int total = separator.Length + remainingKeyLength;
        if (total > InlineKeyBytes) ThrowKeyTooLarge();
        Span<byte> target = MemoryMarshal.CreateSpan(ref _keyBuf[0], InlineKeyBytes);
        separator.CopyTo(target);
        if (remainingKeyLength > 0)
        {
            Span<byte> remTarget = target.Slice(separator.Length, remainingKeyLength);
            _reader.TryRead(remainingKeyAbsStart, remTarget);
        }
        _keyLen = total;
    }

    private static void ThrowKeyTooLarge() =>
        throw new InvalidOperationException($"HsstEnumerator: key exceeds inline buffer ({InlineKeyBytes} bytes).");

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
/// One key/value pair yielded by <see cref="HsstEnumerator{TReader,TPin}.Current"/>.
/// The <see cref="Key"/> span is valid until the next <c>MoveNext</c> call;
/// <see cref="ValueBound"/> is an absolute reader offset+length and stays valid for the
/// lifetime of the underlying reader.
/// </summary>
public readonly ref struct KeyValueEntry(ReadOnlySpan<byte> key, Bound valueBound)
{
    public ReadOnlySpan<byte> Key { get; } = key;
    public Bound ValueBound { get; } = valueBound;
}
