// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using Nethermind.Core.Collections;
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
    private const int TreeDepth = 8;
    private const int NodesPerLeafPath = TreeDepth + 1;
    private const int RootPosition = 2 * LeafCount - 2;
    private const int MaxNodeCount = 2 * LeafCount - 1;

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
    /// Applies <paramref name="changes"/> (each a 32-byte leaf value; a zero value clears the leaf)
    /// to <paramref name="blob"/>, returning the new blob and, via <paramref name="subtreeRoot"/>,
    /// its merkelized 256-leaf subtree root. Returns an empty array (and a zero root) when no leaves remain.
    /// </summary>
    /// <remarks>
    /// Rebuilds dirty paths in post-order and copies clean subtree entries verbatim. A present leaf
    /// contributes <c>blake3(value)</c>; higher levels use the EIP-8297 pair hash, with empty
    /// subtrees folding to zero.
    /// </remarks>
    public static byte[] Apply(ReadOnlySpan<byte> blob, IReadOnlyDictionary<byte, ValueHash256> changes, out ValueHash256 subtreeRoot)
    {
        Span<byte> previousBitmap = stackalloc byte[BitmapLength];
        previousBitmap.Clear();
        if (!blob.IsEmpty) blob[..BitmapLength].CopyTo(previousBitmap);

        Span<byte> bitmap = stackalloc byte[BitmapLength];
        previousBitmap.CopyTo(bitmap);

        using ArrayPoolListRef<LeafChange> sortedChanges = new(changes.Count);
        foreach ((byte subIndex, ValueHash256 value) in changes)
        {
            sortedChanges.Add(new LeafChange(subIndex, value));
            SetPresent(bitmap, subIndex, value != default);
        }

        if (!RangeHasLeaf(bitmap, 0, LeafCount))
        {
            subtreeRoot = default;
            return [];
        }

        sortedChanges.Sort(static (left, right) => left.SubIndex.CompareTo(right.SubIndex));

        int previousLiveCount = GetLiveCount(blob);
        int estimatedLiveCount = Math.Min(MaxNodeCount, previousLiveCount + NodesPerLeafPath * sortedChanges.Count);
        using RebuildState state = new(
            blob,
            previousBitmap,
            bitmap,
            sortedChanges.AsSpan(),
            previousLiveCount,
            estimatedLiveCount);

        subtreeRoot = state.Rebuild(0, LeafCount, RootPosition);
        return state.Assemble(bitmap);
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

    private readonly record struct LeafChange(byte SubIndex, ValueHash256 Value);

    private ref struct RebuildState
    {
        private readonly ReadOnlySpan<byte> _previousBitmap;
        private readonly ReadOnlySpan<byte> _newBitmap;
        private readonly ReadOnlySpan<byte> _previousOffsets;
        private readonly ReadOnlySpan<byte> _previousNodes;
        private readonly ReadOnlySpan<LeafChange> _changes;
        private ArrayPoolListRef<byte> _offsets;
        private ArrayPoolListRef<byte> _nodes;
        private int _previousSlot;
        private int _changeIndex;

        public RebuildState(
            ReadOnlySpan<byte> blob,
            ReadOnlySpan<byte> previousBitmap,
            ReadOnlySpan<byte> newBitmap,
            ReadOnlySpan<LeafChange> changes,
            int previousLiveCount,
            int estimatedLiveCount)
        {
            _previousBitmap = previousBitmap;
            _newBitmap = newBitmap;
            _changes = changes;
            _previousSlot = 0;
            _changeIndex = 0;

            int previousOffsetsLength = previousLiveCount * OffsetEntryLength;
            _previousOffsets = blob.IsEmpty
                ? default
                : blob.Slice(BitmapLength, previousOffsetsLength);
            _previousNodes = blob.IsEmpty
                ? default
                : blob[(BitmapLength + previousOffsetsLength)..];

            _offsets = new ArrayPoolListRef<byte>(estimatedLiveCount * OffsetEntryLength);
            _nodes = new ArrayPoolListRef<byte>(estimatedLiveCount * ValueLength);
        }

        public ValueHash256 Rebuild(int low, int high, int position)
        {
            if (_changeIndex >= _changes.Length || _changes[_changeIndex].SubIndex >= high)
            {
                return CopyCleanSubtree(high - low, position);
            }

            if (high - low == 1)
            {
                Debug.Assert(_changes[_changeIndex].SubIndex == low);
                if (IsPresent(_previousBitmap, (byte)low)) _previousSlot++;

                ValueHash256 value = _changes[_changeIndex++].Value;
                if (value == default) return default;

                Append(position, value);
                return Blake3Hash.Hash(value.Bytes);
            }

            int width = high - low;
            int middle = low + width / 2;
            ValueHash256 left = Rebuild(low, middle, position - width);
            ValueHash256 right = Rebuild(middle, high, position - 1);

            if (RangeHasLeaf(_previousBitmap, low, high)) _previousSlot++;
            if (!RangeHasLeaf(_newBitmap, low, high)) return default;

            ValueHash256 hash = Blake3Hash.HashPairOrZero(left, right);
            Append(position, hash);
            return hash;
        }

        public readonly byte[] Assemble(ReadOnlySpan<byte> bitmap)
        {
            Debug.Assert(_offsets.Count / OffsetEntryLength == _nodes.Count / ValueLength);

            byte[] result = new byte[BitmapLength + _offsets.Count + _nodes.Count];
            bitmap.CopyTo(result);
            _offsets.AsSpan().CopyTo(result.AsSpan(BitmapLength));
            _nodes.AsSpan().CopyTo(result.AsSpan(BitmapLength + _offsets.Count));
            return result;
        }

        public void Dispose()
        {
            _offsets.Dispose();
            _nodes.Dispose();
        }

        private ValueHash256 CopyCleanSubtree(int width, int position)
        {
            int firstSlot = _previousSlot;
            int previousCount = _previousOffsets.Length / OffsetEntryLength;
            while (_previousSlot < previousCount && ReadOffset(_previousOffsets, _previousSlot) <= position)
            {
                _previousSlot++;
            }

            int count = _previousSlot - firstSlot;
            if (count == 0) return default;

            _offsets.AddRange(_previousOffsets.Slice(firstSlot * OffsetEntryLength, count * OffsetEntryLength));
            _nodes.AddRange(_previousNodes.Slice(firstSlot * ValueLength, count * ValueLength));

            ReadOnlySpan<byte> root = _previousNodes.Slice((_previousSlot - 1) * ValueLength, ValueLength);
            return width == 1 ? Blake3Hash.Hash(root) : new ValueHash256(root);
        }

        private void Append(int position, in ValueHash256 node)
        {
            Span<byte> encodedOffset = stackalloc byte[OffsetEntryLength];
            WriteOffset(encodedOffset, position);
            _offsets.AddRange(encodedOffset);
            _nodes.AddRange(node.Bytes);
        }
    }
}
