// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Collections;
using Nethermind.Core.Utils;
using Nethermind.State.Flat.BSearchIndex;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Builds an HSST (Hierarchical Static Sorted Table) from key-value entries.
/// Entries MUST be added in sorted key order. No internal sorting is performed.
///
/// Binary layout (BTree):
///   [Data Region: entries...][Index Region: B-tree nodes...][IndexType: u8 = 0x01]
///   Root index is readable from the end via MetadataLength byte (no trailer).
///
/// Binary layout (BTreeInlineValue):
///   [Index Region: B-tree nodes...][IndexType: u8 = 0x02]
///   No data section. Leaf values are stored directly in the B-tree index.
///
/// Binary layout (BTreeHashIndex):
///   [Data Region][Index Region][HashTable: 4*2^L bytes][TableSizeLog2: u8][IndexType: u8 = 0x03]
///   Same as BTree, with an open-addressed hash table of 4-byte LE pointers
///   appended after the root. Each non-zero, non-0xFFFFFFFF entry points at
///   the same MetadataStart that the B-tree would yield. 0 = empty slot;
///   0xFFFFFFFF = collision sentinel — reader must consult the B-tree.
///
/// Entry format (normal, value first, lengths forward-readable from MetadataStart):
///   [Value][ValueLength: LEB128][KeyLength: u8][FullKey]
/// MetadataStart points at the ValueLength LEB128. KeyLength is a single byte: keys are
/// capped at 255 bytes by format contract. The leaf B-tree node also stores a separator
/// (a min-length prefix of the full key) for binary-search navigation, but the
/// data-region entry is self-describing — the full key lives in the entry tail and the
/// reader does not need to consult the leaf to recover it. (ValueLength uses LEB128
/// because values are unbounded; the LEB128 terminator chain is forward-readable only,
/// so the lengths sit after the value and the index aims at them.)
/// </summary>
public ref struct HsstBuilder<TWriter>
    where TWriter : IByteBufferWriter
{
    /// <summary>
    /// Default maximum entries per leaf B-tree node. Above this, the builder splits and
    /// promotes a separator into an intermediate node.
    /// </summary>
    public const int MaxLeafEntries = 256;

    private ref TWriter _writer;
    private int _writtenBeforeValue;
    private readonly int _baseOffset;

    private readonly int _minSeparatorLength;
    private readonly bool _inlineValues;
    private readonly bool _useHashIndex;
    private readonly double _hashIndexTargetUtilization;
    private readonly HashProbeMode _leafHashProbeMode;

    // Working buffers allocated from NativeMemory
    private NativeMemoryListRef<byte> _separatorBuffer;
    private NativeMemoryListRef<HsstEntry> _entriesBuffer;
    private NativeMemoryListRef<byte> _prevKeyBuffer;

    // Inline value buffers (only allocated when _inlineValues is true)
    private NativeMemoryListRef<byte> _inlineValueBuffer;
    private NativeMemoryListRef<int> _inlineValueLengths;

    // Hash index entry hashes (only allocated when _useHashIndex is true)
    private NativeMemoryListRef<uint> _entryHashes;

    public readonly struct HsstEntry(int sepOffset, int sepLen, int metadataStart)
    {
        public readonly int SepOffset = sepOffset;
        public readonly int SepLen = sepLen;
        /// <summary>
        /// BTree: offset within the HSST (relative to byte 0) where value metadata starts.
        /// BTreeInlineValue: offset into the inline value buffer.
        /// </summary>
        public readonly int MetadataStart = metadataStart;
    }

    /// <summary>
    /// Create builder writing via the given writer.
    /// The trailing IndexType byte is appended in <see cref="Build"/>.
    /// Allocates working buffers from NativeMemory — call Dispose() to free them.
    /// <paramref name="expectedKeyCount"/> sizes the entry/separator working buffers up front;
    /// pass an estimate when known to avoid resize allocations. The buffers still grow on demand.
    /// </summary>
    public HsstBuilder(ref TWriter writer, int minSeparatorLength = 0, bool inlineValues = false, int expectedKeyCount = 16, bool useHashIndex = false, double hashIndexTargetUtilization = 0.75, HashProbeMode leafHashProbeMode = HashProbeMode.None)
    {
        if (useHashIndex && inlineValues)
            throw new NotSupportedException("Hash index is not supported with inline values.");
        if (useHashIndex && !(hashIndexTargetUtilization > 0.1 && hashIndexTargetUtilization <= 1.0))
            throw new ArgumentOutOfRangeException(nameof(hashIndexTargetUtilization), "Must be in (0.1, 1.0].");

        _writer = ref writer;
        _baseOffset = _writer.Written;
        _minSeparatorLength = minSeparatorLength;
        _inlineValues = inlineValues;
        _useHashIndex = useHashIndex;
        _hashIndexTargetUtilization = hashIndexTargetUtilization;
        _leafHashProbeMode = leafHashProbeMode;

        // Heuristic: ~32 bytes per separator/value. The buffers grow as needed.
        int byteCap = Math.Max(64, expectedKeyCount * 32);
        _separatorBuffer = new NativeMemoryListRef<byte>(byteCap);
        _entriesBuffer = new NativeMemoryListRef<HsstEntry>(expectedKeyCount);
        _prevKeyBuffer = new NativeMemoryListRef<byte>(256);

        if (inlineValues)
        {
            _inlineValueBuffer = new NativeMemoryListRef<byte>(byteCap);
            _inlineValueLengths = new NativeMemoryListRef<int>(expectedKeyCount);
        }

        if (useHashIndex || leafHashProbeMode != HashProbeMode.None)
        {
            _entryHashes = new NativeMemoryListRef<uint>(expectedKeyCount);
        }
    }

    private bool NeedsEntryHashes => _useHashIndex || _leafHashProbeMode != HashProbeMode.None;

    /// <summary>
    /// Free working NativeMemory buffers.
    /// </summary>
    public void Dispose()
    {
        _separatorBuffer.Dispose();
        _entriesBuffer.Dispose();
        _prevKeyBuffer.Dispose();
        if (_inlineValues)
        {
            _inlineValueBuffer.Dispose();
            _inlineValueLengths.Dispose();
        }
        if (NeedsEntryHashes)
        {
            _entryHashes.Dispose();
        }
    }

    /// <summary>
    /// Begin writing a value. Returns ref to the shared writer and snapshots Written.
    /// After writing, call FinishValueWrite with just the key.
    /// </summary>
    public ref TWriter BeginValueWrite()
    {
        if (_inlineValues) throw new NotSupportedException("BeginValueWrite not supported in inline mode. Use Add() instead.");
        _writtenBeforeValue = _writer.Written;
        return ref _writer;
    }

    /// <summary>
    /// Finish value write. Computes length from snapshot taken by BeginValueWrite.
    /// Key must be greater than previous key (sorted order).
    /// </summary>
    public void FinishValueWrite(scoped ReadOnlySpan<byte> key)
    {
        if (_inlineValues) throw new NotSupportedException("FinishValueWrite not supported in inline mode. Use Add() instead.");
        ArgumentOutOfRangeException.ThrowIfGreaterThan(key.Length, 255);

        int actualLen = _writer.Written - _writtenBeforeValue;
        // metadataStart stored in index is relative to byte 0 of this HSST.
        int metadataStart = _writer.Written - _baseOffset;

        // Compute separator eagerly
        int sepLen = ComputeSeparatorLength(
            _prevKeyBuffer.AsSpan(),
            key,
            nextKey: default,
            _minSeparatorLength);

        int sepOffset = _separatorBuffer.Count;
        _separatorBuffer.AddRange(key[..sepLen]);

        // Write [ValueLength: LEB128][KeyLength: u8][FullKey]. The full key lives in
        // the data region so the entry is self-describing; the leaf separator above is
        // kept purely to drive in-leaf binary search.
        Span<byte> leb = _writer.GetSpan(5);
        int lebLen = Leb128.Write(leb, 0, actualLen);
        _writer.Advance(lebLen);

        Span<byte> kl = _writer.GetSpan(1);
        kl[0] = (byte)key.Length;
        _writer.Advance(1);

        if (key.Length > 0)
        {
            IByteBufferWriter.Copy(ref _writer, key);
        }

        _entriesBuffer.Add(new HsstEntry(sepOffset, sepLen, metadataStart));

        if (NeedsEntryHashes)
        {
            _entryHashes.Add(HsstHash.HashKey(key));
        }

        _prevKeyBuffer.Clear();
        _prevKeyBuffer.AddRange(key);
    }

    /// <summary>
    /// Convenience: add key-value pair in one call.
    /// </summary>
    public void Add(scoped ReadOnlySpan<byte> key, scoped ReadOnlySpan<byte> value)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(key.Length, 255);
        if (_inlineValues)
        {
            // Inline: separator = full key, buffer value separately
            int sepOffset = _separatorBuffer.Count;
            _separatorBuffer.AddRange(key);

            int valueOffset = _inlineValueBuffer.Count;
            _inlineValueBuffer.AddRange(value);
            _inlineValueLengths.Add(value.Length);

            _entriesBuffer.Add(new HsstEntry(sepOffset, key.Length, valueOffset));

            if (NeedsEntryHashes)
            {
                _entryHashes.Add(HsstHash.HashKey(key));
            }

            _prevKeyBuffer.Clear();
            _prevKeyBuffer.AddRange(key);
        }
        else
        {
            _writtenBeforeValue = _writer.Written;
            IByteBufferWriter.Copy(ref _writer, value);
            FinishValueWrite(key);
        }
    }

    /// <summary>
    /// Build index, then append the trailing IndexType byte. The ref writer is already advanced.
    /// The root index node is readable from the end via its MetadataLength byte; the IndexType
    /// byte sits one byte further out, at the very end of the HSST.
    /// </summary>
    public void Build(int maxLeafEntries = MaxLeafEntries)
    {
        ReadOnlySpan<uint> entryHashes = NeedsEntryHashes ? _entryHashes.AsSpan() : default;

        if (_inlineValues)
        {
            // Inline: no data section, index starts at byte 0 of the HSST.
            int absoluteIndexStart = 0;

            HsstIndexBuilder<TWriter> indexBuilder = new(
                ref _writer, _entriesBuffer.AsSpan(),
                _separatorBuffer.AsSpan(),
                _inlineValueBuffer.AsSpan(),
                _inlineValueLengths.AsSpan(),
                entryHashes,
                _leafHashProbeMode);

            indexBuilder.Build(absoluteIndexStart, maxLeafEntries);
        }
        else
        {
            int absoluteIndexStart = _writer.Written - _baseOffset;

            HsstIndexBuilder<TWriter> indexBuilder = new(
                ref _writer, _entriesBuffer.AsSpan(),
                _separatorBuffer.AsSpan(),
                entryHashes,
                _leafHashProbeMode);

            indexBuilder.Build(absoluteIndexStart, maxLeafEntries);
        }

        // Optional hash index section. Empty HSSTs fall back to plain BTree because
        // a 0-entry table has no benefit and an empty data region would make the
        // 0 sentinel ambiguous.
        bool emitHashIndex = _useHashIndex && _entriesBuffer.Count > 0;
        if (emitHashIndex)
        {
            EmitHashTable();
        }

        // Trailing IndexType byte (last byte of the HSST).
        IndexType tag;
        if (emitHashIndex) tag = IndexType.BTreeHashIndex;
        else if (_inlineValues) tag = IndexType.BTreeInlineValue;
        else tag = IndexType.BTree;
        Span<byte> tail = _writer.GetSpan(1);
        tail[0] = (byte)tag;
        _writer.Advance(1);
    }

    private void EmitHashTable()
    {
        ReadOnlySpan<HsstEntry> entries = _entriesBuffer.AsSpan();
        ReadOnlySpan<uint> hashes = _entryHashes.AsSpan();
        int n = entries.Length;

        int tableSize = HsstHash.BucketCount(n, _hashIndexTargetUtilization);
        int log2 = BitOperations.TrailingZeroCount(tableSize);
        uint mask = (uint)(tableSize - 1);

        // Build the table in a scratch buffer first, then blit. Avoids interleaving
        // GetSpan/Advance calls and simplifies grow-aware writers.
        // The (capacity, startingCount) ctor zero-initializes the first startingCount slots.
        using NativeMemoryListRef<uint> table = new(tableSize, tableSize);
        Span<uint> slots = table.AsSpan();

        const uint Empty = 0u;
        const uint Collision = 0xFFFFFFFFu;

        for (int i = 0; i < n; i++)
        {
            uint slot = hashes[i] & mask;
            if (slots[(int)slot] == Empty)
            {
                slots[(int)slot] = (uint)entries[i].MetadataStart;
            }
            else
            {
                slots[(int)slot] = Collision;
            }
        }

        // Emit table in 4-byte little-endian slots.
        for (int i = 0; i < tableSize; i++)
        {
            Span<byte> dst = _writer.GetSpan(4);
            BinaryPrimitives.WriteUInt32LittleEndian(dst, slots[i]);
            _writer.Advance(4);
        }

        // Emit TableSizeLog2 byte.
        Span<byte> log2Span = _writer.GetSpan(1);
        log2Span[0] = (byte)log2;
        _writer.Advance(1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ComputeSeparatorLength(ReadOnlySpan<byte> prevKey, ReadOnlySpan<byte> currKey, ReadOnlySpan<byte> nextKey, int minSeparatorLength = 0)
    {
        int minVsPrev = 0;
        if (!prevKey.IsEmpty)
        {
            int common = CommonPrefixLength(prevKey, currKey);
            minVsPrev = common + 1;
        }

        int minVsNext = 0;
        if (!nextKey.IsEmpty)
        {
            int common = CommonPrefixLength(currKey, nextKey);
            minVsNext = common + 1;
        }

        int len = Math.Max(minVsPrev, minVsNext);
        len = Math.Min(len, currKey.Length);
        if (len == 0) len = Math.Min(1, currKey.Length);

        return Math.Min(Math.Max(len, minSeparatorLength), currKey.Length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CommonPrefixLength(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        int minLen = Math.Min(a.Length, b.Length);
        for (int i = 0; i < minLen; i++)
        {
            if (a[i] != b[i]) return i;
        }
        return minLen;
    }
}
