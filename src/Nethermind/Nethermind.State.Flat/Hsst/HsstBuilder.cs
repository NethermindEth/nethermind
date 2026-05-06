// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Nethermind.Core.Collections;
using Nethermind.Core.Utils;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Builds an HSST (Hierarchical Static Sorted Table) from key-value entries.
/// Entries MUST be added in sorted key order. No internal sorting is performed.
///
/// Binary layout (BTree):
///   [Data Region: entries...][Index Region: B-tree nodes...][IndexType: u8 = 0x01]
///   Root index is readable from the end via MetadataLength byte (no trailer).
///
/// Binary layout (BTreeHashIndex):
///   [Data Region][Index Region][HashTable: 4*N bytes][TableSize: u32 LE][IndexType: u8 = 0x03]
///   Same as BTree, with an open-addressed hash table of 4-byte LE pointers
///   appended after the root. Each non-zero, non-0xFFFFFFFF entry points at
///   the same MetadataStart that the B-tree would yield. 0 = empty slot;
///   0xFFFFFFFF = collision sentinel — reader must consult the B-tree. The slot
///   for a key is computed via Lemire's multiply-shift reduction so the table
///   need not be a power of two; <see cref="HsstHash.BucketCount"/> sizes it
///   directly to ceil(N / target).
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
    private ref TWriter _writer;
    private int _writtenBeforeValue;
    private readonly int _baseOffset;
    private readonly HsstBTreeOptions _options;

    // Working buffers allocated from NativeMemory
    private NativeMemoryListRef<byte> _separatorBuffer;
    private NativeMemoryListRef<HsstEntry> _entriesBuffer;
    private NativeMemoryListRef<byte> _prevKeyBuffer;

    // Hash index entry hashes (only allocated when UseHashIndex)
    private NativeMemoryListRef<uint> _entryHashes;

    public readonly struct HsstEntry(int sepOffset, int sepLen, ulong metadataStart)
    {
        public readonly int SepOffset = sepOffset;
        public readonly int SepLen = sepLen;
        /// <summary>
        /// Offset within the HSST (relative to byte 0) where value metadata starts.
        /// Stored as ulong so the B-tree value section can address up to 2^48 bytes
        /// (limit is the 6-byte BaseOffset footer field, not this type).
        /// </summary>
        public readonly ulong MetadataStart = metadataStart;
    }

    /// <summary>
    /// Create builder writing via the given writer.
    /// The trailing IndexType byte is appended in <see cref="Build"/>.
    /// Allocates working buffers from NativeMemory — call Dispose() to free them.
    /// <paramref name="expectedKeyCount"/> sizes the entry/separator working buffers up front;
    /// pass an estimate when known to avoid resize allocations. The buffers still grow on demand.
    /// </summary>
    public HsstBuilder(ref TWriter writer, HsstBTreeOptions? options = null, int expectedKeyCount = 16)
    {
        HsstBTreeOptions opts = options ?? HsstBTreeOptions.Default;
        if (opts.UseHashIndex && !(opts.HashIndexTargetUtilization > 0.1 && opts.HashIndexTargetUtilization <= 1.0))
            throw new ArgumentOutOfRangeException(nameof(options), "HashIndexTargetUtilization must be in (0.1, 1.0].");

        _writer = ref writer;
        _baseOffset = _writer.Written;
        _options = opts;

        // Heuristic: ~32 bytes per separator/value. The buffers grow as needed.
        int byteCap = Math.Max(64, expectedKeyCount * 32);
        _separatorBuffer = new NativeMemoryListRef<byte>(byteCap);
        _entriesBuffer = new NativeMemoryListRef<HsstEntry>(expectedKeyCount);
        _prevKeyBuffer = new NativeMemoryListRef<byte>(256);

        if (opts.UseHashIndex)
        {
            _entryHashes = new NativeMemoryListRef<uint>(expectedKeyCount);
        }
    }

    private bool NeedsEntryHashes => _options.UseHashIndex;

    /// <summary>
    /// Free working NativeMemory buffers.
    /// </summary>
    public void Dispose()
    {
        _separatorBuffer.Dispose();
        _entriesBuffer.Dispose();
        _prevKeyBuffer.Dispose();
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
        _writtenBeforeValue = _writer.Written;
        return ref _writer;
    }

    /// <summary>
    /// Finish value write. Computes length from snapshot taken by BeginValueWrite.
    /// Key must be greater than previous key (sorted order).
    /// </summary>
    public void FinishValueWrite(scoped ReadOnlySpan<byte> key)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(key.Length, 255);

        int actualLen = _writer.Written - _writtenBeforeValue;
        // metadataStart stored in index is relative to byte 0 of this HSST.
        ulong metadataStart = (ulong)(_writer.Written - _baseOffset);

        // Compute separator eagerly
        int sepLen = ComputeSeparatorLength(
            _prevKeyBuffer.AsSpan(),
            key,
            nextKey: default,
            _options.MinSeparatorLength);

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
        _writtenBeforeValue = _writer.Written;
        IByteBufferWriter.Copy(ref _writer, value);
        FinishValueWrite(key);
    }

    /// <summary>
    /// Build index, then append the trailing IndexType byte. The ref writer is already advanced.
    /// The root index node is readable from the end via its MetadataLength byte; the IndexType
    /// byte sits one byte further out, at the very end of the HSST.
    /// </summary>
    public void Build()
    {
        int maxLeafEntries = _options.MaxLeafEntries;
        int minLeafEntries = Math.Min(_options.MinLeafEntries, maxLeafEntries);
        int maxIntermediateEntries = _options.MaxIntermediateEntries;

        int absoluteIndexStart = _writer.Written - _baseOffset;

        HsstIndexBuilder<TWriter> indexBuilder = new(
            ref _writer, _entriesBuffer.AsSpan(),
            _separatorBuffer.AsSpan());

        indexBuilder.Build(absoluteIndexStart, maxLeafEntries, maxIntermediateEntries, minLeafEntries);

        // Optional hash index section. Empty HSSTs fall back to plain BTree because
        // a 0-entry table has no benefit and an empty data region would make the
        // 0 sentinel ambiguous.
        bool emitHashIndex = _options.UseHashIndex && _entriesBuffer.Count > 0;
        if (emitHashIndex)
        {
            EmitHashTable();
        }

        // Trailing IndexType byte (last byte of the HSST).
        IndexType tag = emitHashIndex ? IndexType.BTreeHashIndex : IndexType.BTree;
        Span<byte> tail = _writer.GetSpan(1);
        tail[0] = (byte)tag;
        _writer.Advance(1);
    }

    private void EmitHashTable()
    {
        ReadOnlySpan<HsstEntry> entries = _entriesBuffer.AsSpan();
        ReadOnlySpan<uint> hashes = _entryHashes.AsSpan();
        int n = entries.Length;

        int tableSize = HsstHash.BucketCount(n, _options.HashIndexTargetUtilization);

        // Build the table in a scratch buffer first, then blit. Avoids interleaving
        // GetSpan/Advance calls and simplifies grow-aware writers.
        // The (capacity, startingCount) ctor zero-initializes the first startingCount slots.
        using NativeMemoryListRef<uint> table = new(tableSize, tableSize);
        Span<uint> slots = table.AsSpan();

        const uint Empty = 0u;
        const uint Collision = 0xFFFFFFFFu;

        for (int i = 0; i < n; i++)
        {
            uint slot = HsstHash.Slot(hashes[i], tableSize);
            if (slots[(int)slot] == Empty)
            {
                ulong meta = entries[i].MetadataStart;
                if (meta > uint.MaxValue)
                    throw new InvalidOperationException(
                        $"BTreeHashIndex MetadataStart {meta} exceeds 4 GiB; use plain BTree variant for >4 GiB HSSTs.");
                slots[(int)slot] = (uint)meta;
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

        // Emit TableSize as 4-byte little-endian (replaces TableSizeLog2 byte; Lemire
        // sizing produces non-power-of-two values so a single log2 byte no longer fits).
        Span<byte> sizeSpan = _writer.GetSpan(4);
        BinaryPrimitives.WriteUInt32LittleEndian(sizeSpan, (uint)tableSize);
        _writer.Advance(4);
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
