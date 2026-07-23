// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Sparse;

internal enum SparseNodeKind : byte
{
    Free = 0,
    Leaf,
    Extension,
    Branch,

    /// <summary>Known only by hash; created when structural mutation re-parents an unrevealed child.</summary>
    Blinded,
}

[Flags]
internal enum SparseNodeFlags : byte
{
    None = 0,

    /// <summary>Mutated since reveal/creation or last encode; must be re-encoded.</summary>
    Dirty = 1,

    /// <summary>Last encoding was shorter than 32 bytes; embedded in the parent, never persisted alone.</summary>
    Inline = 2,

    /// <summary>Encoded and staged for publication but not yet published.</summary>
    Unpublished = 4,

    /// <summary>A one-child branch whose survivor reveal and collapse are queued; prevents duplicate queuing.</summary>
    CollapsePending = 8,

    /// <summary>The value bytes were allocated for this node (not aliasing its original RLP), so they die with it.</summary>
    OwnedValue = 16,

    /// <summary>The RLP region was allocated for this node (revealed copy or inline re-encode), not aliased from a parent.</summary>
    OwnedRlp = 32,
}

/// <summary>
/// Compact metadata for one materialized sparse-trie node. All variable content (original RLP,
/// prefix nibbles, leaf value) lives in the owning <see cref="SparseTrieArena"/> byte segment and
/// is addressed by offset; children are addressed through the child-entry segment.
/// </summary>
/// <remarks>
/// Child entries are <c>int</c>s with two encodings: a non-negative value is the arena index of a
/// materialized node; a negative value is <c>~offset</c> (absolute, into the byte segment) of the
/// child item inside this node's original RLP (a blinded hash reference or a not-yet-materialized
/// inline child). Blinded children therefore usually have no node of their own — their 33-byte
/// hash reference (or inline body) is read from the original RLP, which also makes re-encoding
/// unchanged children a copy from that RLP. Offsets are absolute rather than relative to
/// <see cref="RlpOffset"/> so re-encoding a node (which can repoint <see cref="RlpOffset"/> at
/// its new inline bytes) never invalidates surviving entries.
/// </remarks>
internal struct SparseNode
{
    /// <summary>Keccak of the node RLP; valid while not <see cref="SparseNodeFlags.Dirty"/> and not <see cref="SparseNodeFlags.Inline"/>.</summary>
    public ValueHash256 Hash;

    /// <summary>Byte-segment offset of the node RLP (original revealed bytes, or re-encoded inline bytes); -1 when absent.</summary>
    public int RlpOffset;

    /// <summary>Byte-segment offset of the prefix nibbles (one nibble per byte); leaf and extension only.</summary>
    public int PrefixOffset;

    /// <summary>Byte-segment offset of the leaf value bytes.</summary>
    public int ValueOffset;

    /// <summary>Branch: child-entry segment offset of the dense child slice. Extension: the single child entry itself.</summary>
    public int ChildSlice;

    /// <summary>Index of this node's staged publication record; -1 when none.</summary>
    public int StagedRecord;

    public ushort RlpLength;

    /// <summary>Branch only: bit per nibble with a non-empty child.</summary>
    public ushort OccupiedMask;

    /// <summary>Branch only: subset of <see cref="OccupiedMask"/> whose subtree contains dirty nodes.</summary>
    public ushort DirtyMask;

    public byte PrefixLength;
    public byte ValueLength;
    public SparseNodeKind Kind;
    public SparseNodeFlags Flags;

    public readonly bool IsDirty => (Flags & SparseNodeFlags.Dirty) != 0;
    public readonly bool IsInline => (Flags & SparseNodeFlags.Inline) != 0;
    public readonly bool IsUnpublished => (Flags & SparseNodeFlags.Unpublished) != 0;

    /// <summary>Number of children in the dense child slice.</summary>
    public readonly int ChildCount => System.Numerics.BitOperations.PopCount(OccupiedMask);

    /// <summary>Position of the child for <paramref name="nibble"/> within the dense child slice.</summary>
    public readonly int ChildSlot(int nibble) => System.Numerics.BitOperations.PopCount((uint)(OccupiedMask & ((1 << nibble) - 1)));
}

/// <summary>
/// Pooled, chunked storage for sparse-trie nodes, child entries, and variable bytes, with stable
/// integer indices/handles and exact rented/dead-byte accounting.
/// </summary>
/// <remarks>
/// Chunks are rented from <see cref="ArrayPool{T}.Shared"/> and never resized, so an index or
/// handle stays valid for the arena's lifetime; spans obtained from the arena stay valid too, but
/// by convention must not be held across an allocation call. Byte and child-entry allocations are
/// bump allocations that never cross a chunk boundary; a run that does not fit the current chunk
/// starts a new one and the skipped tail is counted as dead. Freed regions are only accounted
/// (there is no compaction); freed node indices are reused through a free stack. The owner
/// rejects a retained generation whose <see cref="DeadBytes"/> exceed 25% of
/// <see cref="RentedBytes"/> instead of compacting.
/// </remarks>
internal sealed class SparseTrieArena : IDisposable
{
    // Large profile: the state trie and dominant storage tries.
    private const int LargeNodeShift = 12;  // 4096 nodes (256KB) per chunk
    private const int LargeByteShift = 16;  // 64KB per chunk
    private const int LargeChildShift = 14; // 16K entries (64KB) per chunk

