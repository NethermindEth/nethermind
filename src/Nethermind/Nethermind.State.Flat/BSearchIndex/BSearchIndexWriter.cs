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
/// Index block layout (low → high address):
///   [Values section][Keys section][BaseOffset: 6-byte LE][CommonPrefix bytes][CommonPrefixLen: u8]?
///   [ValueSize: u8][KeySize: u16 LE][KeyCount: u16 LE][Flags: u8]
///
/// The footer is fixed-width: 6 base bytes + a mandatory 6-byte BaseOffset, plus
/// an optional (1 + prefixLen) common-key-prefix block. Readers parse it
/// backwards from <c>Flags</c> with no varint decoding. <c>ValueSize</c> is u8
/// because per-entry value slots are 1..8 bytes (Uniform pointers); Variable
/// value sections are not used by index nodes.
///
/// Variable-encoded sections (KeyType/ValueType=0) use a sentinel-terminated
/// offset table of (count+1) u16 entries appended after the raw entry data;
/// length(i) = offsets[i+1] - offsets[i]. No per-entry length prefix.
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
    private readonly ReadOnlySpan<byte> _commonKeyPrefix;
    private int _count;
    private int _keyPos;    // grows forward from 0 in _keyBuf
    private int _valuePos;  // grows forward from 0 in _valueBuf

    public BSearchIndexWriter(
        ref TWriter writer,
        BSearchIndexMetadata metadata,
        Span<byte> keyBuffer,
        ReadOnlySpan<byte> commonKeyPrefix = default)
    {
        _writer = ref writer;
        _startWritten = _writer.Written;
        _metadata = metadata;
        _keyBuf = keyBuffer;
        _valueBuf = default;
        _commonKeyPrefix = commonKeyPrefix;
        _count = 0;
        _keyPos = 0;
        _valuePos = 0;
    }

    public BSearchIndexWriter(
        ref TWriter writer,
        BSearchIndexMetadata metadata,
        Span<byte> keyBuffer,
        Span<byte> valueBuffer,
        ReadOnlySpan<byte> commonKeyPrefix = default)
    {
        _writer = ref writer;
        _startWritten = _writer.Written;
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

        WriteMetadata(keySize, valueSize, _commonKeyPrefix);

        // When a section uses Variable encoding, its u16 offset table cannot
        // address bytes past 64 KiB. The per-section writer already enforces
        // that on the section itself; here we additionally cap the *total* node
        // size at 64 KiB so a node that mixes Variable + non-Variable sections
        // can never grow into a state where any future Variable-relative offset
        // would overflow. Keeps the node-size invariant tight enough that
        // callers above this layer don't have to track per-section vs
        // whole-node accounting separately.
        if (_metadata.KeyType == 0 || _metadata.ValueType == 0)
        {
            int totalNodeSize = _writer.Written - _startWritten;
            const int MaxVariableNodeSize = 64 * 1024;
            if (totalNodeSize > MaxVariableNodeSize)
                throw new InvalidOperationException(
                    $"Index node with Variable key/value section exceeds 64 KiB ({totalNodeSize} bytes); split before finalizing.");
        }
    }

    private void WriteEmptyNode()
    {
        // Empty footer: all-zero BaseOffset + sizes/count, leaf flags only.
        // [BaseOffset: 6 bytes=0][ValueSize: u8=0][KeySize: u16=0][KeyCount: u16=0][Flags: u8]
        byte flags = (byte)(_metadata.IsIntermediate ? 0x01 : 0x00);
        Span<byte> span = _writer.GetSpan(12);
        span[..11].Clear();
        span[11] = flags;
        _writer.Advance(12);
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
        // Sentinel offset table: count+1 u16 entries; offsets[i] is the start of
        // entry i, offsets[count] is the end of data (sentinel) so each entry's
        // length is offsets[i+1] - offsets[i] — no per-entry length prefix.
        int tableSize = (_count + 1) * 2;

        // Pre-compute offsets (relative to section start) by iterating key lengths.
        Span<ushort> offsets = stackalloc ushort[_count + 1];
        int keySrc = 0;
        int dataOffset = 0;
        for (int i = 0; i < _count; i++)
        {
            int len = BinaryPrimitives.ReadUInt16LittleEndian(_keyBuf[keySrc..]);
            keySrc += 2 + len;
            if (dataOffset > ushort.MaxValue)
                throw new InvalidOperationException("Variable section exceeds 64 KiB; offset table cannot address it");
            offsets[i] = (ushort)dataOffset;
            dataOffset += len;
        }
        if (dataOffset > ushort.MaxValue)
            throw new InvalidOperationException("Variable section exceeds 64 KiB; offset table cannot address it");
        offsets[_count] = (ushort)dataOffset;

        // Write key data first
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

        // Then write offset table at the end of the section
        Span<byte> table = _writer.GetSpan(tableSize);
        for (int i = 0; i <= _count; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(table[(i * 2)..], offsets[i]);
        _writer.Advance(tableSize);

        return dataOffset + tableSize;
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
        int tableSize = (_count + 1) * 2;

        // Pre-compute offsets (relative to section start)
        Span<ushort> offsets = stackalloc ushort[_count + 1];
        int valSrc = 0;
        int dataOffset = 0;
        for (int i = 0; i < _count; i++)
        {
            int len = BinaryPrimitives.ReadUInt16LittleEndian(_valueBuf[valSrc..]);
            valSrc += 2 + len;
            if (dataOffset > ushort.MaxValue)
                throw new InvalidOperationException("Variable section exceeds 64 KiB; offset table cannot address it");
            offsets[i] = (ushort)dataOffset;
            dataOffset += len;
        }
        if (dataOffset > ushort.MaxValue)
            throw new InvalidOperationException("Variable section exceeds 64 KiB; offset table cannot address it");
        offsets[_count] = (ushort)dataOffset;

        // Write value data first
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

        // Then write offset table at the end of the section
        Span<byte> table = _writer.GetSpan(tableSize);
        for (int i = 0; i <= _count; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(table[(i * 2)..], offsets[i]);
        _writer.Advance(tableSize);

        return dataOffset + tableSize;
    }

    private void WriteMetadata(int keySize, int valueSize, scoped ReadOnlySpan<byte> commonKeyPrefix)
    {
        // Footer fields are sized for the 64 KiB per-node cap; ValueSize is u8 since
        // per-entry value slots are 1..8 bytes for Uniform offsets (the only value
        // shape b-tree index nodes use). Reject anything beyond the encodable range
        // up-front rather than silently truncating on the cast below.
        if ((uint)_count > ushort.MaxValue)
            throw new InvalidOperationException($"Index node entry count {_count} exceeds u16 footer field");
        if ((uint)keySize > ushort.MaxValue)
            throw new InvalidOperationException($"Index node KeySize {keySize} exceeds u16 footer field (node > 64 KiB)");
        if ((uint)valueSize > byte.MaxValue)
            throw new InvalidOperationException($"Index node ValueSize {valueSize} exceeds u8 footer field");

        bool hasCommonPrefix = commonKeyPrefix.Length > 0;
        byte flags = (byte)(
            (_metadata.IsIntermediate ? 0x01 : 0x00) |
            (_metadata.KeyType << 1) |
            (_metadata.ValueType << 3) |
            (hasCommonPrefix ? 0x40 : 0x00));

        // BaseOffset is mandatory: a fixed 6-byte LE field (low 48 bits of the
        // ulong). Now that value slots are variable-width, the 6-byte footer cost
        // is paid once per node and the per-entry savings dwarf it.
        if (_metadata.BaseOffset > 0xFFFF_FFFF_FFFFUL)
            throw new InvalidOperationException(
                $"BaseOffset {_metadata.BaseOffset} exceeds 6-byte (48-bit) footer field");
        {
            Span<byte> bo = _writer.GetSpan(6);
            ulong v = _metadata.BaseOffset;
            bo[0] = (byte)v;
            bo[1] = (byte)(v >> 8);
            bo[2] = (byte)(v >> 16);
            bo[3] = (byte)(v >> 24);
            bo[4] = (byte)(v >> 32);
            bo[5] = (byte)(v >> 40);
            _writer.Advance(6);
        }

        // Optional common-prefix block: bytes followed by their length, so a
        // backward reader sees the length first and uses it to step past the bytes.
        if (hasCommonPrefix)
        {
            int plen = commonKeyPrefix.Length;
            if ((uint)plen > byte.MaxValue)
                throw new InvalidOperationException($"Common key prefix length {plen} exceeds u8 footer field");
            Span<byte> dst = _writer.GetSpan(plen + 1);
            commonKeyPrefix.CopyTo(dst);
            dst[plen] = (byte)plen;
            _writer.Advance(plen + 1);
        }

        // Fixed 6-byte tail: [ValueSize u8][KeySize u16][KeyCount u16][Flags u8].
        Span<byte> tail = _writer.GetSpan(6);
        tail[0] = (byte)valueSize;
        BinaryPrimitives.WriteUInt16LittleEndian(tail[1..], (ushort)keySize);
        BinaryPrimitives.WriteUInt16LittleEndian(tail[3..], (ushort)_count);
        tail[5] = flags;
        _writer.Advance(6);
    }
}
