// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Numerics;
using Nethermind.Core.Collections;
using Nethermind.Core.Utils;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Builds an HSST in the <see cref="IndexType.VarPackedArray"/> layout from
/// key-value entries with variable-length values. Every key must be exactly
/// <c>keySize</c> bytes; values may be any length (including zero). Entries
/// MUST be added in strictly ascending key order.
///
/// Binary layout (read backward from the trailing discriminator byte):
///   [Values: ValuesTotalLength bytes, concatenated with no separators]
///   [KeyOffsets: EntryCount * (KeySize + OffsetSize)]
///       Each entry: [Key: KeySize][EndOffset: OffsetSize, LE]
///       EndOffset_i is the END byte offset of value_i within Values.
///       Value_i = Values[EndOffset_{i-1} .. EndOffset_i), with EndOffset_{-1} := 0.
///   [Summary L0..L(D-1): same shape as PackedArray]
///   [Metadata: KeySize, OffsetSize, EntryCount, ValuesTotalLength,
///              EntriesPerCkLevel0Log2, RecordsPerCkHigherLog2, Depth,
///              Count_0..Count_{D-1} as LEB128]
///   [MetadataLength: u8]
///   [IndexType: u8 = 0x05]
///
/// OffsetSize is chosen at <see cref="Build"/> from ValuesTotalLength so the
/// key+offset section stays compact: 1/2/4/6 bytes (6-byte LE covers up to 256 TiB).
///
/// NOTE: this format buffers ALL keys AND per-entry end offsets in NativeMemory
/// until <see cref="Build"/>; values themselves stream straight to the writer.
/// Keys are buffered because the key+offset section is emitted AFTER the values
/// block, and OffsetSize (and hence the entry stride) isn't known until the
/// total values length is. Memory use scales with
/// <c>entryCount × (keySize + sizeof(long))</c> — independent of value sizes.
/// </summary>
public ref struct HsstVarPackedArrayBuilder<TWriter>
    where TWriter : IByteBufferWriter
{
    /// <summary>Default checkpoint stride: emit a binary-index entry every ~1 KiB of (key+offset).</summary>
    public const int DefaultBinaryIndexStrideBytes = 1024;

    private ref TWriter _writer;
    private readonly long _baseOffset;
    private readonly int _keySize;
    private readonly int _strideBytes;
    private readonly int _entriesPerCkLevel0Log2;
    private readonly int _entriesPerCkLevel0;

    // Values stream straight to the writer; only their running total length is tracked.
    // Keys and per-entry end offsets are buffered because they're emitted AFTER values
    // on disk, and OffsetSize (which sets the key+offset stride) isn't known until Build.
    private long _valuesWritten;
    private NativeMemoryListRef<long> _endOffsets;
    private NativeMemoryListRef<byte> _keysBuffer;

    private NativeMemoryListRef<byte> _prevKeyBuffer;
    private NativeMemoryListRef<byte> _checkpointKeys;

    private int _entryCount;
    private int _level0Count;

    /// <summary>
    /// Create a builder writing via <paramref name="writer"/>. <paramref name="keySize"/>
    /// fixes the key stride; subsequent <see cref="Add"/> calls validate against it.
    /// Allocates working buffers from NativeMemory — call <see cref="Dispose"/> to free.
    /// </summary>
    public HsstVarPackedArrayBuilder(ref TWriter writer, int keySize,
        int binaryIndexStrideBytes = DefaultBinaryIndexStrideBytes,
        int expectedKeyCount = 16)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(keySize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(keySize, 255);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(binaryIndexStrideBytes, 0);

        _writer = ref writer;
        _baseOffset = _writer.Written;
        _keySize = keySize;
        _strideBytes = binaryIndexStrideBytes;

        // Stride applies to the key+offset section. OffsetSize is unknown until Build();
        // estimate 4 bytes so the index density at construction matches the typical case.
        // Off-by-2x is harmless — the stride is a knob, not a correctness invariant.
        int estEntrySize = Math.Max(1, _keySize + 4);
        int rawN = Math.Max(1, _strideBytes / estEntrySize);
        _entriesPerCkLevel0Log2 = BitOperations.Log2((uint)rawN);
        _entriesPerCkLevel0 = 1 << _entriesPerCkLevel0Log2;

        _valuesWritten = 0;
        _endOffsets = new NativeMemoryListRef<long>(Math.Max(8, expectedKeyCount));
        _keysBuffer = new NativeMemoryListRef<byte>(Math.Max(64, expectedKeyCount * Math.Max(1, keySize)));
        _prevKeyBuffer = new NativeMemoryListRef<byte>(Math.Max(1, keySize));
        int checkpointSlots = Math.Max(8, expectedKeyCount / 8);
        _checkpointKeys = new NativeMemoryListRef<byte>(Math.Max(64, checkpointSlots * Math.Max(1, keySize)));

        _entryCount = 0;
        _level0Count = 0;
    }

    public void Dispose()
    {
        _endOffsets.Dispose();
        _keysBuffer.Dispose();
        _prevKeyBuffer.Dispose();
        _checkpointKeys.Dispose();
    }

    /// <summary>
    /// Append a key-value pair. <paramref name="key"/> must be exactly <c>keySize</c> bytes
    /// and strictly greater than the previous key. <paramref name="value"/> may be any length.
    /// </summary>
    public void Add(scoped ReadOnlySpan<byte> key, scoped ReadOnlySpan<byte> value)
    {
        if (key.Length != _keySize)
            throw new ArgumentException($"key length {key.Length} != keySize {_keySize}", nameof(key));

        if (_entryCount > 0 && key.SequenceCompareTo(_prevKeyBuffer.AsSpan()) <= 0)
            throw new InvalidOperationException("Keys must be added in strictly ascending order.");

        if (value.Length > 0) IByteBufferWriter.Copy(ref _writer, value);
        _valuesWritten += value.Length;
        _endOffsets.Add(_valuesWritten);
        if (_keySize > 0) _keysBuffer.AddRange(key);

        _entryCount++;

        _prevKeyBuffer.Clear();
        _prevKeyBuffer.AddRange(key);

        // Emit checkpoint at exact entries-per-ck boundaries (power-of-two mask).
        if ((_entryCount & (_entriesPerCkLevel0 - 1)) == 0)
        {
            if (_keySize > 0) _checkpointKeys.AddRange(key);
            _level0Count++;
        }
    }

    /// <summary>
    /// Finalize the HSST: emits Values, KeyOffsets, recursive summary levels, Metadata,
    /// MetadataLength, and the trailing IndexType discriminator byte.
    /// </summary>
    public void Build()
    {
        long valuesTotal = _valuesWritten;
        int offsetSize = HsstOffset.ChooseOffsetSize(valuesTotal);

        // Tail checkpoint covers the last entry when count isn't a multiple of the stride.
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

        // Build summary levels in memory; identical to PackedArray (summaries are key-only).
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
                    throw new InvalidOperationException($"VarPackedArray summary depth exceeded {HsstPackedArrayLayout.MaxSummaryDepth}.");

                higherLevelStartRec.Add(newLevelStartRec);
                levelCounts.Add(newCount);

                prevStartRec = newLevelStartRec;
                prevCount = newCount;
                prevIsLevel0 = false;

                if (newCount <= 1) break;
            }
        }

        int depth = levelCounts.Count;

        // Values were already streamed during Add; emit the KeyOffsets section now.
        ReadOnlySpan<byte> keysSpan = _keysBuffer.AsSpan();
        Span<byte> offsetBuf = stackalloc byte[8];
        for (int i = 0; i < _entryCount; i++)
        {
            if (_keySize > 0)
                IByteBufferWriter.Copy(ref _writer, keysSpan.Slice(i * _keySize, _keySize));
            BinaryPrimitives.WriteUInt64LittleEndian(offsetBuf, (ulong)_endOffsets[i]);
            IByteBufferWriter.Copy(ref _writer, offsetBuf[..offsetSize]);
        }

        // Flush summary levels.
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

        // Metadata.
        long metaStart = _writer.Written;
        WriteLeb128(_keySize);
        WriteLeb128(offsetSize);
        WriteLeb128(_entryCount);
        WriteLeb128Long(valuesTotal);
        WriteLeb128(_entriesPerCkLevel0Log2);
        WriteLeb128(recordsPerCkHigherLog2);
        WriteLeb128(depth);
        for (int i = 0; i < depth; i++) WriteLeb128(levelCounts[i]);
        int metaLen = checked((int)(_writer.Written - metaStart));
        if (metaLen > 255)
            throw new InvalidOperationException("VarPackedArray metadata exceeds 255 bytes.");

        Span<byte> trail = _writer.GetSpan(2);
        trail[0] = (byte)metaLen;
        trail[1] = (byte)IndexType.VarPackedArray;
        _writer.Advance(2);
    }

    private static int ChooseOffsetSize(long valuesTotal)
    {
        if (valuesTotal <= byte.MaxValue) return 1;
        if (valuesTotal <= ushort.MaxValue) return 2;
        if (valuesTotal <= uint.MaxValue) return 4;
        if (valuesTotal <= (1L << 48) - 1) return 6;
        throw new InvalidOperationException("VarPackedArray total value size exceeds 256 TiB.");
    }

    private void WriteLeb128(int value)
    {
        Span<byte> buf = _writer.GetSpan(5);
        int len = Leb128.Write(buf, 0, value);
        _writer.Advance(len);
    }

    /// <summary>
    /// Long-valued LEB128 writer for <c>ValuesTotalLength</c> — int Leb128 only covers
    /// 32 bits, but VarPackedArray's value section can in principle reach 48 bits.
    /// </summary>
    private void WriteLeb128Long(long value)
    {
        Span<byte> buf = _writer.GetSpan(10);
        ulong v = (ulong)value;
        int pos = 0;
        while (v >= 0x80)
        {
            buf[pos++] = (byte)(v | 0x80);
            v >>= 7;
        }
        buf[pos++] = (byte)v;
        _writer.Advance(pos);
    }
}