    // Small profile for small capacity hints: the typical few-slot storage trie rents ~48KB of
    // first chunks instead of ~384KB, which is what dominates pool churn when a block
    // materializes thousands of per-account tries.
    private const int SmallProfileMaxHint = 128;
    private const int SmallNodeShift = 9;   // 512 nodes (32KB) per chunk
    private const int SmallByteShift = 13;  // 8KB per chunk
    private const int SmallChildShift = 11; // 2K entries (8KB) per chunk

    private const int InitialDirectoryLength = 16;

    private readonly int _nodeShift;
    private readonly int _nodeMask;
    private readonly int _byteShift;
    private readonly int _byteSize;
    private readonly int _byteMask;
    private readonly int _childShift;
    private readonly int _childSize;
    private readonly int _childMask;

    private SparseNode[]?[] _nodeChunks;
    private byte[]?[] _byteChunks;
    private int[]?[] _childChunks;

    private int _nodeCount;
    private int _byteCount;      // bump cursor as a global handle: chunk << shift | offset
    private int _childCount;

    private int[] _freeNodes;
    private int _freeNodeCount;

    private long _rentedBytes;
    private long _deadBytes;

    public SparseTrieArena(int nodeCapacityHint = 0)
    {
        bool small = nodeCapacityHint > 0 && nodeCapacityHint <= SmallProfileMaxHint;
        _nodeShift = small ? SmallNodeShift : LargeNodeShift;
        _byteShift = small ? SmallByteShift : LargeByteShift;
        _childShift = small ? SmallChildShift : LargeChildShift;
        _nodeMask = (1 << _nodeShift) - 1;
        _byteSize = 1 << _byteShift;
        _byteMask = _byteSize - 1;
        _childSize = 1 << _childShift;
        _childMask = _childSize - 1;

        int directoryLength = nodeCapacityHint > 0
            ? Math.Max(InitialDirectoryLength, (nodeCapacityHint >> _nodeShift) + 1)
            : InitialDirectoryLength;

        _nodeChunks = ArrayPool<SparseNode[]?>.Shared.Rent(directoryLength);
        _byteChunks = ArrayPool<byte[]?>.Shared.Rent(InitialDirectoryLength);
        _childChunks = ArrayPool<int[]?>.Shared.Rent(InitialDirectoryLength);
        Array.Clear(_nodeChunks);
        Array.Clear(_byteChunks);
        Array.Clear(_childChunks);
        _freeNodes = ArrayPool<int>.Shared.Rent(64);
    }

    public int NodeCount => _nodeCount - _freeNodeCount;

    /// <summary>
    /// Total pool-rented capacity in bytes: the node/byte/child chunks plus the always-rented
    /// chunk directories and the free-node stack, so an arena holding only baseline arrays (e.g. a
    /// cleared storage trie) reports the memory it actually retains rather than zero.
    /// </summary>
    public long RentedBytes => _nodeChunks is null
        ? 0
        : _rentedBytes
          + (long)(_nodeChunks.Length + _byteChunks.Length + _childChunks.Length) * IntPtr.Size
          + (long)_freeNodes.Length * sizeof(int);

    /// <summary>Bytes made unreachable by mutation (freed nodes, replaced slices/values, chunk tails).</summary>
    public long DeadBytes => _deadBytes;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref SparseNode Node(int index) => ref _nodeChunks[index >> _nodeShift]![index & _nodeMask];

    public int AllocNode()
    {
        if (_freeNodeCount > 0)
        {
            int reused = _freeNodes[--_freeNodeCount];
            ref SparseNode node = ref Node(reused);
            node = default;
            node.RlpOffset = -1;
            node.StagedRecord = -1;
            return reused;
        }

        int index = _nodeCount++;
        int chunk = index >> _nodeShift;
        if (chunk >= _nodeChunks.Length)
        {
            GrowDirectory(ref _nodeChunks);
        }

        SparseNode[]? chunkArray = _nodeChunks[chunk];
        if (chunkArray is null)
        {
            _nodeChunks[chunk] = chunkArray = ArrayPool<SparseNode>.Shared.Rent(_nodeMask + 1);
            _rentedBytes += (long)chunkArray.Length * Unsafe.SizeOf<SparseNode>();
        }

        ref SparseNode newNode = ref chunkArray[index & _nodeMask];
        newNode = default;
        newNode.RlpOffset = -1;
        newNode.StagedRecord = -1;
        return index;
    }

