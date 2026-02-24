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
    private ref TWriter _writer;
    private readonly int _startWritten;
    private readonly BSearchIndexMetadata _metadata;
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
            value.CopyTo(_writer.GetSpan(value.Length));
            _writer.Advance(value.Length);
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
            WriteEmptyNode();
        else
        {
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

            WriteMetadata(keySize, valueSize);
        }
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
            _keyBuf.Slice(keySrc, keyLen).CopyTo(_writer.GetSpan(keyLen));
            _writer.Advance(keyLen);
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

        // Pre-compute offsets by iterating key lengths
        Span<ushort> offsets = stackalloc ushort[_count];
        int keySrc = 0;
        int dataOffset = 0;
        for (int i = 0; i < _count; i++)
        {
            int len = BinaryPrimitives.ReadUInt16LittleEndian(_keyBuf[keySrc..]);
            keySrc += 2 + len;
            offsets[i] = (ushort)dataOffset;
            dataOffset += Leb128.EncodedSize(len) + len;
        }

        // Write offset table
        Span<byte> table = _writer.GetSpan(tableSize);
        for (int i = 0; i < _count; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(table[(i * 2)..], offsets[i]);
        _writer.Advance(tableSize);

        // Write key data
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
                _keyBuf.Slice(keySrc, len).CopyTo(_writer.GetSpan(len));
                _writer.Advance(len);
            }
            keySrc += len;
        }

        int keysSize = tableSize + dataOffset;
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
                _valueBuf.Slice(valSrc, valLen).CopyTo(_writer.GetSpan(valLen));
                _writer.Advance(valLen);
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

        // Pre-compute offsets
        Span<ushort> offsets = stackalloc ushort[_count];
        int valSrc = 0;
        int dataOffset = 0;
        for (int i = 0; i < _count; i++)
        {
            int len = BinaryPrimitives.ReadUInt16LittleEndian(_valueBuf[valSrc..]);
            valSrc += 2 + len;
            offsets[i] = (ushort)dataOffset;
            dataOffset += Leb128.EncodedSize(len) + len;
        }

        // Write offset table
        Span<byte> table = _writer.GetSpan(tableSize);
        for (int i = 0; i < _count; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(table[(i * 2)..], offsets[i]);
        _writer.Advance(tableSize);

        // Write value data
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
                _valueBuf.Slice(valSrc, len).CopyTo(_writer.GetSpan(len));
                _writer.Advance(len);
            }
            valSrc += len;
        }

        return tableSize + dataOffset;
    }

    private void WriteMetadata(int keySize, int valueSize)
    {
        int metadataStart = _writer.Written;
        bool hasBaseOffset = _metadata.BaseOffset > 0;
        byte flags = (byte)(
            (_metadata.IsIntermediate ? 0x01 : 0x00) |
            (_metadata.KeyType << 1) |
            (_metadata.ValueType << 3) |
            (hasBaseOffset ? 0x20 : 0x00));

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

        int metadataLen = _writer.Written - metadataStart;
        span = _writer.GetSpan(1);
        span[0] = (byte)metadataLen;
        _writer.Advance(1);
    }
}
