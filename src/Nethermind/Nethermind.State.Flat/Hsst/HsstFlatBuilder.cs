// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Numerics;
using Nethermind.Core.Collections;
using Nethermind.Core.Utils;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Builds an HSST in the <see cref="IndexType.FlatEntries"/> layout from key-value entries.
/// Every key must be exactly <c>keySize</c> bytes and every value exactly <c>valueSize</c>
/// bytes. Entries MUST be added in strictly ascending key order.
///
/// Binary layout (read backward from the trailing discriminator byte):
///   [Data: EntryCount * (KeySize+ValueSize)]
///   [BinaryIndex: IndexCount * (KeySize+4)]   // [CheckpointKey][LastEntryIndex: u32 LE]
///   [HashIndex: 2^L * 4 bytes]                // 0=empty, 0xFFFFFFFF=collision, else entryIdx+1
///   [TableSizeLog2: u8]
///   [Metadata: KeySize, ValueSize, EntryCount, IndexCount as LEB128]
///   [MetadataLength: u8]
///   [IndexType: u8 = 0x04]
///
/// Lookup walks the binary index first (top-level binary search) to narrow the candidate
/// range to ~one stride of bytes, then does a second binary search within that range.
/// The hash index is consulted in parallel for an O(1) exact-match fast path.
/// </summary>
public ref struct HsstFlatBuilder<TWriter>
    where TWriter : IByteBufferWriter
{
    /// <summary>Default checkpoint stride: emit a binary-index entry every ~1 KiB of (key+value).</summary>
    public const int DefaultBinaryIndexStrideBytes = 1024;

    /// <summary>Hash table is sized so its load factor stays at or below this value.</summary>
    private const double HashTableTargetUtilization = 0.75;

    private const uint HashEmpty = 0u;
    private const uint HashCollision = 0xFFFFFFFFu;

    private ref TWriter _writer;
    private readonly int _baseOffset;
    private readonly int _keySize;
    private readonly int _valueSize;
    private readonly int _strideBytes;

    private NativeMemoryListRef<byte> _prevKeyBuffer;
    private NativeMemoryListRef<byte> _checkpointKeys;
    private NativeMemoryListRef<int> _checkpointIndices;
    private NativeMemoryListRef<uint> _entryHashes;

    private int _entryCount;
    private int _bytesSinceLastCheckpoint;
    private int _entryIndexAtLastCheckpoint;

    /// <summary>
    /// Create a builder writing via <paramref name="writer"/>. <paramref name="keySize"/> /
    /// <paramref name="valueSize"/> set the fixed entry stride; subsequent
    /// <see cref="Add"/> calls validate against them. Allocates working buffers from
    /// NativeMemory — call <see cref="Dispose"/> to free.
    /// </summary>
    public HsstFlatBuilder(ref TWriter writer, int keySize, int valueSize,
        int binaryIndexStrideBytes = DefaultBinaryIndexStrideBytes,
        int expectedKeyCount = 16)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(keySize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(keySize, 255);
        ArgumentOutOfRangeException.ThrowIfNegative(valueSize);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(binaryIndexStrideBytes, 0);

        _writer = ref writer;
        _baseOffset = _writer.Written;
        _keySize = keySize;
        _valueSize = valueSize;
        _strideBytes = binaryIndexStrideBytes;

        _prevKeyBuffer = new NativeMemoryListRef<byte>(Math.Max(1, keySize));
        // One checkpoint per stride; size lower bound is keySize bytes.
        int checkpointSlots = Math.Max(8, expectedKeyCount / 8);
        _checkpointKeys = new NativeMemoryListRef<byte>(Math.Max(64, checkpointSlots * Math.Max(1, keySize)));
        _checkpointIndices = new NativeMemoryListRef<int>(checkpointSlots);
        _entryHashes = new NativeMemoryListRef<uint>(expectedKeyCount);

        _entryCount = 0;
        _bytesSinceLastCheckpoint = 0;
        _entryIndexAtLastCheckpoint = -1;
    }

    public void Dispose()
    {
        _prevKeyBuffer.Dispose();
        _checkpointKeys.Dispose();
        _checkpointIndices.Dispose();
        _entryHashes.Dispose();
    }

    /// <summary>
    /// Append a key-value pair. <paramref name="key"/> must be exactly <c>keySize</c> bytes,
    /// <paramref name="value"/> exactly <c>valueSize</c> bytes, and strictly greater than the
    /// previous key.
    /// </summary>
    public void Add(scoped ReadOnlySpan<byte> key, scoped ReadOnlySpan<byte> value)
    {
        if (key.Length != _keySize)
            throw new ArgumentException($"key length {key.Length} != keySize {_keySize}", nameof(key));
        if (value.Length != _valueSize)
            throw new ArgumentException($"value length {value.Length} != valueSize {_valueSize}", nameof(value));

        if (_entryCount > 0 && key.SequenceCompareTo(_prevKeyBuffer.AsSpan()) <= 0)
            throw new InvalidOperationException("Keys must be added in strictly ascending order.");

        if (_keySize > 0) IByteBufferWriter.Copy(ref _writer, key);
        if (_valueSize > 0) IByteBufferWriter.Copy(ref _writer, value);

        _entryHashes.Add(HsstHash.HashKey(key));

        _bytesSinceLastCheckpoint += _keySize + _valueSize;
        _entryCount++;

        _prevKeyBuffer.Clear();
        _prevKeyBuffer.AddRange(key);

        if (_bytesSinceLastCheckpoint >= _strideBytes)
        {
            EmitCheckpoint(key, _entryCount - 1);
            _bytesSinceLastCheckpoint = 0;
        }
    }

    /// <summary>
    /// Finalize the HSST: emits BinaryIndex, HashIndex, Metadata, MetadataLength, and the
    /// trailing IndexType discriminator byte. The writer is advanced past all of them.
    /// </summary>
    public void Build()
    {
        // Always include a final checkpoint covering the last entry. Without it a target key
        // greater than every checkpoint key would have an empty candidate range.
        if (_entryCount > 0 && _entryIndexAtLastCheckpoint != _entryCount - 1)
        {
            EmitCheckpoint(_prevKeyBuffer.AsSpan(), _entryCount - 1);
        }

        int indexCount = _checkpointIndices.Count;
        ReadOnlySpan<byte> ckKeys = _checkpointKeys.AsSpan();
        ReadOnlySpan<int> ckIdx = _checkpointIndices.AsSpan();
        for (int i = 0; i < indexCount; i++)
        {
            if (_keySize > 0)
                IByteBufferWriter.Copy(ref _writer, ckKeys.Slice(i * _keySize, _keySize));
            Span<byte> idxBuf = _writer.GetSpan(4);
            BinaryPrimitives.WriteInt32LittleEndian(idxBuf, ckIdx[i]);
            _writer.Advance(4);
        }

        int log2 = EmitHashTable();

        Span<byte> log2Span = _writer.GetSpan(1);
        log2Span[0] = (byte)log2;
        _writer.Advance(1);

        int metaStart = _writer.Written;
        WriteLeb128(_keySize);
        WriteLeb128(_valueSize);
        WriteLeb128(_entryCount);
        WriteLeb128(indexCount);
        int metaLen = _writer.Written - metaStart;
        if (metaLen > 255)
            throw new InvalidOperationException("FlatEntries metadata exceeds 255 bytes.");

        Span<byte> trail = _writer.GetSpan(2);
        trail[0] = (byte)metaLen;
        trail[1] = (byte)IndexType.FlatEntries;
        _writer.Advance(2);
    }

    private void EmitCheckpoint(scoped ReadOnlySpan<byte> key, int entryIdx)
    {
        if (_keySize > 0) _checkpointKeys.AddRange(key);
        _checkpointIndices.Add(entryIdx);
        _entryIndexAtLastCheckpoint = entryIdx;
    }

    private void WriteLeb128(int value)
    {
        Span<byte> buf = _writer.GetSpan(5);
        int len = Leb128.Write(buf, 0, value);
        _writer.Advance(len);
    }

    private int EmitHashTable()
    {
        int n = _entryCount;
        // Smallest power-of-two table size satisfying load factor ≤ target. Empty HSSTs still
        // emit a single-slot table so the reader path is uniform.
        long required = n == 0 ? 1 : (long)Math.Ceiling(n / HashTableTargetUtilization);
        if (required < 1) required = 1;
        int log2 = required <= 1 ? 0 : (32 - BitOperations.LeadingZeroCount((uint)(required - 1)));
        if (log2 > 31) throw new InvalidOperationException("Hash index table size too large.");
        int tableSize = 1 << log2;
        uint mask = (uint)(tableSize - 1);

        using NativeMemoryListRef<uint> table = new(tableSize, tableSize);
        Span<uint> slots = table.AsSpan();
        ReadOnlySpan<uint> hashes = _entryHashes.AsSpan();

        for (int i = 0; i < n; i++)
        {
            uint slot = hashes[i] & mask;
            // Slot stores 1-based entry index so 0 stays the unambiguous empty sentinel.
            slots[(int)slot] = slots[(int)slot] == HashEmpty ? (uint)(i + 1) : HashCollision;
        }

        for (int i = 0; i < tableSize; i++)
        {
            Span<byte> dst = _writer.GetSpan(4);
            BinaryPrimitives.WriteUInt32LittleEndian(dst, slots[i]);
            _writer.Advance(4);
        }

        return log2;
    }
}