    public void FreeNode(int index)
    {
        ref SparseNode node = ref Node(index);
        // The node slot itself is recycled through the free stack, so only the variable regions
        // this node owns become dead; aliased regions die with their owner.
        _deadBytes += node.PrefixLength;
        if ((node.Flags & SparseNodeFlags.OwnedRlp) != 0)
        {
            _deadBytes += node.RlpLength;
        }

        if ((node.Flags & SparseNodeFlags.OwnedValue) != 0)
        {
            _deadBytes += node.ValueLength;
        }

        if (node.Kind == SparseNodeKind.Branch)
        {
            _deadBytes += node.ChildCount * sizeof(int);
        }

        node = default; // Kind = Free

        if (_freeNodeCount == _freeNodes.Length)
        {
            int[] grown = ArrayPool<int>.Shared.Rent(_freeNodes.Length * 2);
            _freeNodes.AsSpan().CopyTo(grown);
            ArrayPool<int>.Shared.Return(_freeNodes);
            _freeNodes = grown;
        }

        _freeNodes[_freeNodeCount++] = index;
    }

    /// <summary>Allocates a contiguous byte run and returns its handle; get the span via <see cref="Bytes"/>.</summary>
    public int AllocBytes(int length)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, _byteSize);
        int offset = _byteCount & _byteMask;
        int chunk = _byteCount >> _byteShift;
        if (offset + length > _byteSize)
        {
            _deadBytes += _byteSize - offset;
            chunk++;
            offset = 0;
            _byteCount = chunk << _byteShift;
        }

        if (chunk >= _byteChunks.Length)
        {
            GrowDirectory(ref _byteChunks);
        }

        if (_byteChunks[chunk] is null)
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(_byteSize);
            _byteChunks[chunk] = rented;
            _rentedBytes += rented.Length;
        }

        int handle = _byteCount;
        _byteCount += length;
        return handle;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> Bytes(int handle, int length) =>
        _byteChunks[handle >> _byteShift].AsSpan(handle & _byteMask, length);

    /// <summary>Accounts a byte run made unreachable by mutation.</summary>
    public void ReleaseBytes(int length) => _deadBytes += length;

    /// <summary>Allocates a dense child slice of <paramref name="count"/> entries and returns its handle.</summary>
    public int AllocChildSlice(int count)
    {
        int offset = _childCount & _childMask;
        int chunk = _childCount >> _childShift;
        if (offset + count > _childSize)
        {
            _deadBytes += (_childSize - offset) * sizeof(int);
            chunk++;
            _childCount = chunk << _childShift;
        }

        if (chunk >= _childChunks.Length)
        {
            GrowDirectory(ref _childChunks);
        }

        if (_childChunks[chunk] is null)
        {
            int[] rented = ArrayPool<int>.Shared.Rent(_childSize);
            _childChunks[chunk] = rented;
            _rentedBytes += (long)rented.Length * sizeof(int);
        }

        int handle = _childCount;
        _childCount += count;
        return handle;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<int> ChildSlice(int handle, int count) =>
        _childChunks[handle >> _childShift].AsSpan(handle & _childMask, count);

    /// <summary>Accounts a child slice made unreachable by mutation.</summary>
    public void ReleaseChildSlice(int count) => _deadBytes += count * sizeof(int);

    private static void GrowDirectory<T>(ref T[]?[] directory)
    {
        T[]?[] grown = ArrayPool<T[]?>.Shared.Rent(directory.Length * 2);
        Array.Clear(grown, directory.Length, grown.Length - directory.Length);
        directory.AsSpan().CopyTo(grown);
        ArrayPool<T[]?>.Shared.Return(directory, clearArray: true);
        directory = grown;
    }

    public void Dispose()
    {
        ReturnChunks(_nodeChunks);
        ReturnChunks(_byteChunks);
        ReturnChunks(_childChunks);
        _nodeChunks = null!;
        _byteChunks = null!;
        _childChunks = null!;

        if (_freeNodes.Length > 0)
        {
            ArrayPool<int>.Shared.Return(_freeNodes);
            _freeNodes = [];
        }

        _rentedBytes = 0;
        _deadBytes = 0;
        _nodeCount = 0;
        _byteCount = 0;
        _childCount = 0;
        _freeNodeCount = 0;
    }

    private static void ReturnChunks<T>(T[]?[]? directory)
    {
        if (directory is null)
        {
            return;
        }

        for (int i = 0; i < directory.Length; i++)
        {
            if (directory[i] is T[] chunk)
            {
                ArrayPool<T>.Shared.Return(chunk);
                directory[i] = null;
            }
        }

        ArrayPool<T[]?>.Shared.Return(directory, clearArray: true);
    }
}
