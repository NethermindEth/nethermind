// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
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
///   [Summary L0: Count_0 * (KeySize+4)]
///   [Summary L1: Count_1 * (KeySize+4)]
///   ...
///   [Summary L(D-1): Count_{D-1} * (KeySize+4)]
///   [HashTable: 4 * TableSize bytes]   (omitted when TableSize == 0)
///   [Metadata: KeySize, ValueSize, EntryCount, TableSize, Depth, Count_0..Count_{D-1} as LEB128]
///   [MetadataLength: u8]
///   [IndexType: u8 = 0x06]
///
/// Each summary level uses the same `[CheckpointKey][LastEntryIndex: u32 LE]` record;
/// level 0 indexes into Data, level k+1 indexes into level k. The hash table is optional
/// (controlled by the <c>useHashIndex</c> ctor flag); when enabled, the slot for a key is
/// computed via Lemire's multiply-shift reduction so the table need not be a power of two.
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
    private readonly bool _useHashIndex;

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
        int expectedKeyCount = 16,
        bool useHashIndex = true)
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
        _useHashIndex = useHashIndex;

        _prevKeyBuffer = new NativeMemoryListRef<byte>(Math.Max(1, keySize));
        // One checkpoint per stride; size lower bound is keySize bytes.
        int checkpointSlots = Math.Max(8, expectedKeyCount / 8);
        _checkpointKeys = new NativeMemoryListRef<byte>(Math.Max(64, checkpointSlots * Math.Max(1, keySize)));
        _checkpointIndices = new NativeMemoryListRef<int>(checkpointSlots);
        _entryHashes = useHashIndex ? new NativeMemoryListRef<uint>(expectedKeyCount) : default;

        _entryCount = 0;
        _bytesSinceLastCheckpoint = 0;
        _entryIndexAtLastCheckpoint = -1;
    }

    public void Dispose()
    {
        _prevKeyBuffer.Dispose();
        _checkpointKeys.Dispose();
        _checkpointIndices.Dispose();
        if (_useHashIndex) _entryHashes.Dispose();
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

        if (_useHashIndex) _entryHashes.Add(HsstHash.HashKey(key));

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
    /// Finalize the HSST: emits the recursive summary levels, optional HashTable, Metadata,
    /// MetadataLength, and the trailing IndexType discriminator byte.
    /// </summary>
    public void Build()
    {
        // Always include a final checkpoint covering the last entry. Without it a target key
        // greater than every checkpoint key would have an empty candidate range.
        if (_entryCount > 0 && _entryIndexAtLastCheckpoint != _entryCount - 1)
        {
            EmitCheckpoint(_prevKeyBuffer.AsSpan(), _entryCount - 1);
        }

        int entrySize = _keySize + 4;

        // Build all summary levels in memory first, then flush them in order to the writer.
        // Level 0 is already accumulated in _checkpointKeys / _checkpointIndices.
        using NativeMemoryListRef<int> levelCounts = new(HsstFlatLayout.MaxSummaryDepth);

        int level0Count = _checkpointIndices.Count;
        if (level0Count > 0) levelCounts.Add(level0Count);

        // Higher levels: each summary entry covers a stride-sized window of the level below.
        // We collect them into a single staging buffer plus per-level (startRec) pointers.
        using NativeMemoryListRef<byte> higherLevelsKeys = new(64);
        using NativeMemoryListRef<int> higherLevelsIdx = new(8);
        using NativeMemoryListRef<int> higherLevelStartRec = new(HsstFlatLayout.MaxSummaryDepth);

        // Track the previous level by (startRec, count, fromLevel0) so we re-fetch its span
        // each iteration — adding to higherLevels* may move the underlying NativeMemory.
        int prevStartRec = -1;
        int prevCount = _checkpointIndices.Count;
        bool prevIsLevel0 = true;

        while (prevCount > 1)
        {
            ReadOnlySpan<byte> prevKeys = prevIsLevel0
                ? _checkpointKeys.AsSpan()
                : higherLevelsKeys.AsSpan().Slice(prevStartRec * _keySize, prevCount * _keySize);

            int newLevelStartRec = higherLevelsIdx.Count;

            int bytesAccumulated = 0;
            int lastEmittedIdx = -1;
            for (int i = 0; i < prevCount; i++)
            {
                bytesAccumulated += entrySize;
                if (bytesAccumulated >= _strideBytes)
                {
                    if (_keySize > 0) higherLevelsKeys.AddRange(prevKeys.Slice(i * _keySize, _keySize));
                    higherLevelsIdx.Add(i);
                    lastEmittedIdx = i;
                    bytesAccumulated = 0;
                }
            }
            // Final summary entry: covers the tail of the previous level.
            if (lastEmittedIdx != prevCount - 1)
            {
                int i = prevCount - 1;
                if (_keySize > 0) higherLevelsKeys.AddRange(prevKeys.Slice(i * _keySize, _keySize));
                higherLevelsIdx.Add(i);
            }

            int newCount = higherLevelsIdx.Count - newLevelStartRec;
            if (newCount == 0 || newCount >= prevCount)
            {
                // No reduction — drop this level and bail out.
                higherLevelsKeys.Truncate(newLevelStartRec * _keySize);
                higherLevelsIdx.Truncate(newLevelStartRec);
                break;
            }

            if (levelCounts.Count >= HsstFlatLayout.MaxSummaryDepth)
                throw new InvalidOperationException($"FlatEntries summary depth exceeded {HsstFlatLayout.MaxSummaryDepth}.");

            higherLevelStartRec.Add(newLevelStartRec);
            levelCounts.Add(newCount);

            // Promote: prev is now this just-built level.
            prevStartRec = newLevelStartRec;
            prevCount = newCount;
            prevIsLevel0 = false;

            if (newCount <= 1) break;
        }

        int depth = levelCounts.Count;

        // Flush level 0 to the writer.
        if (level0Count > 0)
        {
            ReadOnlySpan<byte> ckKeys = _checkpointKeys.AsSpan();
            ReadOnlySpan<int> ckIdx = _checkpointIndices.AsSpan();
            for (int i = 0; i < level0Count; i++)
            {
                if (_keySize > 0)
                    IByteBufferWriter.Copy(ref _writer, ckKeys.Slice(i * _keySize, _keySize));
                Span<byte> idxBuf = _writer.GetSpan(4);
                BinaryPrimitives.WriteInt32LittleEndian(idxBuf, ckIdx[i]);
                _writer.Advance(4);
            }
        }

        // Flush levels 1..depth-1 in order from the staging buffer.
        ReadOnlySpan<byte> hlKeys = higherLevelsKeys.AsSpan();
        ReadOnlySpan<int> hlIdx = higherLevelsIdx.AsSpan();
        for (int lvl = 1; lvl < depth; lvl++)
        {
            int startRec = higherLevelStartRec[lvl - 1];
            int count = levelCounts[lvl];
            for (int i = 0; i < count; i++)
            {
                int rec = startRec + i;
                if (_keySize > 0)
                    IByteBufferWriter.Copy(ref _writer, hlKeys.Slice(rec * _keySize, _keySize));
                Span<byte> idxBuf = _writer.GetSpan(4);
                BinaryPrimitives.WriteInt32LittleEndian(idxBuf, hlIdx[rec]);
                _writer.Advance(4);
            }
        }

        // Optional hash table.
        int tableSize = 0;
        if (_useHashIndex && _entryCount > 0)
        {
            tableSize = HsstHash.BucketCount(_entryCount, HashTableTargetUtilization);
            EmitHashTable(tableSize);
        }

        int metaStart = _writer.Written;
        WriteLeb128(_keySize);
        WriteLeb128(_valueSize);
        WriteLeb128(_entryCount);
        WriteLeb128(tableSize);
        WriteLeb128(depth);
        for (int i = 0; i < depth; i++) WriteLeb128(levelCounts[i]);
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

    private void EmitHashTable(int tableSize)
    {
        int n = _entryCount;
        using NativeMemoryListRef<uint> table = new(tableSize, tableSize);
        Span<uint> slots = table.AsSpan();
        ReadOnlySpan<uint> hashes = _entryHashes.AsSpan();

        for (int i = 0; i < n; i++)
        {
            uint slot = HsstHash.Slot(hashes[i], tableSize);
            // Slot stores 1-based entry index so 0 stays the unambiguous empty sentinel.
            slots[(int)slot] = slots[(int)slot] == HashEmpty ? (uint)(i + 1) : HashCollision;
        }

        for (int i = 0; i < tableSize; i++)
        {
            Span<byte> dst = _writer.GetSpan(4);
            BinaryPrimitives.WriteUInt32LittleEndian(dst, slots[i]);
            _writer.Advance(4);
        }
    }
}
