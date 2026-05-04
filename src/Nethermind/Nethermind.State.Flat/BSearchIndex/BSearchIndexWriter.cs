// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Utils;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.BSearchIndex;

/// <summary>
/// Metadata describing the format of an index node to build.
/// </summary>
internal struct BSearchIndexMetadata
{
    /// <summary>True if this is an internal (non-leaf) node.</summary>
    public bool IsIntermediate;
    /// <summary>0=Variable, 1=Uniform, 2=UniformWithLen.</summary>
    public int KeyType;
    /// <summary>
    /// Base offset subtracted from values before writing.
    /// 0 means no base offset. When non-zero, caller must subtract this from each value before calling AddKey.
    /// </summary>
    public int BaseOffset;
    /// <summary>
    /// Uniform/UniformWithLen: fixed key length or slot size.
    /// Variable: ignored.
    /// </summary>
    public int KeySlotSize;
    /// <summary>0=Variable, 1=Uniform, 2=UniformWithLen. Default: Uniform.</summary>
    public int ValueType = 1;
    /// <summary>Uniform/UniformWithLen: fixed value size or slot size. Default: 4-byte int offsets.</summary>
    public int ValueSlotSize = 4;

    public BSearchIndexMetadata() { }
}

/// <summary>
/// Writes B-tree index nodes using an AddKey/Finalize builder pattern.
///
/// Index block layout: [Values section][Keys section][Metadata][MetadataLength: u8]
///
/// Variable-encoded sections place entry data first, followed by the
/// count × u16 offset table at the end of the section. This matches the
/// back-to-front layout of the rest of the format and lets the writer stream
/// entries forward, appending offsets at finalization.
///
/// Metadata: [Flags: 1][KeyCount: LEB128][KeySize: LEB128][ValueSize: LEB128][BaseOffset: LEB128?]
///
/// Usage: create with writer + metadata + key scratch buffer, call AddKey(key, value)
/// for each entry in sorted key order, call Finalize() to produce the final binary layout.
///
/// <paramref name="keyBuffer"/> holds intermediate key data during build. Required size:
/// sum of (2 + key.Length) for each entry that will be added (2 bytes per ushort length prefix).
/// </summary>
internal ref struct BSearchIndexWriter<TWriter>
    where TWriter : IByteBufferWriter
{
    /// <summary>
    /// Cap on the in-metadata common-key-prefix length. Metadata is bounded by
    /// MetadataLength (u8); 128 leaves comfortable headroom for the other fields.
    /// </summary>
    private const int MaxCommonKeyPrefixLen = 128;

    private ref TWriter _writer;
    private readonly int _startWritten;
    private BSearchIndexMetadata _metadata;
    private readonly Span<byte> _keyBuf;
    private readonly Span<byte> _valueBuf;
    private int _count;
    private int _keyPos;    // grows forward from 0 in _keyBuf
    private int _valuePos;  // grows forward from 0 in _valueBuf

    public BSearchIndexWriter(ref TWriter writer, BSearchIndexMetadata metadata, Span<byte> keyBuffer)
    {
        _writer = ref writer;
        _startWritten = _writer.Written;
        _metadata = metadata;
        _keyBuf = keyBuffer;
        _valueBuf = default;
        _count = 0;
        _keyPos = 0;
        _valuePos = 0;
    }

    public BSearchIndexWriter(ref TWriter writer, BSearchIndexMetadata metadata, Span<byte> keyBuffer, Span<byte> valueBuffer)
    {
        _writer = ref writer;
        _startWritten = _writer.Written;
        _metadata = metadata;
        _keyBuf = keyBuffer;
        _valueBuf = valueBuffer;
        _count = 0;
        _keyPos = 0;
        _valuePos = 0;
    }

    /// <summary>
    /// Add a key-value pair. Must be called in sorted key order.
    /// If <see cref="BSearchIndexMetadata.BaseOffset"/> is non-zero, value bytes must already
    /// have the base offset subtracted before calling AddKey.
    /// </summary>
    public void AddKey(scoped ReadOnlySpan<byte> key, scoped ReadOnlySpan<byte> value)
    {
        if (_valueBuf.Length > 0)
        {
            // Buffer value: [u16 length][value bytes]
            BinaryPrimitives.WriteUInt16LittleEndian(_valueBuf[_valuePos..], (ushort)value.Length);
            _valuePos += 2;
            value.CopyTo(_valueBuf[_valuePos..]);
            _valuePos += value.Length;
        }
        else
        {
            // Write value forward via writer
            IByteBufferWriter.Copy(ref _writer, value);
        }

        // Store key in keyBuf: [u16 length][key bytes]
        BinaryPrimitives.WriteUInt16LittleEndian(_keyBuf[_keyPos..], (ushort)key.Length);
        _keyPos += 2;
        key.CopyTo(_keyBuf[_keyPos..]);
        _keyPos += key.Length;

        _count++;
    }

    /// <summary>
    /// Write the final binary layout. The ref writer is already advanced.
    /// </summary>
    public void FinalizeNode()
    {
        if (_count == 0)
        {
            WriteEmptyNode();
            return;
        }

        // Detect a longest common byte prefix shared by every buffered key.
        // Stored once in metadata; per-entry storage drops to suffixes only.
        Span<byte> prefixBuf = stackalloc byte[MaxCommonKeyPrefixLen];
        int prefixLen = ApplyCommonKeyPrefix(prefixBuf);

        // Write buffered values if applicable
        int valueSize;
        if (_valueBuf.Length > 0)
        {
            valueSize = _metadata.ValueType switch
            {
                1 => FinalizeUniformValues(),
                2 => FinalizeUniformWithLenValues(),
                _ => FinalizeVariableValues(),
            };
        }
        else
        {
            valueSize = _metadata.ValueSlotSize;
        }

        // Write keys
        int keySize = _metadata.KeyType switch
        {
            1 => FinalizeUniformKeys(),
            2 => FinalizeUniformWithLenKeys(),
            _ => FinalizeVariableKeys(),
        };

        WriteMetadata(keySize, valueSize, prefixBuf[..prefixLen]);
    }

    /// <summary>
    /// Detect the longest common byte prefix across all buffered keys. When the prefix
    /// pays for itself (savings = prefixLen × (count − 1) − 1 > 0), strip it from every
    /// entry in <see cref="_keyBuf"/> in-place, copy the prefix bytes into
    /// <paramref name="prefixOut"/>, adjust uniform slot sizes, and return the prefix
    /// length. Returns 0 when the optimization isn't worth applying.
    /// </summary>
    private int ApplyCommonKeyPrefix(scoped Span<byte> prefixOut)
    {
        if (_count < 2) return 0;

        // Pass 1: compute LCP and shortest-key length.
        int firstLen = BinaryPrimitives.ReadUInt16LittleEndian(_keyBuf);
        int firstStart = 2;
        int lcp = firstLen;
        int shortestLen = firstLen;
        int srcPos = 2 + firstLen;

        for (int i = 1; i < _count && lcp > 0; i++)
        {
            int len = BinaryPrimitives.ReadUInt16LittleEndian(_keyBuf[srcPos..]);
            srcPos += 2;
            if (len < shortestLen) shortestLen = len;
            int boundary = Math.Min(len, lcp);
            int common = _keyBuf.Slice(firstStart, boundary)
                .CommonPrefixLength(_keyBuf.Slice(srcPos, boundary));
            if (common < lcp) lcp = common;
            srcPos += len;
        }

        if (lcp > MaxCommonKeyPrefixLen) lcp = MaxCommonKeyPrefixLen;

        // Gating: skip when no positive savings, or when stripping would empty out
        // the shortest key (degenerate; would also collapse Uniform slots to 0).
        if (lcp == 0) return 0;
        if (lcp >= shortestLen) return 0;
        if (lcp * (_count - 1) - 1 <= 0) return 0;

        // Stash prefix bytes from the first key BEFORE we rewrite _keyBuf in place.
        _keyBuf.Slice(firstStart, lcp).CopyTo(prefixOut);

        // Pass 2: in-place forward rewrite. Each entry shrinks by `lcp` bytes; dst ≤ src
        // throughout, so a forward CopyTo is safe.
        int dstPos = 0;
        int rsrc = 0;
        for (int i = 0; i < _count; i++)
        {
            int len = BinaryPrimitives.ReadUInt16LittleEndian(_keyBuf[rsrc..]);
            rsrc += 2;
            int newLen = len - lcp;
            BinaryPrimitives.WriteUInt16LittleEndian(_keyBuf[dstPos..], (ushort)newLen);
            dstPos += 2;
            if (newLen > 0)
                _keyBuf.Slice(rsrc + lcp, newLen).CopyTo(_keyBuf[dstPos..]);
            dstPos += newLen;
            rsrc += len;
        }
        _keyPos = dstPos;

        // Adjust uniform slot sizes (Variable's section size is recomputed by its finalizer).
        if (_metadata.KeyType == 1 || _metadata.KeyType == 2)
            _metadata.KeySlotSize -= lcp;

        return lcp;
    }

    private void WriteEmptyNode()
    {
        byte flags = (byte)(_metadata.IsIntermediate ? 0x01 : 0x00);
        Span<byte> span = _writer.GetSpan(5);
        span[0] = flags;
        span[1] = 0x00; // KeyCount=0
        span[2] = 0x00; // KeySize=0
        span[3] = 0x00; // ValueSize=0
        span[4] = 4;    // MetadataLength=4
        _writer.Advance(5);
    }

    private int FinalizeUniformKeys()
    {
        int keyLen = _metadata.KeySlotSize;
        int keySrc = 0;
        for (int i = 0; i < _count; i++)
        {
            keySrc += 2; // skip u16 length (known from keyLen)
            IByteBufferWriter.Copy(ref _writer, _keyBuf.Slice(keySrc, keyLen));
            keySrc += keyLen;
        }
        return keyLen;
    }

    private int FinalizeUniformWithLenKeys()
    {
        int slotSize = _metadata.KeySlotSize;
        int keySrc = 0;
        for (int i = 0; i < _count; i++)
        {
            int len = BinaryPrimitives.ReadUInt16LittleEndian(_keyBuf[keySrc..]);
            keySrc += 2;
            Span<byte> slot = _writer.GetSpan(slotSize);
            slot[..slotSize].Clear();
            if (len > 0)
                _keyBuf.Slice(keySrc, len).CopyTo(slot);
            slot[slotSize - 1] = (byte)len;
            _writer.Advance(slotSize);
            keySrc += len;
        }
        return slotSize;
    }

    private int FinalizeVariableKeys()
    {
        int tableSize = _count * 2;

        // Pre-compute offsets (relative to section start) by iterating key lengths.
        Span<ushort> offsets = stackalloc ushort[_count];
        int keySrc = 0;
        int dataOffset = 0;
        for (int i = 0; i < _count; i++)
        {
            int len = BinaryPrimitives.ReadUInt16LittleEndian(_keyBuf[keySrc..]);
            keySrc += 2 + len;
            if (dataOffset > ushort.MaxValue)
                throw new InvalidOperationException("Variable section exceeds 64 KiB; offset table cannot address it");
            offsets[i] = (ushort)dataOffset;
            dataOffset += Leb128.EncodedSize(len) + len;
        }

        // Write key data first
        keySrc = 0;
        for (int i = 0; i < _count; i++)
        {
            int len = BinaryPrimitives.ReadUInt16LittleEndian(_keyBuf[keySrc..]);
            keySrc += 2;

            Span<byte> leb = _writer.GetSpan(10);
            int lebLen = Leb128.Write(leb, 0, len);
            _writer.Advance(lebLen);

            if (len > 0)
            {
                IByteBufferWriter.Copy(ref _writer, _keyBuf.Slice(keySrc, len));
            }
            keySrc += len;
        }

        // Then write offset table at the end of the section
        Span<byte> table = _writer.GetSpan(tableSize);
        for (int i = 0; i < _count; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(table[(i * 2)..], offsets[i]);
        _writer.Advance(tableSize);

        int keysSize = dataOffset + tableSize;
        return keysSize;
    }

    private int FinalizeUniformValues()
    {
        int valLen = _metadata.ValueSlotSize;
        int valSrc = 0;
        for (int i = 0; i < _count; i++)
        {
            valSrc += 2; // skip u16 length
            if (valLen > 0)
            {
                IByteBufferWriter.Copy(ref _writer, _valueBuf.Slice(valSrc, valLen));
            }
            valSrc += valLen;
        }
        return valLen;
    }

    private int FinalizeUniformWithLenValues()
    {
        int slotSize = _metadata.ValueSlotSize;
        int valSrc = 0;
        for (int i = 0; i < _count; i++)
        {
            int len = BinaryPrimitives.ReadUInt16LittleEndian(_valueBuf[valSrc..]);
            valSrc += 2;
            Span<byte> slot = _writer.GetSpan(slotSize);
            slot[..slotSize].Clear();
            if (len > 0)
                _valueBuf.Slice(valSrc, len).CopyTo(slot);
            slot[slotSize - 1] = (byte)len;
            _writer.Advance(slotSize);
            valSrc += len;
        }
        return slotSize;
    }

    private int FinalizeVariableValues()
    {
        int tableSize = _count * 2;

        // Pre-compute offsets (relative to section start)
        Span<ushort> offsets = stackalloc ushort[_count];
        int valSrc = 0;
        int dataOffset = 0;
        for (int i = 0; i < _count; i++)
        {
            int len = BinaryPrimitives.ReadUInt16LittleEndian(_valueBuf[valSrc..]);
            valSrc += 2 + len;
            if (dataOffset > ushort.MaxValue)
                throw new InvalidOperationException("Variable section exceeds 64 KiB; offset table cannot address it");
            offsets[i] = (ushort)dataOffset;
            dataOffset += Leb128.EncodedSize(len) + len;
        }

        // Write value data first
        valSrc = 0;
        for (int i = 0; i < _count; i++)
        {
            int len = BinaryPrimitives.ReadUInt16LittleEndian(_valueBuf[valSrc..]);
            valSrc += 2;

            Span<byte> leb = _writer.GetSpan(10);
            int lebLen = Leb128.Write(leb, 0, len);
            _writer.Advance(lebLen);

            if (len > 0)
            {
                IByteBufferWriter.Copy(ref _writer, _valueBuf.Slice(valSrc, len));
            }
            valSrc += len;
        }

        // Then write offset table at the end of the section
        Span<byte> table = _writer.GetSpan(tableSize);
        for (int i = 0; i < _count; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(table[(i * 2)..], offsets[i]);
        _writer.Advance(tableSize);

        return dataOffset + tableSize;
    }

    private void WriteMetadata(int keySize, int valueSize, scoped ReadOnlySpan<byte> commonKeyPrefix)
    {
        int metadataStart = _writer.Written;
        bool hasBaseOffset = _metadata.BaseOffset > 0;
        bool hasCommonPrefix = commonKeyPrefix.Length > 0;
        byte flags = (byte)(
            (_metadata.IsIntermediate ? 0x01 : 0x00) |
            (_metadata.KeyType << 1) |
            (_metadata.ValueType << 3) |
            (hasBaseOffset ? 0x20 : 0x00) |
            (hasCommonPrefix ? 0x40 : 0x00));

        Span<byte> span = _writer.GetSpan(1);
        span[0] = flags;
        _writer.Advance(1);

        Span<byte> leb = _writer.GetSpan(10);
        int lebLen = Leb128.Write(leb, 0, _count);
        _writer.Advance(lebLen);

        leb = _writer.GetSpan(10);
        lebLen = Leb128.Write(leb, 0, keySize);
        _writer.Advance(lebLen);

        leb = _writer.GetSpan(10);
        lebLen = Leb128.Write(leb, 0, valueSize);
        _writer.Advance(lebLen);

        if (hasBaseOffset)
        {
            leb = _writer.GetSpan(10);
            lebLen = Leb128.Write(leb, 0, _metadata.BaseOffset);
            _writer.Advance(lebLen);
        }

        if (hasCommonPrefix)
        {
            Span<byte> dst = _writer.GetSpan(1 + commonKeyPrefix.Length);
            dst[0] = (byte)commonKeyPrefix.Length;
            commonKeyPrefix.CopyTo(dst[1..]);
            _writer.Advance(1 + commonKeyPrefix.Length);
        }

        int metadataLen = _writer.Written - metadataStart;
        span = _writer.GetSpan(1);
        span[0] = (byte)metadataLen;
        _writer.Advance(1);
    }
}
