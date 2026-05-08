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
    /// Base offset subtracted from values before writing. 0 means no base offset.
    /// When non-zero, caller must subtract this from each value before calling AddKey.
    /// Encoded on disk as a fixed 6-byte LE field (max 2^48 − 1 ≈ 256 TiB).
    /// </summary>
    public ulong BaseOffset;
    /// <summary>
    /// Uniform/UniformWithLen: fixed key length or slot size.
    /// Variable: ignored.
    /// </summary>
    public int KeySlotSize;
    /// <summary>0=Variable, 1=Uniform, 2=UniformWithLen. Default: Uniform.</summary>
    public int ValueType = 1;
    /// <summary>
    /// Uniform/UniformWithLen: fixed value size or slot size in bytes (1..8 for Uniform offsets).
    /// Default: 4 bytes.
    /// </summary>
    public int ValueSlotSize = 4;

    public BSearchIndexMetadata() { }
}

/// <summary>
/// Writes B-tree index nodes using an AddKey/Finalize builder pattern.
///
/// Index node layout (low → high address):
///   [Flags: u8][KeyCount: u16 LE][KeySize: u16 LE][ValueSize: u8][BaseOffset: 6-byte LE]
///   [CommonPrefixLen: u8][CommonPrefix bytes]?     (only if Flags bit6 set)
///   [Keys section][Values section]
///
/// Header is fixed-width (12 base bytes) plus an optional (1 + prefixLen) common-key-prefix
/// block. Readers parse it forward from the first byte; the parent stores the child's
/// first-byte offset. Putting the metadata header before the keys/values section lets the
/// hardware prefetcher pull the entry data into L1/L2 while the search code is still parsing
/// the header — the previous metadata-at-end layout fought the prefetcher's forward stride.
///
/// Variable-encoded sections (KeyType/ValueType=0) use a sentinel-terminated offset table
/// of (count+1) u16 entries appended after the raw entry data; length(i) =
/// offsets[i+1] - offsets[i]. No per-entry length prefix.
///
/// Usage: create with writer + metadata + key/value scratch buffers, call AddKey(key, value)
/// for each entry in sorted key order, call FinalizeNode() to flush the binary layout.
///
/// <paramref name="keyBuffer"/> holds intermediate key data during build. Required size:
/// sum of (2 + key.Length) for each entry. <paramref name="valueBuffer"/> mirrors that for
/// values: sum of (2 + value.Length). Both are sized by the caller from the known per-node
/// upper bound and reused across nodes.
/// </summary>
internal ref struct BSearchIndexWriter<TWriter>
    where TWriter : IByteBufferWriter
{
    private ref TWriter _writer;
    private readonly BSearchIndexMetadata _metadata;
    private readonly Span<byte> _keyBuf;
    private readonly Span<byte> _valueBuf;
    private readonly ReadOnlySpan<byte> _commonKeyPrefix;
    private int _count;
    private int _keyPos;    // grows forward from 0 in _keyBuf
    private int _valuePos;  // grows forward from 0 in _valueBuf

    public BSearchIndexWriter(
        ref TWriter writer,
        BSearchIndexMetadata metadata,
        Span<byte> keyBuffer,
        Span<byte> valueBuffer,
        ReadOnlySpan<byte> commonKeyPrefix = default)
    {
        _writer = ref writer;
        _metadata = metadata;
        _keyBuf = keyBuffer;
        _valueBuf = valueBuffer;
        _commonKeyPrefix = commonKeyPrefix;
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
        // Buffer value: [u16 length][value bytes]
        BinaryPrimitives.WriteUInt16LittleEndian(_valueBuf[_valuePos..], (ushort)value.Length);
        _valuePos += 2;
        value.CopyTo(_valueBuf[_valuePos..]);
        _valuePos += value.Length;

        // Store key in keyBuf: [u16 length][key bytes]
        BinaryPrimitives.WriteUInt16LittleEndian(_keyBuf[_keyPos..], (ushort)key.Length);
        _keyPos += 2;
        key.CopyTo(_keyBuf[_keyPos..]);
        _keyPos += key.Length;

        _count++;
    }

    /// <summary>
    /// Write the final binary layout. The ref writer is already advanced.
    ///
    /// <see cref="BSearchIndexMetadata.KeyType"/>, <see cref="BSearchIndexMetadata.KeySlotSize"/>,
    /// and the common-key-prefix passed at construction are taken as-is — the writer does
    /// not auto-detect or adjust. Callers (e.g. <c>HsstIndexBuilder</c>) decide both jointly
    /// via <see cref="BSearchIndexLayoutPlanner.Plan"/> and pre-strip prefix bytes from
    /// each <see cref="AddKey"/> call so that <see cref="_keyBuf"/> already holds suffixes.
    /// </summary>
    public void FinalizeNode()
    {
        if (_count == 0)
        {
            WriteEmptyNode();
            return;
        }

        // Section sizes are known from the buffered scratches without writing yet.
        int keySize = _metadata.KeyType switch
        {
            1 => _metadata.KeySlotSize,
            2 => _metadata.KeySlotSize,
            _ => ComputeVariableKeySectionSize(),
        };
        int valueSize = _metadata.ValueType switch
        {
            1 => _metadata.ValueSlotSize,
            2 => _metadata.ValueSlotSize,
            _ => ComputeVariableValueSectionSize(),
        };

        // 1) Header.
        WriteHeader(keySize, valueSize, _commonKeyPrefix);

        // 2) Keys section.
        switch (_metadata.KeyType)
        {
            case 1: WriteUniformKeys(); break;
            case 2: WriteUniformWithLenKeys(); break;
            default: WriteVariableKeys(); break;
        }

        // 3) Values section.
        switch (_metadata.ValueType)
        {
            case 1: WriteUniformValues(); break;
            case 2: WriteUniformWithLenValues(); break;
            default: WriteVariableValues(); break;
        }

        // When a section uses Variable encoding, its u16 offset table cannot
        // address bytes past 64 KiB. We've already enforced that the section
        // alone is below the cap. Cap the *whole* node at 64 KiB so any future
        // Variable-relative offset reasoning stays valid even for nodes that
        // mix Variable and non-Variable sections.
        if (_metadata.KeyType == 0 || _metadata.ValueType == 0)
        {
            int header = HeaderSize();
            int totalNodeSize = header + keySize + valueSize;
            const int MaxVariableNodeSize = 64 * 1024;
            if (totalNodeSize > MaxVariableNodeSize)
                throw new InvalidOperationException(
                    $"Index node with Variable key/value section exceeds 64 KiB ({totalNodeSize} bytes); split before finalizing.");
        }
    }

    private int HeaderSize()
    {
        int hdr = 12; // Flags(1) + KeyCount(2) + KeySize(2) + ValueSize(1) + BaseOffset(6)
        if (_commonKeyPrefix.Length > 0) hdr += 1 + _commonKeyPrefix.Length;
        return hdr;
    }

    private void WriteEmptyNode()
    {
        // Empty header: flags only (leaf/intermediate), all sizes/count = 0.
        // [Flags u8][KeyCount=0 u16][KeySize=0 u16][ValueSize=0 u8][BaseOffset=0 6 bytes]
        byte flags = (byte)(_metadata.IsIntermediate ? 0x01 : 0x00);
        Span<byte> span = _writer.GetSpan(12);
        span[0] = flags;
        span[1..12].Clear();
        _writer.Advance(12);
    }

    private int ComputeVariableKeySectionSize()
    {
        // Sentinel offset table: (count+1) u16 entries; length(i) = offsets[i+1] - offsets[i].
        int dataBytes = 0;
        int keySrc = 0;
        for (int i = 0; i < _count; i++)
        {
            int len = BinaryPrimitives.ReadUInt16LittleEndian(_keyBuf[keySrc..]);
            keySrc += 2 + len;
            dataBytes += len;
        }
        if (dataBytes > ushort.MaxValue)
            throw new InvalidOperationException("Variable section exceeds 64 KiB; offset table cannot address it");
        return dataBytes + (_count + 1) * 2;
    }

    private int ComputeVariableValueSectionSize()
    {
        int dataBytes = 0;
        int valSrc = 0;
        for (int i = 0; i < _count; i++)
        {
            int len = BinaryPrimitives.ReadUInt16LittleEndian(_valueBuf[valSrc..]);
            valSrc += 2 + len;
            dataBytes += len;
        }
        if (dataBytes > ushort.MaxValue)
            throw new InvalidOperationException("Variable section exceeds 64 KiB; offset table cannot address it");
        return dataBytes + (_count + 1) * 2;
    }

    private void WriteHeader(int keySize, int valueSize, scoped ReadOnlySpan<byte> commonKeyPrefix)
    {
        // Header fields are sized for the 64 KiB per-node cap; ValueSize is u8 since
        // per-entry value slots are 1..8 bytes for Uniform offsets (the only value
        // shape b-tree index nodes use). Reject anything beyond the encodable range
        // up-front rather than silently truncating.
        if ((uint)_count > ushort.MaxValue)
            throw new InvalidOperationException($"Index node entry count {_count} exceeds u16 header field");
        if ((uint)keySize > ushort.MaxValue)
            throw new InvalidOperationException($"Index node KeySize {keySize} exceeds u16 header field (node > 64 KiB)");
        if ((uint)valueSize > byte.MaxValue)
            throw new InvalidOperationException($"Index node ValueSize {valueSize} exceeds u8 header field");

        bool hasCommonPrefix = commonKeyPrefix.Length > 0;
        byte flags = (byte)(
            (_metadata.IsIntermediate ? 0x01 : 0x00) |
            (_metadata.KeyType << 1) |
            (_metadata.ValueType << 3) |
            (hasCommonPrefix ? 0x40 : 0x00));

        if (_metadata.BaseOffset > 0xFFFF_FFFF_FFFFUL)
            throw new InvalidOperationException(
                $"BaseOffset {_metadata.BaseOffset} exceeds 6-byte (48-bit) header field");

        // Fixed 12-byte head: [Flags u8][KeyCount u16][KeySize u16][ValueSize u8][BaseOffset 6 bytes].
        Span<byte> head = _writer.GetSpan(12);
        head[0] = flags;
        BinaryPrimitives.WriteUInt16LittleEndian(head[1..], (ushort)_count);
        BinaryPrimitives.WriteUInt16LittleEndian(head[3..], (ushort)keySize);
        head[5] = (byte)valueSize;
        ulong v = _metadata.BaseOffset;
        head[6] = (byte)v;
        head[7] = (byte)(v >> 8);
        head[8] = (byte)(v >> 16);
        head[9] = (byte)(v >> 24);
        head[10] = (byte)(v >> 32);
        head[11] = (byte)(v >> 40);
        _writer.Advance(12);

        // Optional common-prefix block: length first (forward-readable), then bytes.
        if (hasCommonPrefix)
        {
            int plen = commonKeyPrefix.Length;
            if ((uint)plen > byte.MaxValue)
                throw new InvalidOperationException($"Common key prefix length {plen} exceeds u8 header field");
            Span<byte> dst = _writer.GetSpan(plen + 1);
            dst[0] = (byte)plen;
            commonKeyPrefix.CopyTo(dst[1..]);
            _writer.Advance(plen + 1);
        }
    }

    private void WriteUniformKeys()
    {
        int keyLen = _metadata.KeySlotSize;
        int keySrc = 0;
        for (int i = 0; i < _count; i++)
        {
            keySrc += 2; // skip u16 length (known from keyLen)
            IByteBufferWriter.Copy(ref _writer, _keyBuf.Slice(keySrc, keyLen));
            keySrc += keyLen;
        }
    }

    private void WriteUniformWithLenKeys()
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
    }

    private void WriteVariableKeys()
    {
        // Sentinel offset table: count+1 u16 entries; offsets[i] is the start of
        // entry i, offsets[count] is the end of data (sentinel) so each entry's
        // length is offsets[i+1] - offsets[i] — no per-entry length prefix.
        Span<ushort> offsets = stackalloc ushort[_count + 1];
        int keySrc = 0;
        int dataOffset = 0;
        for (int i = 0; i < _count; i++)
        {
            int len = BinaryPrimitives.ReadUInt16LittleEndian(_keyBuf[keySrc..]);
            keySrc += 2 + len;
            offsets[i] = (ushort)dataOffset;
            dataOffset += len;
        }
        if (dataOffset > ushort.MaxValue)
            throw new InvalidOperationException("Variable section exceeds 64 KiB; offset table cannot address it");
        offsets[_count] = (ushort)dataOffset;

        // Write key data first.
        keySrc = 0;
        for (int i = 0; i < _count; i++)
        {
            int len = BinaryPrimitives.ReadUInt16LittleEndian(_keyBuf[keySrc..]);
            keySrc += 2;
            if (len > 0)
            {
                IByteBufferWriter.Copy(ref _writer, _keyBuf.Slice(keySrc, len));
            }
            keySrc += len;
        }

        // Then the offset table at the end of the section.
        int tableSize = (_count + 1) * 2;
        Span<byte> table = _writer.GetSpan(tableSize);
        for (int i = 0; i <= _count; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(table[(i * 2)..], offsets[i]);
        _writer.Advance(tableSize);
    }

    private void WriteUniformValues()
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
    }

    private void WriteUniformWithLenValues()
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
    }

    private void WriteVariableValues()
    {
        Span<ushort> offsets = stackalloc ushort[_count + 1];
        int valSrc = 0;
        int dataOffset = 0;
        for (int i = 0; i < _count; i++)
        {
            int len = BinaryPrimitives.ReadUInt16LittleEndian(_valueBuf[valSrc..]);
            valSrc += 2 + len;
            offsets[i] = (ushort)dataOffset;
            dataOffset += len;
        }
        if (dataOffset > ushort.MaxValue)
            throw new InvalidOperationException("Variable section exceeds 64 KiB; offset table cannot address it");
        offsets[_count] = (ushort)dataOffset;

        valSrc = 0;
        for (int i = 0; i < _count; i++)
        {
            int len = BinaryPrimitives.ReadUInt16LittleEndian(_valueBuf[valSrc..]);
            valSrc += 2;
            if (len > 0)
            {
                IByteBufferWriter.Copy(ref _writer, _valueBuf.Slice(valSrc, len));
            }
            valSrc += len;
        }

        int tableSize = (_count + 1) * 2;
        Span<byte> table = _writer.GetSpan(tableSize);
        for (int i = 0; i <= _count; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(table[(i * 2)..], offsets[i]);
        _writer.Advance(tableSize);
    }
}
