// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Nethermind.Core.Collections;
using Nethermind.Core.Utils;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Builds an HSST in the <see cref="IndexType.PackedArray"/> layout from key-value entries.
/// Every key must be exactly <c>keySize</c> bytes and every value exactly <c>valueSize</c>
/// bytes. Entries MUST be added in strictly ascending key order.
///
/// Binary layout (read backward from the trailing discriminator byte):
///   [Data: EntryCount * (KeySize+ValueSize)]
///   [Summary L0: Count_0 * KeySize]
///   [Summary L1: Count_1 * KeySize]
///   ...
///   [Summary L(D-1): Count_{D-1} * KeySize]
///   [Metadata: KeySize, ValueSize, EntryCount, EntriesPerCkLevel0,
///              RecordsPerCkHigher, Depth, Count_0..Count_{D-1} as LEB128]
///   [MetadataLength: u8]
///   [IndexType: u8 = 0x02]
///
/// Each summary record is just the checkpoint key — the slab boundaries at the level below
/// are derived from the level's strides (<c>EntriesPerCkLevel0</c> for level 0, which spans
/// data; <c>RecordsPerCkHigher</c> for level k+1, which spans level k). Level 0 ck i covers
/// data entries [i*N, min((i+1)*N - 1, EntryCount - 1)]; higher-level ck i covers level-below
/// records [i*M, min((i+1)*M - 1, prevCount - 1)].
/// </summary>
public ref struct HsstPackedArrayBuilder<TWriter>
    where TWriter : IByteBufferWriter
{
    /// <summary>Default checkpoint stride: emit a binary-index entry every ~1 KiB of (key+value).</summary>
    public const int DefaultBinaryIndexStrideBytes = 1024;

    private ref TWriter _writer;
    private readonly long _baseOffset;
    private readonly int _keySize;
    private readonly int _valueSize;
    private readonly int _strideBytes;
    private readonly int _entriesPerCkLevel0Log2;
    private readonly int _entriesPerCkLevel0;

    private NativeMemoryListRef<byte> _prevKeyBuffer;
    private NativeMemoryListRef<byte> _checkpointKeys;

    private long _entryCount;
    private long _level0Count;

    /// <summary>
    /// Create a builder writing via <paramref name="writer"/>. <paramref name="keySize"/> /
    /// <paramref name="valueSize"/> set the fixed entry stride; subsequent
    /// <see cref="Add"/> calls validate against them. Allocates working buffers from
    /// NativeMemory — call <see cref="Dispose"/> to free.
    /// </summary>
    public HsstPackedArrayBuilder(ref TWriter writer, int keySize, int valueSize,
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
        // Entries-per-ck at level 0: floor(stride / entry size), then rounded down to the
        // nearest power of two so the reader can use a mask + shift instead of div/mul.
        // With fixed-size entries this turns the byte-stride knob into an exact entry-count
        // boundary, which lets the reader compute slabs from position alone — no need to
        // store LastEntryIndex per checkpoint.
        int entrySize = Math.Max(1, _keySize + _valueSize);
        int rawN = Math.Max(1, _strideBytes / entrySize);
        _entriesPerCkLevel0Log2 = BitOperations.Log2((uint)rawN);
        _entriesPerCkLevel0 = 1 << _entriesPerCkLevel0Log2;

        _prevKeyBuffer = new NativeMemoryListRef<byte>(Math.Max(1, keySize));
        // One checkpoint per stride; size lower bound is keySize bytes.
        int checkpointSlots = Math.Max(8, expectedKeyCount / 8);
        _checkpointKeys = new NativeMemoryListRef<byte>(Math.Max(64, checkpointSlots * Math.Max(1, keySize)));

        _entryCount = 0;
        _level0Count = 0;
    }

    public void Dispose()
    {
        _prevKeyBuffer.Dispose();
        _checkpointKeys.Dispose();
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

        _entryCount++;

        _prevKeyBuffer.Clear();
        _prevKeyBuffer.AddRange(key);

        // Emit at exact entries-per-ck boundaries so reader can derive slab bounds.
        // _entriesPerCkLevel0 is a power of two — use mask in place of modulo.
        if ((_entryCount & (_entriesPerCkLevel0 - 1)) == 0)
        {
            if (_keySize > 0) _checkpointKeys.AddRange(key);
            _level0Count++;
        }
    }

    /// <summary>
    /// Finalize the HSST: emits the recursive summary levels, Metadata, MetadataLength,
    /// and the trailing IndexType discriminator byte.
    /// </summary>
    public void Build()
    {
        // Tail checkpoint: cover the last entry when the entry count is not a multiple of
        // the level-0 stride. Without it a target greater than every emitted ck would have
        // an empty candidate range.
        if (_entryCount > 0 && (_entryCount & (_entriesPerCkLevel0 - 1)) != 0)
        {
            if (_keySize > 0) _checkpointKeys.AddRange(_prevKeyBuffer.AsSpan());
            _level0Count++;
        }

        // Records-per-ck for higher levels: floor(stride / KeySize), rounded down to a
        // power of two. Must be ≥ 2 to guarantee strict reduction. Higher levels cannot be
        // built when KeySize is zero (the keys carry no info).
        int recordsPerCkHigherLog2 = 0;
        int recordsPerCkHigher = 0;
        if (_keySize > 0)
        {
            int rawM = Math.Max(2, _strideBytes / _keySize);
            recordsPerCkHigherLog2 = BitOperations.Log2((uint)rawM);
            if (recordsPerCkHigherLog2 < 1) recordsPerCkHigherLog2 = 1;
            recordsPerCkHigher = 1 << recordsPerCkHigherLog2;
        }

        // Build all summary levels in memory first, then flush them in order to the writer.
        // Per-level record counts are int-bounded in practice (level-0 count ≤
        // _entryCount >> entriesPerCkLevel0Log2 — even a 2.6 GiB-of-entries HSST stays
        // well under int.MaxValue at typical strides). Surface a violation via the
        // checked cast on _level0Count below.
        using NativeMemoryListRef<int> levelCounts = new(HsstPackedArrayLayout.MaxSummaryDepth);

        int level0CountInt = checked((int)_level0Count);
        if (level0CountInt > 0) levelCounts.Add(level0CountInt);

        // Higher levels staged into a single buffer + per-level (startRec) pointers.
        using NativeMemoryListRef<byte> higherLevelsKeys = new(64);
        using NativeMemoryListRef<int> higherLevelStartRec = new(HsstPackedArrayLayout.MaxSummaryDepth);

        // Track the previous level by (startRec, count, fromLevel0) so we re-fetch its span
        // each iteration — adding to higherLevelsKeys may move the underlying NativeMemory.
        int prevStartRec = -1;
        int prevCount = level0CountInt;
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

                // Emit a checkpoint at every recordsPerCkHigher boundary; the ck records the
                // key of the last record in its slab — i.e. record index (k+1)*M - 1.
                for (int i = recordsPerCkHigher - 1; i < prevCount; i += recordsPerCkHigher)
                {
                    higherLevelsKeys.AddRange(prevKeys.Slice(i * _keySize, _keySize));
                    newCount++;
                }
                int lastEmittedIdx = (newCount << recordsPerCkHigherLog2) - 1;
                // Tail ck for the partial last slab.
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
                    throw new InvalidOperationException($"PackedArray summary depth exceeded {HsstPackedArrayLayout.MaxSummaryDepth}.");

                higherLevelStartRec.Add(newLevelStartRec);
                levelCounts.Add(newCount);

                prevStartRec = newLevelStartRec;
                prevCount = newCount;
                prevIsLevel0 = false;

                if (newCount <= 1) break;
            }
        }

        int depth = levelCounts.Count;

        // Flush level 0.
        if (level0CountInt > 0)
        {
            ReadOnlySpan<byte> ckKeys = _checkpointKeys.AsSpan();
            for (int i = 0; i < level0CountInt; i++)
            {
                if (_keySize > 0)
                    IByteBufferWriter.Copy(ref _writer, ckKeys.Slice(i * _keySize, _keySize));
            }
        }

        // Flush higher levels in order from the staging buffer.
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

        long metaStart = _writer.Written;
        WriteLeb128(_keySize);
        WriteLeb128(_valueSize);
        WriteLeb128(_entryCount);
        WriteLeb128(_entriesPerCkLevel0Log2);
        WriteLeb128(recordsPerCkHigherLog2);
        WriteLeb128(depth);
        for (int i = 0; i < depth; i++) WriteLeb128(levelCounts[i]);
        int metaLen = checked((int)(_writer.Written - metaStart));
        if (metaLen > 255)
            throw new InvalidOperationException("PackedArray metadata exceeds 255 bytes.");

        Span<byte> trail = _writer.GetSpan(2);
        trail[0] = (byte)metaLen;
        trail[1] = (byte)IndexType.PackedArray;
        _writer.Advance(2);
    }

    private void WriteLeb128(long value)
    {
        Span<byte> buf = _writer.GetSpan(10);
        int len = Leb128.Write(buf, 0, value);
        _writer.Advance(len);
    }
}
