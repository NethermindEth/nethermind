// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using static Nethermind.Pbt.TrieUpdater;

namespace Nethermind.Pbt;

internal static partial class TrieUpdater<TKey, TPath>
    where TKey : struct, IPbtKey<TKey>
    where TPath : struct, IPbtNodePath<TPath>
{
    /// <summary>A group-local node, borrowing original encodings from its reader.</summary>
    internal struct Subtree
    {
        internal readonly NodeKind Kind;
        internal readonly ReadOnlyMemory<byte> Encoding;
        internal readonly TKey LeafKey;
        internal readonly ValueHash256 ValueOrLeft;
        private readonly ValueHash256 _right;

        internal Subtree(ReadOnlyMemory<byte> encoding, NodeGroupPath path)
        {
            Kind = NodeKind.Original;
            Encoding = encoding;
            Path = path;
        }

        internal Subtree(PbtWriteOperation<TKey> operation)
        {
            Kind = NodeKind.Leaf;
            LeafKey = operation.Key;
            ValueOrLeft = operation.Value;
        }

        internal Subtree(NodeGroupPath path, in ValueHash256 left, in ValueHash256 right)
        {
            Kind = NodeKind.Branch;
            ValueOrLeft = left;
            _right = right;
            Path = path;
        }

        internal readonly NodeGroupPath Path { get; }
        internal readonly PbtNodeReader Reader => PbtNodeReader.FromValidated(Encoding.Span);
        internal readonly bool IsEmpty => Kind == NodeKind.Empty;
        internal readonly bool IsLeaf => Kind == NodeKind.Leaf || (Kind == NodeKind.Original && Reader.IsLeaf);
        internal readonly TKey Key => Kind == NodeKind.Leaf ? LeafKey : TKey.Create(Reader.Key);
        internal readonly CompressedPrefix Prefix => Kind == NodeKind.Branch ? default : Reader.Prefix;
        internal readonly ValueHash256 LeftHash => Kind == NodeKind.Branch ? ValueOrLeft : Reader.LeftHash;
        internal readonly ValueHash256 RightHash => Kind == NodeKind.Branch ? _right : Reader.RightHash;

        internal static Subtree Move(ref Subtree source)
        {
            Subtree result = source;
            source = default;
            return result;
        }
    }

    /// <summary>A node paired with its borrowed source-group cursor, distinct from its eventual placement.</summary>
    /// <remarks>The source prefix and encoding must remain valid until the view is consumed or materialized.</remarks>
    internal ref struct TraversalSubtree(PbtTraversalPath groupPath, Subtree node)
    {
        internal Subtree Node = node;
        internal readonly PbtTraversalPath GroupPath = groupPath;
        internal readonly bool IsEmpty => Node.IsEmpty;
        internal readonly bool IsLeaf => Node.IsLeaf;
        internal readonly int AnchorDepth => GroupPath.BitDepth + Node.Path.Length;
        internal readonly int BranchDepth => AnchorDepth + Node.Prefix.BitCount;

        internal readonly int PrefixBit(int bit)
        {
            if (bit < GroupPath.BitDepth) return GetBit(GroupPath.Bytes, bit);
            if (bit < AnchorDepth) return Node.Path.GetBit(bit - GroupPath.BitDepth);
            return GetBit(Node.Prefix.Bytes, bit - AnchorDepth);
        }

        internal readonly int FirstDifferingBit(TKey key, int start)
        {
            int end = Math.Min(BranchDepth, key.BitLength);
            int anchorEnd = Math.Min(AnchorDepth, end);
            if (start < Math.Min(GroupPath.BitDepth, anchorEnd))
            {
                int difference = PbtKeyOperations.FirstDifferingBit(GroupPath.Bytes, key.Bytes, start);
                if (difference < Math.Min(GroupPath.BitDepth, anchorEnd)) return difference;
                start = Math.Min(GroupPath.BitDepth, anchorEnd);
            }
            while (start < anchorEnd)
            {
                if (PrefixBit(start) != key.GetBit(start)) return start;
                start++;
            }
            if (start < end)
                return AnchorDepth + MatchingPrefixBits(Node.Prefix, key, AnchorDepth);
            return end;
        }

        internal readonly int EncodedLength(int depth) => IsLeaf
            ? (Node.Kind == NodeKind.Leaf ? 3 + Node.LeafKey.Length + 32 : Node.Encoding.Length)
            : 3 + PbtBitPrefix.ByteCount(BranchDepth - depth) + 64;

        internal readonly ValueHash256 Hash(int depth)
        {
            if (IsEmpty) return default;
            if (Node.Kind == NodeKind.Original && (IsLeaf || depth == AnchorDepth))
                return PbtNodeCodec.Hash(Node.Reader);
            Span<byte> encoding = stackalloc byte[EncodedLength(depth)];
            return Encode(encoding, depth);
        }

        internal readonly ValueHash256 Encode(Span<byte> encoding, int depth)
        {
            if (Node.Kind == NodeKind.Original && (IsLeaf || depth == AnchorDepth))
            {
                Node.Encoding.Span.CopyTo(encoding);
                return PbtNodeCodec.Hash(Node.Reader);
            }
            if (IsLeaf)
            {
                PbtNodeCodec.EncodeLeaf(encoding, Node.LeafKey, Node.ValueOrLeft.Bytes);
                return PbtNodeCodec.Hash(PbtNodeReader.FromValidated(encoding));
            }

            // Promotion absorbs the source anchor's skipped bits into the relative compressed prefix.
            int bitCount = BranchDepth - depth;
            PbtNodeCodec.CreateBranchEncoding(encoding, bitCount, Node.LeftHash, Node.RightHash);
            CopyBranchBits(depth, bitCount, encoding.Slice(3, PbtBitPrefix.ByteCount(bitCount)));
            return Blake3Hash.Hash(encoding);
        }

        private readonly void CopyBranchBits(int start, int count, Span<byte> destination)
        {
            int end = start + count;
            int groupEnd = Math.Min(end, GroupPath.BitDepth);
            if (start < groupEnd)
                PbtBitPrefix.CopyBits(GroupPath.Bytes, start, groupEnd - start, destination, 0);
            for (int bit = Math.Max(start, GroupPath.BitDepth); bit < Math.Min(end, AnchorDepth); bit++)
                destination[(bit - start) >> 3] |= (byte)(Node.Path.GetBit(bit - GroupPath.BitDepth) << (7 - ((bit - start) & 7)));
            int prefixStart = Math.Max(start, AnchorDepth);
            if (prefixStart < end)
                PbtBitPrefix.CopyBits(Node.Prefix.Bytes, prefixStart - AnchorDepth, end - prefixStart, destination, prefixStart - start);
        }

        internal readonly OwnedSubtree Materialize()
        {
            if (IsEmpty) return default;
            if (IsLeaf)
            {
                Subtree leaf = Node.Kind == NodeKind.Leaf ? Node : new(new PbtWriteOperation<TKey>(Node.Key, new ValueHash256(Node.Reader.Value)));
                return new(default, leaf);
            }

            int splitDepth = BranchDepth;
            int groupDepth = splitDepth / 4 * 4;
            int localLength = splitDepth - groupDepth;
            Span<byte> branchBytes = stackalloc byte[PbtBitPrefix.ByteCount(splitDepth)];
            branchBytes.Clear();
            CopyBranchBits(0, splitDepth, branchBytes);
            int slot = localLength == 0 ? 0 : branchBytes[groupDepth >> 3] >> (4 - (groupDepth & 7)) & 15;
            if (localLength != 0) slot &= 15 << (4 - localLength);
            Span<byte> groupBytes = branchBytes[..PbtBitPrefix.ByteCount(groupDepth)];
            if ((groupDepth & 7) != 0) groupBytes[^1] &= 0xF0;
            return new(TPath.Create(groupBytes, groupDepth), new Subtree(new NodeGroupPath(slot, localLength), Node.LeftHash, Node.RightHash));
        }

        internal static TraversalSubtree Move(ref TraversalSubtree source) => new(source.GroupPath, Subtree.Move(ref source.Node));
    }

    /// <summary>A detached result whose group anchor survives traversal-buffer reuse.</summary>
    internal struct OwnedSubtree(TPath groupPath, Subtree node)
    {
        internal TPath GroupPath = groupPath;
        internal Subtree Node = node;
        internal readonly bool IsEmpty => Node.IsEmpty;
        internal readonly TraversalSubtree Borrow(Span<byte> buffer) => new(PbtTraversalPath.FromPath(buffer, GroupPath), Node);

        internal static OwnedSubtree TakeFrom<TSourceKey, TSourcePath>(ref TrieUpdater<TSourceKey, TSourcePath>.OwnedSubtree source)
            where TSourceKey : struct, IPbtKey<TSourceKey>
            where TSourcePath : struct, IPbtNodePath<TSourcePath>
        {
            OwnedSubtree result = default;
            if (!source.IsEmpty)
            {
                if (source.Node.IsLeaf)
                    result.Node = new(new PbtWriteOperation<TKey>(TKey.Create(source.Node.Key.Bytes), source.Node.ValueOrLeft));
                else
                {
                    Debug.Assert(source.Node.Kind == NodeKind.Branch);
                    result = new(source.GroupPath.ToPath<TPath>(), new Subtree(source.Node.Path, source.Node.LeftHash, source.Node.RightHash));
                }
            }
            source = default;
            return result;
        }
    }

    /// <summary>A borrowed frontier node or group position for deferred acquisition.</summary>
    internal struct DecompositionEntry
    {
        internal Subtree Node;
        private readonly byte _sourcePositionPlusOne;
        internal readonly int SourcePosition => _sourcePositionPlusOne - 1;
        internal readonly bool IsEmpty => _sourcePositionPlusOne == 0 && Node.IsEmpty;
        internal DecompositionEntry(ValueHash256 hash, int position) => _sourcePositionPlusOne = hash == default ? (byte)0 : (byte)(position + 1);
        internal DecompositionEntry(ref Subtree subtree) => Node = Subtree.Move(ref subtree);

        internal Subtree TakeSubtree(ref GroupFrameReader<TKey, TPath> reader, PbtNodeGroupWriter<TPath> writer, scoped in PbtTraversalPath path)
        {
            Subtree subtree = _sourcePositionPlusOne != 0 ? reader.Take(path, writer, SourcePosition) : Subtree.Move(ref Node);
            this = default;
            return subtree;
        }
    }

    /// <summary>Keeps foreign result anchors separate from compact local frontier entries.</summary>
    internal struct Frontier
    {
        internal EntryBuffer Entries;
        private AnchorBuffer _anchors;
        private ushort _anchorMask;
        internal uint Mask;

        internal TraversalSubtree Take(scoped ref GroupFrameReader<TKey, TPath> reader, PbtNodeGroupWriter<TPath> writer, PbtTraversalPath path, int slot, Span<byte> scratch)
        {
            Subtree node = Entries[slot].TakeSubtree(ref reader, writer, path);
            PbtTraversalPath sourcePath = path;
            if ((_anchorMask & (1 << slot)) != 0)
            {
                sourcePath = PbtTraversalPath.FromPath(scratch, _anchors[slot]);
                _anchorMask &= (ushort)~(1 << slot);
                _anchors[slot] = default;
            }
            return new(sourcePath, node);
        }

        internal void Set(PbtTraversalPath path, int slot, ref TraversalSubtree result)
        {
            _anchorMask &= (ushort)~(1 << slot);
            _anchors[slot] = default;
            if (!result.IsEmpty && !result.IsLeaf &&
                (path.BitDepth != result.GroupPath.BitDepth || !path.Bytes.SequenceEqual(result.GroupPath.Bytes)))
            {
                // A descendant cursor may have filled the unused tail of this borrowed ancestor view.
                _anchors[slot] = PbtNodePathOperations.Prefix<TPath>(result.GroupPath.Bytes, result.GroupPath.BitDepth, result.GroupPath.BitDepth);
                _anchorMask |= (ushort)(1 << slot);
            }
            Entries[slot] = new(ref result.Node);
        }
    }

    [InlineArray(PbtFourLevelGroupGeometry.BoundarySlots)]
    internal struct EntryBuffer { private DecompositionEntry _element; }

    [InlineArray(PbtFourLevelGroupGeometry.BoundarySlots)]
    private struct AnchorBuffer { private TPath _element; }
}
