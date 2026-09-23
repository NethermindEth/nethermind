// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
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
    /// A composed branch reaching past its group also owns the compressed prefix below it, so that it stays readable
    /// against the cursor that placed it rather than against a group path of its own.
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
            : this(path, left, right, leftLeafKey, rightLeafKey, leafChildren, default) { }

        /// <param name="prefix">The owned compressed prefix below this branch's anchor, empty when it branches at its anchor.</param>
        internal Subtree(NodeGroupPath path, in ValueHash256 left, in ValueHash256 right, TKey leftLeafKey, TKey rightLeafKey, byte leafChildren,
            ReadOnlyMemory<byte> prefix)
        {
            Kind = NodeKind.Branch;
            Encoding = prefix;
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
        internal readonly CompressedPrefix Prefix => Kind != NodeKind.Branch
            ? Reader.Prefix
            : Encoding.IsEmpty ? default : CompressedPrefix.FromValidated(Encoding.Span);
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

        /// <summary>Copies this branch's children into a composed branch anchored at <paramref name="path"/>, over <paramref name="prefix"/>.</summary>
        internal readonly Subtree CopyBranch(NodeGroupPath path, ReadOnlyMemory<byte> prefix) =>
            new(path, LeftHash, RightHash, HasLeftLeaf ? LeftLeafKey : default, HasRightLeaf ? RightLeafKey : default, LeafChildrenMask, prefix);

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

        /// <summary>Detaches this view for a caller that addresses it at <paramref name="anchorDepth"/>.</summary>
        /// <remarks>
        /// The result is read against that caller's cursor: its group path is the cursor itself, the four levels below
        /// are its <see cref="NodeGroupPath"/>, and anything deeper becomes an owned compressed prefix. Detaching copies
        /// the node out of its source group, so the result outlives the frame it was read from.
        /// </remarks>
        internal readonly OwnedSubtree Materialize(int anchorDepth)
        {
            Debug.Assert(anchorDepth % PbtFourLevelGroupGeometry.LevelsPerGroup == 0, "A result is anchored at a group depth.");
            if (IsEmpty) return default;
            if (IsLeaf) return new(Node);

            int splitDepth = BranchDepth;
            Debug.Assert(splitDepth >= anchorDepth, "A result branches at or below the cursor that addresses it.");
            int localLength = Math.Min(splitDepth - anchorDepth, PbtFourLevelGroupGeometry.LevelsPerGroup);
            int slot = 0;
            for (int bit = anchorDepth; bit < anchorDepth + localLength; bit++) slot = (slot << 1) | PrefixBit(bit);
            return new(Node.CopyBranch(new NodeGroupPath(slot << (PbtFourLevelGroupGeometry.LevelsPerGroup - localLength), localLength),
                OwnedPrefix(anchorDepth + localLength, splitDepth)));
        }

        /// <summary>The bits from <paramref name="anchorDepth"/> to <paramref name="splitDepth"/> as a standalone compressed prefix.</summary>
        private readonly ReadOnlyMemory<byte> OwnedPrefix(int anchorDepth, int splitDepth)
        {
            int bitCount = splitDepth - anchorDepth;
            if (bitCount == 0) return default;
            // Zeroed, because the bits are copied in by disjunction.
            byte[] prefix = new byte[sizeof(ushort) + PbtBitPrefix.ByteCount(bitCount)];
            BinaryPrimitives.WriteUInt16BigEndian(prefix, (ushort)bitCount);
            CopyBranchBits(anchorDepth, bitCount, prefix.AsSpan(sizeof(ushort)));
            return prefix;
        }
    }

    /// <summary>A detached result, read against the cursor of the caller it was materialized for.</summary>
    /// <remarks>
    /// The result owns everything below that cursor, so it survives traversal-buffer reuse and the release of the group
    /// it was read from, but it is only meaningful paired with a cursor at its anchor depth.
    /// </remarks>
    internal struct OwnedSubtree(Subtree node)
    {
        internal Subtree Node = node;
        /// <summary>The change in stored size across the groups this result was folded from, still owed to the caller's boundary slot.</summary>
        internal long SizeDelta;
        internal readonly bool IsEmpty => Node.IsEmpty;
        internal readonly TraversalSubtree Borrow(in PbtTraversalPath cursor) => new(cursor, Node);

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
                    result = new(new Subtree(source.Node.Path, source.Node.LeftHash, source.Node.RightHash,
                        source.Node.HasLeftLeaf ? TKey.Create(source.Node.LeftLeafKey.Bytes) : default,
                        source.Node.HasRightLeaf ? TKey.Create(source.Node.RightLeafKey.Bytes) : default,
                        source.Node.LeafChildrenMask, source.Node.Encoding));
                }
            }
            result.SizeDelta = source.SizeDelta;
            source = default;
            return result;
        }
    }

    /// <summary>A composed node held for composition, or the place the frame reads a decomposed one from.</summary>
    /// <remarks>
    /// Decomposition never carries a node here: it records where the node is, so an untouched subtree is left in its
    /// frame until composition claims it, and a boundary node is sliced out only by the fold that consumes it.
    /// </remarks>
    internal struct DecompositionEntry
    {
        internal Subtree Node;
        private readonly byte _source;

        internal DecompositionEntry(ref Subtree subtree) => Node = Subtree.Move(ref subtree);
        internal DecompositionEntry(EntrySource source, int sourcePosition)
        {
            Debug.Assert(source != EntrySource.Node, "A decomposed entry names where its node is read from.");
            _source = (byte)((sourcePosition << 2) | (int)source);
        }

        internal readonly EntrySource Source => (EntrySource)(_source & 3);
        internal readonly int SourcePosition => _source >> 2;
        internal readonly bool IsEmpty => _source == 0 && Node.IsEmpty;
    }
}
