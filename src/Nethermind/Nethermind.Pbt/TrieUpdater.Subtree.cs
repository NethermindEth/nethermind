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
    /// <remarks>
    /// An original node is always a stored branch: the root leaf is acquired as a <see cref="NodeKind.Leaf"/> and every
    /// other leaf is inlined in its parent branch, so a leaf is only ever its key and hash. A composed branch carries the
    /// keys of its leaf children, flagged by <see cref="LeafChildren"/> because fixed-length key types have no empty value.
    /// </remarks>
    internal struct Subtree
    {
        internal const byte LeftLeaf = 1;
        internal const byte RightLeaf = 2;

        internal readonly NodeKind Kind;
        internal readonly ReadOnlyMemory<byte> Encoding;
        /// <summary>A leaf's key, or a composed branch's left leaf key.</summary>
        internal readonly TKey LeafKey;
        private readonly TKey _rightLeafKey;
        /// <summary>A leaf's hash, or a branch's left child hash.</summary>
        internal readonly ValueHash256 HashOrLeft;
        private readonly ValueHash256 _right;
        /// <summary>The hash already computed for this node, or default when it must be computed.</summary>
        internal readonly ValueHash256 KnownHash;
        /// <summary>The compressed-prefix bit count of the encoding <see cref="KnownHash"/> is the hash of.</summary>
        internal readonly ushort KnownHashBitCount;
        /// <summary>Which children of a composed branch are leaves: <see cref="LeftLeaf"/> and <see cref="RightLeaf"/> bits.</summary>
        internal readonly byte LeafChildren;

        internal Subtree(ReadOnlyMemory<byte> encoding, NodeGroupPath path, in ValueHash256 knownHash)
        {
            Kind = NodeKind.Original;
            Encoding = encoding;
            Path = path;
            KnownHash = knownHash;
            KnownHashBitCount = (ushort)Reader.Prefix.BitCount;
        }

        private Subtree(in Subtree source, in ValueHash256 knownHash, int knownHashBitCount)
        {
            this = source;
            KnownHash = knownHash;
            KnownHashBitCount = (ushort)knownHashBitCount;
        }

        internal Subtree(TKey key, in ValueHash256 hash)
        {
            Kind = NodeKind.Leaf;
            LeafKey = key;
            HashOrLeft = hash;
        }

        internal Subtree(NodeGroupPath path, in ValueHash256 left, in ValueHash256 right, TKey leftLeafKey, TKey rightLeafKey, byte leafChildren)
        {
            Kind = NodeKind.Branch;
            HashOrLeft = left;
            _right = right;
            Path = path;
            LeafKey = leftLeafKey;
            _rightLeafKey = rightLeafKey;
            LeafChildren = leafChildren;
        }

        internal Subtree(NodeGroupPath path, in ValueHash256 left, in ValueHash256 right, in ValueHash256 knownHash)
            : this(path, left, right, default, default, 0) => KnownHash = knownHash;

        /// <summary>This node carrying the hash of its encoding with a <paramref name="bitCount"/>-bit prefix, so that encoding is not hashed again.</summary>
        internal readonly Subtree WithKnownHash(in ValueHash256 hash, int bitCount) => new(this, hash, bitCount);

        internal readonly NodeGroupPath Path { get; }
        internal readonly PbtNodeReader Reader => PbtNodeReader.FromValidated(Encoding.Span);
        internal readonly bool IsEmpty => Kind == NodeKind.Empty;
        internal readonly bool IsLeaf => Kind == NodeKind.Leaf;
        internal readonly ValueHash256 LeafHash => HashOrLeft;
        internal readonly CompressedPrefix Prefix => Kind == NodeKind.Branch ? default : Reader.Prefix;
        internal readonly ValueHash256 LeftHash => Kind == NodeKind.Branch ? HashOrLeft : Reader.LeftHash;
        internal readonly ValueHash256 RightHash => Kind == NodeKind.Branch ? _right : Reader.RightHash;
        /// <summary>Which children are leaves, for an original branch read from its trailer.</summary>
        internal readonly byte LeafChildrenMask => Kind == NodeKind.Branch
            ? LeafChildren
            : (byte)((Reader.LeftKey.IsEmpty ? 0 : LeftLeaf) | (Reader.RightKey.IsEmpty ? 0 : RightLeaf));
        internal readonly bool HasLeftLeaf => (LeafChildrenMask & LeftLeaf) != 0;
        internal readonly bool HasRightLeaf => (LeafChildrenMask & RightLeaf) != 0;
        /// <summary>The left child's complete key; only meaningful when <see cref="HasLeftLeaf"/>.</summary>
        internal readonly TKey LeftLeafKey => Kind == NodeKind.Branch ? LeafKey : TKey.Create(Reader.LeftKey);
        /// <summary>The right child's complete key; only meaningful when <see cref="HasRightLeaf"/>.</summary>
        internal readonly TKey RightLeafKey => Kind == NodeKind.Branch ? _rightLeafKey : TKey.Create(Reader.RightKey);
        internal readonly int LeftLeafKeyLength => Kind == NodeKind.Branch ? (HasLeftLeaf ? LeafKey.Length : 0) : Reader.LeftKey.Length;
        internal readonly int RightLeafKeyLength => Kind == NodeKind.Branch ? (HasRightLeaf ? _rightLeafKey.Length : 0) : Reader.RightKey.Length;

        /// <summary>Writes the inline leaf keys that follow a branch's preimage.</summary>
        internal readonly void WriteLeafKeys(Span<byte> trailer)
        {
            if (Kind != NodeKind.Branch)
            {
                PbtNodeReader reader = Reader;
                PbtNodeCodec.WriteBranchTrailer(trailer, reader.LeftKey, reader.RightKey);
                return;
            }
            // Each key is copied in its own statement: the spans borrow defensive copies of the readonly fields, and two
            // such same-typed temporaries in one call would share a slot.
            int leftLength = LeftLeafKeyLength;
            PbtNodeCodec.WriteBranchTrailer(trailer, leftLength, RightLeafKeyLength);
            if (HasLeftLeaf) LeafKey.Bytes.CopyTo(trailer[PbtNodeCodec.BranchTrailerHeaderLength..]);
            if (HasRightLeaf) _rightLeafKey.Bytes.CopyTo(trailer[(PbtNodeCodec.BranchTrailerHeaderLength + leftLength)..]);
        }

        /// <summary>Copies this branch's children into a composed branch anchored at <paramref name="path"/>.</summary>
        internal readonly Subtree CopyBranch(NodeGroupPath path) =>
            new(path, LeftHash, RightHash, HasLeftLeaf ? LeftLeafKey : default, HasRightLeaf ? RightLeafKey : default, LeafChildrenMask);

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

        /// <summary>The stored length at <paramref name="depth"/>; a leaf is only stored as the tree root.</summary>
        internal readonly int EncodedLength(int depth) => IsLeaf
            ? PbtNodeCodec.LeafLength(Node.LeafKey.Length)
            : PbtNodeCodec.BranchLength(BranchDepth - depth, Node.LeftLeafKeyLength, Node.RightLeafKeyLength);

        // Encode writes every byte of the encoding it is given.
        [SkipLocalsInit]
        internal readonly ValueHash256 Hash(int depth, TrieUpdaterMetrics? metrics)
        {
            if (IsEmpty) return default;
            if (IsLeaf) return Node.LeafHash;
            if (Node.Kind == NodeKind.Original && depth == AnchorDepth)
                return SourceHash(metrics);
            Span<byte> encoding = stackalloc byte[EncodedLength(depth)];
            return Encode(encoding, depth, metrics);
        }

        internal readonly ValueHash256 Encode(Span<byte> encoding, int depth, TrieUpdaterMetrics? metrics)
        {
            ValueHash256 hash = EncodeDeferringHash(encoding, depth, metrics, out int preimageLength);
            if (preimageLength == 0) return hash;
            metrics?.IncrementNodeHashes();
            return Blake3Hash.Hash(encoding[..preimageLength]);
        }

        /// <summary>
        /// <see cref="Encode"/>, except that a branch whose hash is not yet known is left unhashed: the result is
        /// default and <paramref name="preimageLength"/> the length of its preimage at the start of
        /// <paramref name="encoding"/>, so the caller can hash it together with another node.
        /// </summary>
        internal readonly ValueHash256 EncodeDeferringHash(Span<byte> encoding, int depth, TrieUpdaterMetrics? metrics, out int preimageLength)
        {
            preimageLength = 0;
            if (IsLeaf)
            {
                PbtNodeCodec.EncodeLeaf(encoding, Node.LeafKey);
                return Node.LeafHash;
            }
            if (Node.Kind == NodeKind.Original && depth == AnchorDepth)
            {
                Node.Encoding.Span.CopyTo(encoding);
                return SourceHash(metrics);
            }

            // Promotion absorbs the source anchor's skipped bits into the relative compressed prefix.
            int bitCount = BranchDepth - depth;
            PbtNodeCodec.CreateBranchEncoding(encoding, bitCount, Node.LeftHash, Node.RightHash);
            CopyBranchBits(depth, bitCount, encoding.Slice(3, PbtBitPrefix.ByteCount(bitCount)));
            int branchPreimageLength = PbtNodeCodec.BranchPreimageLength(bitCount);
            Node.WriteLeafKeys(encoding[branchPreimageLength..]);
            // An omitted branch reacquired at its own anchor, or a published group root written into its parent
            // group, is the node already hashed at this prefix length.
            if (Node.KnownHash != default && bitCount == Node.KnownHashBitCount) return Node.KnownHash;
            preimageLength = branchPreimageLength;
            return default;
        }

        private readonly ValueHash256 SourceHash(TrieUpdaterMetrics? metrics)
        {
            if (Node.KnownHash != default) return Node.KnownHash;
            metrics?.IncrementNodeHashes();
            return PbtNodeCodec.Hash(Node.Reader);
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

        [SkipLocalsInit]
        internal readonly OwnedSubtree Materialize()
        {
            if (IsEmpty) return default;
            if (IsLeaf) return new(default, Node);

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
            return new(TPath.Create(groupBytes, groupDepth), Node.CopyBranch(new NodeGroupPath(slot, localLength)));
        }

        internal static TraversalSubtree Move(ref TraversalSubtree source) => new(source.GroupPath, Subtree.Move(ref source.Node));
    }

    /// <summary>A detached result whose group anchor survives traversal-buffer reuse.</summary>
    internal struct OwnedSubtree(TPath groupPath, Subtree node)
    {
        internal TPath GroupPath = groupPath;
        internal Subtree Node = node;
        /// <summary>The change in stored size across the groups this result was folded from, still owed to the caller's boundary slot.</summary>
        internal long SizeDelta;
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
                    result.Node = new(TKey.Create(source.Node.LeafKey.Bytes), source.Node.LeafHash);
                else
                {
                    Debug.Assert(source.Node.Kind == NodeKind.Branch);
                    result = new(source.GroupPath.ToPath<TPath>(), new Subtree(source.Node.Path, source.Node.LeftHash, source.Node.RightHash,
                        source.Node.HasLeftLeaf ? TKey.Create(source.Node.LeftLeafKey.Bytes) : default,
                        source.Node.HasRightLeaf ? TKey.Create(source.Node.RightLeafKey.Bytes) : default,
                        source.Node.LeafChildrenMask));
                }
            }
            result.SizeDelta = source.SizeDelta;
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
