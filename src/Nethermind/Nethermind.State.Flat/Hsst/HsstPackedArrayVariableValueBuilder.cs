// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Numerics;
using Nethermind.Core.Collections;
using Nethermind.Core.Utils;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Builds an HSST in the <see cref="IndexType.PackedArrayVariableValue"/> layout:
/// fixed-size keys with variable-size values. Each entry uses the same data-section
/// format as <see cref="IndexType.BTree"/>
/// (<c>[Value][ValueLength: LEB128][KeyLength: u8][FullKey]</c>) so each entry's
/// MetadataStart is interchangeable with the BTree noderef mechanism. Entries MUST
/// be added in strictly ascending key order.
///
/// Binary layout (low → high; trailing discriminator byte read first):
///   [Entries        : per entry, [Value][ValueLength: LEB128][KeyLength: u8][FullKey]]
///   [EntryMetaStarts: EntryCount × u32 LE]   -- absolute MetadataStart, byte 0 of HSST
///   [Summary L0..L(D-1)]                     -- Count_i × KeySize each
///   [HashTable      : 4 × TableSize bytes]   -- omitted when TableSize == 0;
///                                               slot value = MetadataStart, BTreeHashIndex-compatible
///   [Metadata       : KeySize, EntryCount, TableSize, EntriesPerCkLevel0Log2,
///                     RecordsPerCkHigherLog2, EntriesByteLen, Depth,
///                     Count_0..Count_{D-1} as LEB128]
///   [MetadataLength : u8]
///   [IndexType      : u8 = 0x0A]
///
/// Streaming: values are written directly through the writer as they arrive — only the
/// <c>EntryMetaStarts</c> uint array (4 B per entry), the summary checkpoint keys, and
/// per-entry hashes are buffered. The summary geometry mirrors PackedArray, but the
/// level-0 stride is computed from <c>strideBytes / KeySize</c> (not from a fixed
/// entry size) since values are unbounded.
/// </summary>
public ref struct HsstPackedArrayVariableValueBuilder<TWriter>
    where TWriter : IByteBufferWriter
{
    /// <summary>Default checkpoint stride: emit a binary-index entry every ~1 KiB of key bytes.</summary>
    public const int DefaultBinaryIndexStrideBytes = 1024;

    /// <summary>Hash table is sized so its load factor stays at or below this value.</summary>
    private const double HashTableTargetUtilization = 0.75;

    private const uint HashEmpty = 0u;
    private const uint HashCollision = 0xFFFFFFFFu;

    private ref TWriter _writer;
    private readonly int _baseOffset;
    private readonly int _keySize;
    private readonly int _strideBytes;
    private readonly bool _useHashIndex;
    private readonly int _entriesPerCkLevel0Log2;
    private readonly int _entriesPerCkLevel0;

    private NativeMemoryListRef<byte> _prevKeyBuffer;
    private NativeMemoryListRef<byte> _checkpointKeys;
    private NativeMemoryListRef<uint> _entryHashes;
    private NativeMemoryListRef<uint> _entryMetaStarts;

    private int _entryCount;
    private int _level0Count;
    private int _writtenBeforeValue;

    public HsstPackedArrayVariableValueBuilder(ref TWriter writer, int keySize,
        int binaryIndexStrideBytes = DefaultBinaryIndexStrideBytes,
        int expectedKeyCount = 16,
        bool useHashIndex = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(keySize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(keySize, 255);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(binaryIndexStrideBytes, 0);

        _writer = ref writer;
        _baseOffset = _writer.Written;
        _keySize = keySize;
        _strideBytes = binaryIndexStrideBytes;
        _useHashIndex = useHashIndex;
        // Anchor level-0 stride on key byte cost only; values are unbounded so they
        // can't participate in the entry-size denominator. Round down to a power of
        // two so the reader uses mask + shift in place of divide/multiply.
        int rawN = Math.Max(1, _strideBytes / Math.Max(1, _keySize));
        _entriesPerCkLevel0Log2 = BitOperations.Log2((uint)rawN);
        _entriesPerCkLevel0 = 1 << _entriesPerCkLevel0Log2;

        _prevKeyBuffer = new NativeMemoryListRef<byte>(Math.Max(1, keySize));
        int checkpointSlots = Math.Max(8, expectedKeyCount / 8);
        _checkpointKeys = new NativeMemoryListRef<byte>(Math.Max(64, checkpointSlots * Math.Max(1, keySize)));
        _entryHashes = useHashIndex ? new NativeMemoryListRef<uint>(expectedKeyCount) : default;
        _entryMetaStarts = new NativeMemoryListRef<uint>(expectedKeyCount);

        _entryCount = 0;
        _level0Count = 0;
        _writtenBeforeValue = 0;
    }

    public void Dispose()
    {
        _prevKeyBuffer.Dispose();
        _checkpointKeys.Dispose();
        if (_useHashIndex) _entryHashes.Dispose();
        _entryMetaStarts.Dispose();
    }

    /// <summary>
    /// Begin a streaming value write. Returns ref to the shared writer; caller appends
    /// the value bytes and then calls <see cref="FinishValueWrite"/> with the matching key.
    /// Mirrors the BTree builder's begin/finish split so callers writing inner HSSTs in
    /// place can stream into the value bytes directly.
    /// </summary>
    public ref TWriter BeginValueWrite()
    {
        _writtenBeforeValue = _writer.Written;
        return ref _writer;
    }

    /// <summary>
    /// Finalise the current value with the given key. Writes the BTree entry trailer
    /// (<c>[ValueLength: LEB128][KeyLength: u8][FullKey]</c>) and records the
    /// MetadataStart anchor for this entry. Key length must equal <c>KeySize</c> and
    /// be strictly greater than the previous key.
    /// </summary>
    public void FinishValueWrite(scoped ReadOnlySpan<byte> key)
    {
        if (key.Length != _keySize)
            throw new ArgumentException($"key length {key.Length} != keySize {_keySize}", nameof(key));

        if (_entryCount > 0 && key.SequenceCompareTo(_prevKeyBuffer.AsSpan()) <= 0)
            throw new InvalidOperationException("Keys must be added in strictly ascending order.");

        int valueLen = _writer.Written - _writtenBeforeValue;
        long metaAbs = _writer.Written - _baseOffset;
        // Slot encoding (BTreeHashIndex-compatible) caps MetadataStart at 4 GiB.
        if (metaAbs > uint.MaxValue)
            throw new InvalidOperationException("PackedArrayVariableValue MetadataStart exceeds 4 GiB; use plain BTree.");

        // [ValueLength: LEB128][KeyLength: u8][FullKey] — MetadataStart points at the LEB128.
        Span<byte> leb = _writer.GetSpan(5);
        int lebLen = Leb128.Write(leb, 0, valueLen);
        _writer.Advance(lebLen);

        Span<byte> kl = _writer.GetSpan(1);
        kl[0] = (byte)_keySize;
        _writer.Advance(1);

        if (_keySize > 0) IByteBufferWriter.Copy(ref _writer, key);

        _entryMetaStarts.Add((uint)metaAbs);
        if (_useHashIndex) _entryHashes.Add(HsstHash.HashKey(key));

        _entryCount++;

        _prevKeyBuffer.Clear();
        _prevKeyBuffer.AddRange(key);

        // Emit at exact entries-per-ck boundaries so reader can derive slab bounds.
        if ((_entryCount & (_entriesPerCkLevel0 - 1)) == 0)
        {
            if (_keySize > 0) _checkpointKeys.AddRange(key);
            _level0Count++;
        }
    }

    /// <summary>
    /// Convenience: write key + value in one call.
    /// </summary>
    public void Add(scoped ReadOnlySpan<byte> key, scoped ReadOnlySpan<byte> value)
    {
        BeginValueWrite();
        if (value.Length > 0) IByteBufferWriter.Copy(ref _writer, value);
        FinishValueWrite(key);
    }

    /// <summary>
    /// Finalise the HSST: emits EntryMetaStarts, summary levels, optional HashTable,
    /// Metadata, MetadataLength, and the trailing IndexType byte.
    /// </summary>
    public void Build()
    {
        // Tail checkpoint when entry count is not a multiple of the level-0 stride.
        if (_entryCount > 0 && (_entryCount & (_entriesPerCkLevel0 - 1)) != 0)
        {
            if (_keySize > 0) _checkpointKeys.AddRange(_prevKeyBuffer.AsSpan());
            _level0Count++;
        }

        int recordsPerCkHigherLog2 = 0;
        int recordsPerCkHigher = 0;
        if (_keySize > 0)
        {
            int rawM = Math.Max(2, _strideBytes / _keySize);
            recordsPerCkHigherLog2 = BitOperations.Log2((uint)rawM);
            if (recordsPerCkHigherLog2 < 1) recordsPerCkHigherLog2 = 1;
            recordsPerCkHigher = 1 << recordsPerCkHigherLog2;
        }

        // Build all summary levels in memory first, then flush them in order.
        using NativeMemoryListRef<int> levelCounts = new(HsstPackedArrayLayout.MaxSummaryDepth);
        if (_level0Count > 0) levelCounts.Add(_level0Count);

        using NativeMemoryListRef<byte> higherLevelsKeys = new(64);
        using NativeMemoryListRef<int> higherLevelStartRec = new(HsstPackedArrayLayout.MaxSummaryDepth);

        int prevStartRec = -1;
        int prevCount = _level0Count;
        bool prevIsLevel0 = true;

        if (recordsPerCkHigher >= 2)
        {
            while (prevCount > 1)
            {
                ReadOnlySpan<byte> prevKeys = prevIsLevel0
                    ? _checkpointKeys.AsSpan()
                    : higherLevelsKeys.AsSpan().Slice(prevStartRec * _keySize, prevCount * _keySize);

                int newLevelStartRec = higherLevelsKeys.Count / _keySize;
                int newCount = 0;

                for (int i = recordsPerCkHigher - 1; i < prevCount; i += recordsPerCkHigher)
                {
                    higherLevelsKeys.AddRange(prevKeys.Slice(i * _keySize, _keySize));
                    newCount++;
                }
                int lastEmittedIdx = (newCount << recordsPerCkHigherLog2) - 1;
                if (lastEmittedIdx != prevCount - 1)
                {
                    int i = prevCount - 1;
                    higherLevelsKeys.AddRange(prevKeys.Slice(i * _keySize, _keySize));
                    newCount++;
                }

                if (newCount == 0 || newCount >= prevCount)
                {
                    higherLevelsKeys.Truncate(newLevelStartRec * _keySize);
                    break;
                }

                if (levelCounts.Count >= HsstPackedArrayLayout.MaxSummaryDepth)
                    throw new InvalidOperationException($"PackedArrayVariableValue summary depth exceeded {HsstPackedArrayLayout.MaxSummaryDepth}.");

                higherLevelStartRec.Add(newLevelStartRec);
                levelCounts.Add(newCount);

                prevStartRec = newLevelStartRec;
                prevCount = newCount;
                prevIsLevel0 = false;

                if (newCount <= 1) break;
            }
        }

        int depth = levelCounts.Count;
        int entriesByteLen = _writer.Written - _baseOffset;

        // EntryMetaStarts: EntryCount × u32 LE.
        for (int i = 0; i < _entryCount; i++)
        {
            Span<byte> dst = _writer.GetSpan(4);
            BinaryPrimitives.WriteUInt32LittleEndian(dst, _entryMetaStarts[i]);
            _writer.Advance(4);
        }

        // Flush level 0 then higher levels.
        if (_level0Count > 0)
        {
            ReadOnlySpan<byte> ckKeys = _checkpointKeys.AsSpan();
            for (int i = 0; i < _level0Count; i++)
            {
                if (_keySize > 0)
                    IByteBufferWriter.Copy(ref _writer, ckKeys.Slice(i * _keySize, _keySize));
            }
        }
        ReadOnlySpan<byte> hlKeys = higherLevelsKeys.AsSpan();
        for (int lvl = 1; lvl < depth; lvl++)
        {
            int startRec = higherLevelStartRec[lvl - 1];
            int count = levelCounts[lvl];
            for (int i = 0; i < count; i++)
            {
                int rec = startRec + i;
                if (_keySize > 0)
                    IByteBufferWriter.Copy(ref _writer, hlKeys.Slice(rec * _keySize, _keySize));
            }
        }

        int tableSize = 0;
        if (_useHashIndex && _entryCount > 0)
        {
            tableSize = HsstHash.BucketCount(_entryCount, HashTableTargetUtilization);
            EmitHashTable(tableSize);
        }

        int metaStart = _writer.Written;
        WriteLeb128(_keySize);
        WriteLeb128(_entryCount);
        WriteLeb128(tableSize);
        WriteLeb128(_entriesPerCkLevel0Log2);
        WriteLeb128(recordsPerCkHigherLog2);
        WriteLeb128(entriesByteLen);
        WriteLeb128(depth);
        for (int i = 0; i < depth; i++) WriteLeb128(levelCounts[i]);
        int metaLen = _writer.Written - metaStart;
        if (metaLen > 255)
            throw new InvalidOperationException("PackedArrayVariableValue metadata exceeds 255 bytes.");

        Span<byte> trail = _writer.GetSpan(2);
        trail[0] = (byte)metaLen;
        trail[1] = (byte)IndexType.PackedArrayVariableValue;
        _writer.Advance(2);
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
            // Slot stores MetadataStart (BTreeHashIndex-compatible). 0 = empty,
            // 0xFFFFFFFF = collision sentinel; on either, the reader falls back
            // to summary descent.
            uint meta = _entryMetaStarts[i];
            slots[(int)slot] = slots[(int)slot] == HashEmpty ? meta : HashCollision;
        }

        for (int i = 0; i < tableSize; i++)
        {
            Span<byte> dst = _writer.GetSpan(4);
            BinaryPrimitives.WriteUInt32LittleEndian(dst, slots[i]);
            _writer.Advance(4);
        }
    }
}
