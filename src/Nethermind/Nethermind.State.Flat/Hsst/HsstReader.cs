// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
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
    where TPin : struct, IBufferPin, allows ref struct
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
    /// Exact-match B-tree lookup within the current <see cref="Bound"/>. On success sets
    /// <see cref="_bound"/> to the matched entry's value region and returns the prior bound via
    /// <paramref name="previousBound"/>. Returns false if no entry has exactly <paramref name="key"/>.
    /// Use <see cref="TrySeekFloor"/> for floor (largest entry ≤ key) semantics.
    /// </summary>
    public bool TrySeek(scoped ReadOnlySpan<byte> key, out Bound previousBound) =>
        TrySeekCore(key, exactMatch: true, out previousBound);

    /// <summary>
    /// Floor B-tree lookup within the current <see cref="Bound"/>. On success sets
    /// <see cref="_bound"/> to the floor entry's value region (largest stored key ≤ <paramref name="key"/>)
    /// and returns the prior bound via <paramref name="previousBound"/>. Returns false if the HSST
    /// is empty or <paramref name="key"/> precedes every entry.
    /// </summary>
    public bool TrySeekFloor(scoped ReadOnlySpan<byte> key, out Bound previousBound) =>
        TrySeekCore(key, exactMatch: false, out previousBound);

    private bool TrySeekCore(scoped ReadOnlySpan<byte> key, bool exactMatch, out Bound previousBound)
    {
        previousBound = _bound;

        if (_bound.Length < 2) return false;

        Span<byte> vb = stackalloc byte[1];
        if (!_reader.TryRead(_bound.Offset, vb)) return false;
        bool isInline = (vb[0] & 0x80) != 0;

        long currentAbsEnd = _bound.Offset + _bound.Length;

        while (true)
        {
            if (!TryLoadNode(currentAbsEnd, out HsstIndex node, out long nodeAbsStart, out TPin pin))
                return false;
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
                    if (exactMatch && !key.SequenceEqual(node.GetKey(floorIdx))) return false;
                    ReadOnlySpan<byte> val = node.GetValue(floorIdx);
                    if (val.IsEmpty)
                    {
                        _bound = new Bound(0, 0);
                        return true;
                    }
                    ReadOnlySpan<byte> nodeBytes = pin.Buffer;
                    int offsetInNode = (int)Unsafe.ByteOffset(
                        ref Unsafe.AsRef(in MemoryMarshal.GetReference(nodeBytes)),
                        ref Unsafe.AsRef(in MemoryMarshal.GetReference(val)));
                    _bound = new Bound(nodeAbsStart + offsetInNode, val.Length);
                    return true;
                }
                else
                {
                    if (!node.TryGetFloor(key, out ReadOnlySpan<byte> separator, out ReadOnlySpan<byte> metaBytes))
                        return false;

                    // Cheap reject path: the stored full key starts with the leaf separator,
                    // so the input must too. Saves a length-mismatch read in the common
                    // exact-miss case.
                    if (exactMatch && !key.StartsWith(separator)) return false;

                    int metaStart = BinaryPrimitives.ReadInt32LittleEndian(metaBytes) + node.Metadata.BaseOffset;
                    long absMetaStart = _bound.Offset + 1 + metaStart;

                    // Read up to 6 bytes from absMetaStart: enough for ValueLength (≤5)
                    // LEB128 + KeyLength (1 byte). KeyLength only consumed when exact-matching.
                    long available = _bound.Offset + _bound.Length - absMetaStart;
                    if (available <= 0) return false;
                    Span<byte> lebBuf = stackalloc byte[6];
                    int lebRead = (int)Math.Min(6, available);
                    if (!_reader.TryRead(absMetaStart, lebBuf[..lebRead])) return false;

                    int pos = 0;
                    int valueLength = Leb128.Read(lebBuf, ref pos);

                    if (exactMatch)
                    {
                        if (pos >= lebRead) return false;
                        int keyLength = lebBuf[pos++];
                        if (keyLength != key.Length) return false;

                        // Stored key fits in 255 bytes — single read + compare, no chunking.
                        Span<byte> stored = stackalloc byte[255];
                        Span<byte> storedSlice = stored[..keyLength];
                        if (!_reader.TryRead(absMetaStart + pos, storedSlice)) return false;
                        if (!storedSlice.SequenceEqual(key)) return false;
                    }

                    // value bytes are immediately before the metaStart
                    _bound = new Bound(absMetaStart - valueLength, valueLength);
                    return true;
                }
            }
        }
    }

    /// <summary>
    /// Load the index node whose exclusive end is <paramref name="absEnd"/> via the reader's
    /// <see cref="IHsstByteReader{TPin}.PinBuffer"/>. On success outs the parsed <see cref="HsstIndex"/>,
    /// the node's absolute start offset, and the pin (whose <see cref="IBufferPin.Buffer"/> backs
    /// <paramref name="node"/>). The caller must dispose the pin once it's done with the node.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryLoadNode(long absEnd, out HsstIndex node, out long nodeAbsStart, out TPin pin)
    {
        node = default;
        nodeAbsStart = 0;
        pin = default;

        if (absEnd < 1) return false;

        // Read the trailing MetadataLength byte
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
            // BaseOffset is consumed by HsstIndex.ReadFromEnd; we only need section sizes here.
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

    public void Dispose()
    {
        // No owned resources; pins are released per-iteration in TrySeek.
    }
}
