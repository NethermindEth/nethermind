// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>
/// The 256-leaf subtree of one stem, stored as a presence bitmap followed by sparse post-order
/// node offsets and their parallel 32-byte values or cached hashes.
/// </summary>
/// <remarks>
/// The first 32 bytes are the MSB-first leaf bitmap. It is followed by one little-endian
/// 16-bit absolute post-order position for each live node, then one parallel 32-byte node entry.
/// Leaf entries contain their values; internal entries contain their cached child hash. A node is
/// live exactly when its subtree contains a present leaf, so offsets are strictly ascending.
/// Zero values are normalized to absent. An empty blob is represented by an empty array and
/// signals stem deletion.
/// </remarks>
public static class StemLeafBlob
{
    public const int ValueLength = 32;
    private const int BitmapLength = 32;
    private const int LeafCount = 256;
    private const int OffsetEntryLength = 2;
    private const int RootPosition = 2 * LeafCount - 2;

    private static readonly byte[] ZeroBitmap = new byte[BitmapLength];

    public static bool TryGetValue(ReadOnlySpan<byte> blob, byte subIndex, out ReadOnlySpan<byte> value)
    {
        if (!blob.IsEmpty && IsPresent(blob, subIndex))
        {
            int liveCount = GetLiveCount(blob);
            ReadOnlySpan<byte> offsets = blob.Slice(BitmapLength, liveCount * OffsetEntryLength);
            int slot = FindOffset(offsets, LeafPosition(subIndex));
            if (slot >= 0)
            {
                int nodesOffset = BitmapLength + offsets.Length;
                value = blob.Slice(nodesOffset + slot * ValueLength, ValueLength);
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Applies <paramref name="changes"/> (each a 32-byte leaf value; a zero value clears the leaf) to
    /// <paramref name="blob"/>, returning a disposable <see cref="RebuildState"/> whose <see cref="RebuildState.Blob"/>
    /// is the new blob (empty, with <see cref="RebuildState.IsEmpty"/> set, when no leaves remain) and whose
    /// <see cref="RebuildState.SubtreeRoot"/> is the merkelized 256-leaf subtree root.
    /// </summary>
    /// <remarks>
    /// Rebuilds dirty paths in post-order and copies clean subtree entries verbatim. A present leaf
    /// contributes <c>blake3(value)</c>; higher levels use the EIP-8297 pair hash, with empty subtrees
    /// folding to zero. The blob is backed by memory from <paramref name="provider"/>, so the caller must
    /// dispose the result once its bytes have been consumed.
    /// </remarks>
    internal static RebuildState Apply(ReadOnlySpan<byte> blob, IPbtStemChanges changes, IRefCountingMemoryProvider provider)
    {
        ReadOnlySpan<byte> previousBitmap = blob.IsEmpty ? ZeroBitmap : blob[..BitmapLength];

        Span<byte> bitmap = stackalloc byte[BitmapLength];
        previousBitmap.CopyTo(bitmap);

        int changeCount = changes.Count;
        // The map keeps writes ascending by sub-index, so they can be read back in order without sorting.
        for (int i = 0; i < changeCount; i++) SetPresent(bitmap, changes.SubIndexAt(i), changes.Get(i) != default);

        if (!RangeHasLeaf(bitmap, 0, LeafCount)) return default;

        int liveCount = CountLiveNodes(bitmap, 0, LeafCount);
        RebuildState state = new(blob, previousBitmap, bitmap, changes, GetLiveCount(blob), liveCount, provider);
        state.Build();
        return state;
    }

    /// <summary>The stem node hash: <c>blake3(stem || 0x00 || subtreeRoot)</c>.</summary>
    public static ValueHash256 ComputeStemNodeHash(in Stem stem, in ValueHash256 subtreeRoot)
    {
        ValueHash256 left = default;
        stem.Bytes.CopyTo(left.BytesAsSpan);
        return Blake3Hash.HashPairOrZero(left, subtreeRoot);
    }

    private static bool IsPresent(ReadOnlySpan<byte> bitmap, byte subIndex) =>
        (bitmap[subIndex >> 3] & (1 << (7 - (subIndex & 7)))) != 0;

    private static void SetPresent(Span<byte> bitmap, byte subIndex, bool present)
    {
        byte mask = (byte)(1 << (7 - (subIndex & 7)));
        if (present)
        {
            bitmap[subIndex >> 3] |= mask;
        }
        else
        {
            bitmap[subIndex >> 3] &= (byte)~mask;
        }
    }

    private static bool RangeHasLeaf(ReadOnlySpan<byte> bitmap, int low, int high)
    {
        int firstByte = low >> 3;
        int lastByte = (high - 1) >> 3;
        for (int byteIndex = firstByte; byteIndex <= lastByte; byteIndex++)
        {
            int from = byteIndex == firstByte ? low & 7 : 0;
            int to = byteIndex == lastByte && (high & 7) != 0 ? high & 7 : 8;
            byte mask = (byte)((0xff >> from) & (0xff << (8 - to)));
            if (BitOperations.PopCount((uint)(bitmap[byteIndex] & mask)) != 0) return true;
        }

        return false;
    }

    /// <summary>The number of live nodes a rebuild emits for <paramref name="bitmap"/>: present leaves plus every internal node whose range holds one.</summary>
    private static int CountLiveNodes(ReadOnlySpan<byte> bitmap, int low, int high)
    {
        if (!RangeHasLeaf(bitmap, low, high)) return 0;
        if (high - low == 1) return 1;

        int middle = low + (high - low) / 2;
        return 1 + CountLiveNodes(bitmap, low, middle) + CountLiveNodes(bitmap, middle, high);
    }

    private static int LeafPosition(byte subIndex) =>
        2 * subIndex - BitOperations.PopCount((uint)subIndex);

    private static int GetLiveCount(ReadOnlySpan<byte> blob)
    {
        if (blob.IsEmpty) return 0;

        int payloadLength = blob.Length - BitmapLength;
        Debug.Assert(payloadLength >= 0 && payloadLength % (OffsetEntryLength + ValueLength) == 0);
        return payloadLength / (OffsetEntryLength + ValueLength);
    }

    private static ushort ReadOffset(ReadOnlySpan<byte> offsets, int slot) =>
        BinaryPrimitives.ReadUInt16LittleEndian(offsets.Slice(slot * OffsetEntryLength, OffsetEntryLength));

    private static void WriteOffset(Span<byte> destination, int position) =>
        BinaryPrimitives.WriteUInt16LittleEndian(destination, checked((ushort)position));

    private static int FindOffset(ReadOnlySpan<byte> offsets, int position)
    {
        int low = 0;
        int high = offsets.Length / OffsetEntryLength - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            int candidate = ReadOffset(offsets, middle);
            if (candidate == position) return middle;

            if (candidate < position)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return -1;
    }

    private enum NodeKind : byte { Empty, Leaf, Internal }

    /// <summary>A reference to a rebuilt node: its kind and, for a leaf or internal node, its slot in the new blob.</summary>
    private readonly record struct NodeRef(NodeKind Kind, int Slot)
    {
        public static readonly NodeRef Empty = new(NodeKind.Empty, 0);
        public static NodeRef Leaf(int slot) => new(NodeKind.Leaf, slot);
        public static NodeRef Internal(int slot) => new(NodeKind.Internal, slot);
    }

    /// <remarks>
    /// Rebuilds directly into a final blob: the bitmap sits at its front, and each live node is
    /// written to its slot in the offset and node regions as the post-order walk reaches it. The recursion
    /// hands each subtree up as a <see cref="NodeRef"/> into that buffer, so a node's hash is materialized
    /// only where a parent needs it, never copied by value up the stack. The buffer comes from an
    /// <see cref="IRefCountingMemoryProvider"/> and is released on <see cref="Dispose"/>; a <c>default</c>
    /// instance owns no buffer and represents the empty (no leaves remaining) result.
    /// </remarks>
    internal ref struct RebuildState
    {
        private readonly ReadOnlySpan<byte> _previousBitmap;
        private readonly ReadOnlySpan<byte> _newBitmap;
        private readonly ReadOnlySpan<byte> _previousOffsets;
        private readonly ReadOnlySpan<byte> _previousNodes;
        private readonly IPbtStemChanges _changes;
        private readonly Span<byte> _buffer;
        private readonly int _nodeBase;
        private RefCountingMemory? _memory;
        private int _previousSlot;
        private int _changeIndex;
        private int _slot;

        /// <summary>The rebuilt blob, valid until <see cref="Dispose"/>; empty when <see cref="IsEmpty"/>.</summary>
        public readonly ReadOnlySpan<byte> Blob => _buffer;

        /// <summary>Whether the stem has no leaves remaining, in which case <see cref="Blob"/> is empty.</summary>
        public readonly bool IsEmpty => _memory is null;

        /// <summary>The merkelized 256-leaf subtree root, populated by <see cref="Build"/>.</summary>
        public ValueHash256 SubtreeRoot { get; private set; }

        public RebuildState(
            ReadOnlySpan<byte> blob,
            ReadOnlySpan<byte> previousBitmap,
            scoped ReadOnlySpan<byte> newBitmap,
            IPbtStemChanges changes,
            int previousLiveCount,
            int liveCount,
            IRefCountingMemoryProvider provider)
        {
            int length = BitmapLength + liveCount * (OffsetEntryLength + ValueLength);
            _memory = provider.Rent(length);
            _buffer = _memory.GetSpan();
            newBitmap.CopyTo(_buffer);

            _previousBitmap = previousBitmap;
            _newBitmap = _buffer[..BitmapLength];
            _nodeBase = BitmapLength + liveCount * OffsetEntryLength;
            _changes = changes;
            _previousSlot = 0;
            _changeIndex = 0;
            _slot = 0;

            int previousOffsetsLength = previousLiveCount * OffsetEntryLength;
            _previousOffsets = blob.IsEmpty
                ? default
                : blob.Slice(BitmapLength, previousOffsetsLength);
            _previousNodes = blob.IsEmpty
                ? default
                : blob[(BitmapLength + previousOffsetsLength)..];
        }

        /// <summary>Runs the post-order rebuild into <see cref="Blob"/> and sets <see cref="SubtreeRoot"/>.</summary>
        public void Build() => SubtreeRoot = Resolve(Rebuild(0, LeafCount, RootPosition));

        public void Dispose()
        {
            RefCountingMemory? memory = _memory;
            if (memory is not null)
            {
                _memory = null;
                ((IDisposable)memory).Dispose();
            }
        }

        private NodeRef Rebuild(int low, int high, int position)
        {
            if (_changeIndex >= _changes.Count || _changes.SubIndexAt(_changeIndex) >= high)
            {
                return CopyCleanSubtree(high - low, position);
            }

            if (high - low == 1)
            {
                Debug.Assert(_changes.SubIndexAt(_changeIndex) == low);
                if (IsPresent(_previousBitmap, (byte)low)) _previousSlot++;

                ValueHash256 value = _changes.Get(_changeIndex++);
                if (value == default) return NodeRef.Empty;

                return NodeRef.Leaf(Append(position, value.Bytes));
            }

            int width = high - low;
            int middle = low + width / 2;
            NodeRef left = Rebuild(low, middle, position - width);
            NodeRef right = Rebuild(middle, high, position - 1);

            if (RangeHasLeaf(_previousBitmap, low, high)) _previousSlot++;
            if (!RangeHasLeaf(_newBitmap, low, high)) return NodeRef.Empty;

            ValueHash256 hash = Blake3Hash.HashPairOrZero(Resolve(left), Resolve(right));
            return NodeRef.Internal(Append(position, hash.Bytes));
        }

        /// <summary>Materializes the hash a rebuilt node contributes to its parent.</summary>
        /// <remarks>
        /// A leaf entry stores its value, so its contributed hash is <c>blake3(value)</c>; an internal
        /// entry stores its already-cached hash, returned verbatim; an empty subtree folds to zero.
        /// </remarks>
        private readonly ValueHash256 Resolve(in NodeRef node) => node.Kind switch
        {
            NodeKind.Empty => default,
            NodeKind.Leaf => Blake3Hash.Hash(NodeAt(node.Slot)),
            _ => new ValueHash256(NodeAt(node.Slot)),
        };

        private NodeRef CopyCleanSubtree(int width, int position)
        {
            int firstSlot = _previousSlot;
            int previousCount = _previousOffsets.Length / OffsetEntryLength;
            while (_previousSlot < previousCount && ReadOffset(_previousOffsets, _previousSlot) <= position)
            {
                _previousSlot++;
            }

            int count = _previousSlot - firstSlot;
            if (count == 0) return NodeRef.Empty;

            _previousOffsets.Slice(firstSlot * OffsetEntryLength, count * OffsetEntryLength)
                .CopyTo(_buffer.Slice(BitmapLength + _slot * OffsetEntryLength, count * OffsetEntryLength));
            _previousNodes.Slice(firstSlot * ValueLength, count * ValueLength)
                .CopyTo(_buffer.Slice(_nodeBase + _slot * ValueLength, count * ValueLength));
            _slot += count;

            int rootSlot = _slot - 1;
            return width == 1 ? NodeRef.Leaf(rootSlot) : NodeRef.Internal(rootSlot);
        }

        private int Append(int position, ReadOnlySpan<byte> node)
        {
            WriteOffset(_buffer.Slice(BitmapLength + _slot * OffsetEntryLength, OffsetEntryLength), position);
            node.CopyTo(_buffer.Slice(_nodeBase + _slot * ValueLength, ValueLength));
            return _slot++;
        }

        private readonly ReadOnlySpan<byte> NodeAt(int slot) => _buffer.Slice(_nodeBase + slot * ValueLength, ValueLength);
    }
}
