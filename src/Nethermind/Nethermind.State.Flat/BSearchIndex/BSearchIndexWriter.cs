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
    private int _count;
    private int _keyPos;    // grows forward from 0 in _keyBuf

    public BSearchIndexWriter(ref TWriter writer, BSearchIndexMetadata metadata, Span<byte> keyBuffer)
    {
        _writer = ref writer;
        _startWritten = _writer.Written;
        _metadata = metadata;
        _keyBuf = keyBuffer;
        _count = 0;
        _keyPos = 0;
    }

    /// <summary>
    /// Add a key-value pair. Must be called in sorted key order.
    /// If <see cref="BSearchIndexMetadata.BaseOffset"/> is non-zero, value bytes must already
    /// have the base offset subtracted before calling AddKey.
    /// </summary>
    public void AddKey(scoped ReadOnlySpan<byte> key, scoped ReadOnlySpan<byte> value)
    {
        // Write value forward via writer
        value.CopyTo(_writer.GetSpan(value.Length));
        _writer.Advance(value.Length);

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
            switch (_metadata.KeyType)
            {
                case 1: FinalizeUniform(); break;
                case 2: FinalizeUniformWithLen(); break;
                default: FinalizeVariable(); break;
            }
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

    private void FinalizeUniform()
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
        WriteMetadata(keyLen);
    }

    private void FinalizeUniformWithLen()
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
        WriteMetadata(slotSize);
    }

    private void FinalizeVariable()
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
        WriteMetadata(keysSize);
    }

    private void WriteMetadata(int keySize)
    {
        int metadataStart = _writer.Written;
        bool hasBaseOffset = _metadata.BaseOffset > 0;
        byte flags = (byte)(
            (_metadata.IsIntermediate ? 0x01 : 0x00) |
            (_metadata.KeyType << 1) |
            (1 << 3) |  // ValueType=1 (Uniform 4-byte)
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
        lebLen = Leb128.Write(leb, 0, 4); // ValueSize=4
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
