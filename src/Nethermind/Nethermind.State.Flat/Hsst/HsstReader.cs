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
/// Non-span HSST reader generic over <typeparamref name="TReader"/>. Symmetric to
/// <see cref="HsstBuilder{TWriter}"/>: any byte source that implements
/// <see cref="IHsstByteReader"/> works — mmap, heap array, file handle, etc.
///
/// Maintains an active <see cref="Bound"/> (absolute offset+length within the reader).
/// <see cref="TrySeek"/> does a floor B-tree lookup and repositions the bound to the matched
/// entry's value region; the caller saves/restores scope via <see cref="GetBound"/> /
/// <see cref="SetBound"/> using the <c>out previousBound</c> parameter.
/// </summary>
public ref struct HsstReader<TReader, TPin>(scoped in TReader reader, Bound initialBound) : IDisposable
    where TPin : struct, IDisposable, allows ref struct
    where TReader : IHsstByteReader<TPin>, allows ref struct
{
    private TReader _reader = reader;
    private Bound _bound = initialBound;

    public HsstReader(scoped in TReader reader) : this(reader, new Bound(0, (int)reader.Length)) { }

    public readonly Bound GetBound() => _bound;
    public void SetBound(Bound bound) => _bound = bound;

    /// <summary>
    /// Copy the active bound's bytes into <paramref name="output"/>.
    /// Returns the number of bytes actually written (min of bound length and output length).
    /// </summary>
    public readonly int GetValue(Span<byte> output)
    {
        int count = Math.Min(_bound.Length, output.Length);
        if (count > 0)
            _reader.TryRead(_bound.Offset, output[..count]);
        return count;
    }

    /// <summary>
    /// Floor B-tree lookup within the current <see cref="Bound"/> (treated as an HSST).
    /// On success sets <see cref="_bound"/> to the floor entry's value region and returns the
    /// prior bound via <paramref name="previousBound"/> so the caller can restore it with
    /// <see cref="SetBound"/>. Returns false if the HSST is empty or <paramref name="key"/>
    /// precedes every entry.
    /// </summary>
    public bool TrySeek(ReadOnlySpan<byte> key, out Bound previousBound)
    {
        previousBound = _bound;

        if (_bound.Length < 2) return false;

        Span<byte> vb = stackalloc byte[1];
        if (!_reader.TryRead(_bound.Offset, vb)) return false;
        bool isInline = (vb[0] & 0x80) != 0;

        long currentAbsEnd = _bound.Offset + _bound.Length;

        while (true)
        {
            TPin pin = TryLoadNode(currentAbsEnd, out HsstIndex node, out long nodeAbsStart, out ReadOnlySpan<byte> nodeBytes);
            if (nodeBytes.IsEmpty) return false;
            using (pin)
            {
                if (node.IsIntermediate)
                {
                    if (!node.TryGetFloor(key, out _, out ReadOnlySpan<byte> childValueBytes))
                        return false;
                    int childOffset = BinaryPrimitives.ReadInt32LittleEndian(childValueBytes) + node.Metadata.BaseOffset;
                    // childOffset is the inclusive last byte of the child node (0-indexed within the HSST).
                    // Exclusive end in reader-absolute terms = _bound.Offset + childOffset + 1.
                    currentAbsEnd = _bound.Offset + childOffset + 1;
                    continue;
                }

                // Leaf node
                if (isInline)
                {
                    int floorIdx = node.FindFloorIndex(key);
                    if (floorIdx < 0) return false;
                    ReadOnlySpan<byte> val = node.GetValue(floorIdx);
                    if (val.IsEmpty)
                    {
                        _bound = new Bound(0, 0);
                        return true;
                    }
                    int offsetInNode = (int)Unsafe.ByteOffset(
                        ref Unsafe.AsRef(in MemoryMarshal.GetReference(nodeBytes)),
                        ref Unsafe.AsRef(in MemoryMarshal.GetReference(val)));
                    _bound = new Bound(nodeAbsStart + offsetInNode, val.Length);
                    return true;
                }
                else
                {
                    if (!node.TryGetFloor(key, out _, out ReadOnlySpan<byte> metaBytes))
                        return false;
                    int metaStart = BinaryPrimitives.ReadInt32LittleEndian(metaBytes) + node.Metadata.BaseOffset;
                    long absMetaStart = _bound.Offset + 1 + metaStart;

                    // Read enough bytes to decode the valueLength LEB128 (max 5 bytes for int32).
                    long available = _bound.Offset + _bound.Length - absMetaStart;
                    if (available <= 0) return false;
                    Span<byte> lebBuf = stackalloc byte[5];
                    int lebRead = (int)Math.Min(5, available);
                    if (!_reader.TryRead(absMetaStart, lebBuf[..lebRead])) return false;
                    int pos = 0;
                    int valueLength = Leb128.Read(lebBuf, ref pos);
                    // value bytes are immediately before the metaStart
                    _bound = new Bound(absMetaStart - valueLength, valueLength);
                    return true;
                }
            }
        }
    }

    /// <summary>
    /// Load the index node whose exclusive end is <paramref name="absEnd"/> via the reader's
    /// <see cref="IHsstByteReader{TPin}.PinBuffer"/>. Returns the parsed <see cref="HsstIndex"/>,
    /// the node's absolute start offset, the backing span (used by callers to compute inline-value
    /// offsets), and the pin the caller must dispose to release the window.
    /// On failure, <paramref name="nodeBytes"/> is empty; the returned pin is still safe to dispose.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private TPin TryLoadNode(long absEnd, out HsstIndex node, out long nodeAbsStart, [UnscopedRef] out ReadOnlySpan<byte> nodeBytes)
    {
        node = default;
        nodeAbsStart = 0;
        nodeBytes = default;

        if (absEnd < 1) return default;

        // Read the trailing MetadataLength byte
        Span<byte> oneByte = stackalloc byte[1];
        if (!_reader.TryRead(absEnd - 1, oneByte)) return default;
        int metadataLen = oneByte[0];

        long metadataAbsStart = absEnd - 1 - metadataLen;
        if (metadataAbsStart < 0) return default;

        int totalNodeSize;
        using (TPin metaPin = _reader.PinBuffer(metadataAbsStart, metadataLen, out ReadOnlySpan<byte> metaSpan))
        {
            int p = 0;
            byte flags = metaSpan[p++];
            int keyCount = Leb128.Read(metaSpan, ref p);
            int keySize = Leb128.Read(metaSpan, ref p);
            int valueSize = Leb128.Read(metaSpan, ref p);
            // BaseOffset is consumed by HsstIndex.ReadFromEnd; we only need section sizes here.
            int keyType = (flags >> 1) & 0x03;
            int valueType = (flags >> 3) & 0x03;
            int keySectionSize = keyType switch { 0 => keySize, _ => keyCount * keySize };
            int valueSectionSize = valueType switch { 0 => valueSize, _ => keyCount * valueSize };
            totalNodeSize = valueSectionSize + keySectionSize + metadataLen + 1;
        }

        nodeAbsStart = absEnd - totalNodeSize;
        if (nodeAbsStart < 0) return default;

        TPin pin = _reader.PinBuffer(nodeAbsStart, totalNodeSize, out nodeBytes);
        node = HsstIndex.ReadFromEnd(nodeBytes, totalNodeSize);
        return pin;
    }

    public void Dispose()
    {
        // No owned resources; pins are released per-iteration in TrySeek.
    }
}
