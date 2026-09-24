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
    /// <summary>A group-local node a fold composed, or a leaf.</summary>
    /// <remarks>
    /// Only the root leaf of a single-leaf tree is stored as a node; every other leaf is inlined in its parent branch,
    /// so a leaf is only ever its key and hash. A branch carries the keys of its leaf children, flagged by
    /// <see cref="LeafChildren"/> because fixed-length key types have no empty value. A branch reaching past its group
    /// also carries the compressed prefix below it, so that it stays readable against the cursor that placed it rather
    /// than against a group path of its own. An untouched stored branch is a <see cref="DirectCopySubtree"/> instead,
    /// until a cursor that does not address it at its own anchor rebuilds it from its stored children.
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

        /// <param name="knownHash">The hash of this branch's encoding with a <paramref name="knownHashBitCount"/>-bit prefix.</param>
        internal Subtree(NodeGroupPath path, in ValueHash256 left, in ValueHash256 right, TKey leftLeafKey, TKey rightLeafKey, byte leafChildren,
            ReadOnlyMemory<byte> prefix, in ValueHash256 knownHash, int knownHashBitCount)
            : this(path, left, right, leftLeafKey, rightLeafKey, leafChildren, prefix)
        {
            KnownHash = knownHash;
            KnownHashBitCount = (ushort)knownHashBitCount;
        }

        internal Subtree(NodeGroupPath path, in ValueHash256 left, in ValueHash256 right, in ValueHash256 knownHash)
            : this(path, left, right, default, default, 0) => KnownHash = knownHash;

        internal readonly NodeGroupPath Path { get; }
        internal readonly bool IsEmpty => Kind == NodeKind.Empty;
        internal readonly bool IsLeaf => Kind == NodeKind.Leaf;
        internal readonly ValueHash256 LeafHash => HashOrLeft;
        internal readonly CompressedPrefix Prefix => Encoding.IsEmpty ? default : CompressedPrefix.FromValidated(Encoding.Span);
        internal readonly ValueHash256 LeftHash => HashOrLeft;
        internal readonly ValueHash256 RightHash => _right;
        /// <summary>Which children are leaves: <see cref="LeftLeaf"/> and <see cref="RightLeaf"/> bits.</summary>
        internal readonly byte LeafChildrenMask => LeafChildren;
        internal readonly bool HasLeftLeaf => (LeafChildren & LeftLeaf) != 0;
        internal readonly bool HasRightLeaf => (LeafChildren & RightLeaf) != 0;
        /// <summary>The left child's complete key; only meaningful when <see cref="HasLeftLeaf"/>.</summary>
        internal readonly TKey LeftLeafKey => LeafKey;
        /// <summary>The right child's complete key; only meaningful when <see cref="HasRightLeaf"/>.</summary>
        internal readonly TKey RightLeafKey => _rightLeafKey;
        internal readonly int LeftLeafKeyLength => HasLeftLeaf ? LeafKey.Length : 0;
        internal readonly int RightLeafKeyLength => HasRightLeaf ? _rightLeafKey.Length : 0;

        /// <summary>Writes the inline leaf keys that follow a branch's preimage.</summary>
        internal readonly void WriteLeafKeys(Span<byte> trailer)
        {
            // Each key is copied in its own statement: the spans borrow defensive copies of the readonly fields, and two
            // such same-typed temporaries in one call would share a slot.
            int leftLength = LeftLeafKeyLength;
            PbtNodeCodec.WriteBranchTrailer(trailer, leftLength, RightLeafKeyLength);
            if (HasLeftLeaf) LeafKey.Bytes.CopyTo(trailer[PbtNodeCodec.BranchTrailerHeaderLength..]);
            if (HasRightLeaf) _rightLeafKey.Bytes.CopyTo(trailer[(PbtNodeCodec.BranchTrailerHeaderLength + leftLength)..]);
        }

        /// <summary>Copies this branch's children into a fold result anchored at <paramref name="path"/>, over <paramref name="prefix"/>.</summary>
        internal readonly FoldResult CopyBranch(NodeGroupPath path, ReadOnlyMemory<byte> prefix) =>
            new(path, LeftHash, RightHash, HasLeftLeaf ? LeftLeafKey : default, HasRightLeaf ? RightLeafKey : default, LeafChildrenMask, prefix);

    }

    /// <summary>A node paired with its borrowed source-group cursor, distinct from its eventual placement.</summary>
    /// <remarks>
    /// The source prefix and encoding must remain valid until the view is consumed or materialized. The node is either
    /// one a fold composed or a <see cref="DirectCopySubtree"/> still borrowed from the frame it was read from; only
    /// the cursor that addresses that one at its own anchor keeps it a copy, so every other placement rebuilds it from
    /// its stored children.
    /// </remarks>
    internal ref struct TraversalSubtree
    {
        internal Subtree Node;
        /// <summary>The untouched stored node this view carries instead of <see cref="Node"/>, if any.</summary>
        internal DirectCopySubtree Copy;
        internal readonly PbtTraversalPath GroupPath;

        internal TraversalSubtree(PbtTraversalPath groupPath, Subtree node)
        {
            GroupPath = groupPath;
            Node = node;
        }

        internal TraversalSubtree(PbtTraversalPath groupPath, DirectCopySubtree copy)
        {
            GroupPath = groupPath;
            Copy = copy;
        }

        internal readonly bool IsEmpty => Node.IsEmpty && Copy.IsEmpty;
        internal readonly bool IsLeaf => Node.IsLeaf;
        internal readonly int AnchorDepth => GroupPath.BitDepth + LocalPath.Length;
        internal readonly int BranchDepth => AnchorDepth + LocalPrefix.BitCount;

        private readonly NodeGroupPath LocalPath => Copy.IsEmpty ? Node.Path : Copy.Path;
        private readonly CompressedPrefix LocalPrefix => Copy.IsEmpty ? Node.Prefix : Copy.Prefix;

        /// <summary>Clears this view once its node has been placed.</summary>
        internal void Clear()
        {
            Node = default;
            Copy = default;
        }

        internal readonly int PrefixBit(int bit)
        {
            if (bit < GroupPath.BitDepth) return GetBit(GroupPath.Bytes, bit);
            if (bit < AnchorDepth) return LocalPath.GetBit(bit - GroupPath.BitDepth);
            return GetBit(LocalPrefix.Bytes, bit - AnchorDepth);
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
                return AnchorDepth + MatchingPrefixBits(LocalPrefix, key, AnchorDepth);
            return end;
        }

        /// <summary>The stored length at <paramref name="depth"/>; a leaf is only stored as the tree root.</summary>
        internal readonly int EncodedLength(int depth)
        {
            if (IsLeaf) return PbtNodeCodec.LeafLength(Node.LeafKey.Length);
            if (Copy.IsEmpty) return PbtNodeCodec.BranchLength(BranchDepth - depth, Node.LeftLeafKeyLength, Node.RightLeafKeyLength);
            if (depth == AnchorDepth) return Copy.Length;
            PbtNodeReader stored = Copy.Reader;
            return PbtNodeCodec.BranchLength(BranchDepth - depth, stored.LeftKey.Length, stored.RightKey.Length);
        }

        // Encode writes every byte of the encoding it is given.
        [SkipLocalsInit]
        internal readonly ValueHash256 Hash(int depth, TrieUpdaterMetrics? metrics)
        {
            if (IsEmpty) return default;
            if (IsLeaf) return Node.LeafHash;
            if (!Copy.IsEmpty && depth == AnchorDepth) return Copy.Hash(metrics);
            Span<byte> encoding = stackalloc byte[EncodedLength(depth)];
            return Encode(encoding, depth, metrics);
        }

        internal readonly ValueHash256 Encode(Span<byte> encoding, int depth, TrieUpdaterMetrics? metrics)
        {
            ValueHash256 hash = EncodeDeferringHash(encoding, depth, out int preimageLength);
            if (preimageLength == 0) return hash;
            metrics?.IncrementNodeHashes();
            return Blake3Hash.Hash(encoding[..preimageLength]);
        }

        /// <summary>
        /// <see cref="Encode"/>, except that a node whose hash is not yet known is left unhashed: the result is
        /// default and <paramref name="preimageLength"/> the length of its preimage at the start of
        /// <paramref name="encoding"/>, so the caller can hash it together with another node.
        /// </summary>
        internal readonly ValueHash256 EncodeDeferringHash(Span<byte> encoding, int depth, out int preimageLength)
        {
            preimageLength = 0;
            if (IsLeaf)
            {
                PbtNodeCodec.EncodeLeaf(encoding, Node.LeafKey);
                return Node.LeafHash;
            }
            if (!Copy.IsEmpty && depth == AnchorDepth)
            {
                Copy.CopyTo(encoding);
                if (Copy.KnownHash == default) preimageLength = Copy.Reader.Preimage.Length;
                return Copy.KnownHash;
            }

            int bitCount = BranchDepth - depth;
            return Copy.IsEmpty
                ? EncodeBranch(encoding, depth, bitCount, out preimageLength)
                : EncodeReanchoredCopy(encoding, depth, bitCount, out preimageLength);
        }

        /// <summary>An untouched stored branch addressed away from its own anchor, encoded from its stored children.</summary>
        /// <remarks>
        /// The copy's known hash is of its encoding at its own anchor, so the longer prefix written here is always
        /// hashed. Kept out of line, as only a promotion or a shallower cursor takes this path.
        /// </remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private readonly ValueHash256 EncodeReanchoredCopy(Span<byte> encoding, int depth, int bitCount, out int preimageLength)
        {
            PbtNodeReader stored = Copy.Reader;
            preimageLength = WriteBranchPreimage(encoding, depth, bitCount, stored.LeftHash, stored.RightHash);
            PbtNodeCodec.WriteBranchTrailer(encoding[preimageLength..], stored.LeftKey, stored.RightKey);
            return default;
        }

        private readonly ValueHash256 EncodeBranch(Span<byte> encoding, int depth, int bitCount, out int preimageLength)
        {
            preimageLength = 0;
            int branchPreimageLength = WriteBranchPreimage(encoding, depth, bitCount, Node.LeftHash, Node.RightHash);
            Node.WriteLeafKeys(encoding[branchPreimageLength..]);
            // An omitted branch rebuilt at its own anchor, or a published group root written into its parent
            // group, is the node already hashed at this prefix length.
            if (Node.KnownHash != default && bitCount == Node.KnownHashBitCount) return Node.KnownHash;
            preimageLength = branchPreimageLength;
            return default;
        }

        /// <summary>Writes the preimage of a branch over <paramref name="left"/> and <paramref name="right"/> addressed at <paramref name="depth"/>, returning its length.</summary>
        private readonly int WriteBranchPreimage(Span<byte> encoding, int depth, int bitCount, in ValueHash256 left, in ValueHash256 right)
        {
            // Promotion absorbs the source anchor's skipped bits into the relative compressed prefix.
            PbtNodeCodec.CreateBranchEncoding(encoding, bitCount, left, right);
            CopyBranchBits(depth, bitCount, encoding.Slice(3, PbtBitPrefix.ByteCount(bitCount)));
            return PbtNodeCodec.BranchPreimageLength(bitCount);
        }

        private readonly void CopyBranchBits(int start, int count, Span<byte> destination)
        {
            int end = start + count;
            int groupEnd = Math.Min(end, GroupPath.BitDepth);
            if (start < groupEnd)
                PbtBitPrefix.CopyBits(GroupPath.Bytes, start, groupEnd - start, destination, 0);
            for (int bit = Math.Max(start, GroupPath.BitDepth); bit < Math.Min(end, AnchorDepth); bit++)
                destination[(bit - start) >> 3] |= (byte)(LocalPath.GetBit(bit - GroupPath.BitDepth) << (7 - ((bit - start) & 7)));
            int prefixStart = Math.Max(start, AnchorDepth);
            if (prefixStart < end)
                PbtBitPrefix.CopyBits(LocalPrefix.Bytes, prefixStart - AnchorDepth, end - prefixStart, destination, prefixStart - start);
        }

        /// <summary>Detaches this view for a caller that addresses it at <paramref name="anchorDepth"/>.</summary>
        /// <remarks>
        /// The result is read against that caller's cursor: its group path is the cursor itself, the four levels below
        /// are its <see cref="NodeGroupPath"/>, and anything deeper becomes an owned compressed prefix. Detaching copies
        /// the node out of its source group, so the result outlives the frame it was read from.
        /// </remarks>
        internal readonly FoldResult Materialize(int anchorDepth)
        {
            Debug.Assert(anchorDepth % PbtFourLevelGroupGeometry.LevelsPerGroup == 0, "A result is anchored at a group depth.");
            if (IsEmpty) return default;
            if (IsLeaf) return new(Node.LeafKey, Node.LeafHash);

            int splitDepth = BranchDepth;
            Debug.Assert(splitDepth >= anchorDepth, "A result branches at or below the cursor that addresses it.");
            int localLength = Math.Min(splitDepth - anchorDepth, PbtFourLevelGroupGeometry.LevelsPerGroup);
            int slot = 0;
            for (int bit = anchorDepth; bit < anchorDepth + localLength; bit++) slot = (slot << 1) | PrefixBit(bit);
            NodeGroupPath path = new(slot << (PbtFourLevelGroupGeometry.LevelsPerGroup - localLength), localLength);
            ReadOnlyMemory<byte> prefix = OwnedPrefix(anchorDepth + localLength, splitDepth);
            if (!Copy.IsEmpty) return ReanchoredCopy(path, prefix);
            return Node.CopyBranch(path, prefix);
        }

        /// <summary>The untouched stored branch as a fold result at <paramref name="path"/>, read from its stored children.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private readonly FoldResult ReanchoredCopy(NodeGroupPath path, ReadOnlyMemory<byte> prefix)
        {
            PbtNodeReader stored = Copy.Reader;
            return new(path, stored.LeftHash, stored.RightHash,
                stored.LeftKey.IsEmpty ? default : TKey.Create(stored.LeftKey),
                stored.RightKey.IsEmpty ? default : TKey.Create(stored.RightKey),
                (byte)((stored.LeftKey.IsEmpty ? 0 : Subtree.LeftLeaf) | (stored.RightKey.IsEmpty ? 0 : Subtree.RightLeaf)), prefix);
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
    /// it was read from, but it is only meaningful paired with a cursor at its anchor depth. A leaf is only its key and
    /// hash; a branch carries the keys of its leaf children, flagged by <see cref="LeafChildren"/>.
    /// </remarks>
    internal struct FoldResult
    {
        internal readonly NodeKind Kind;
        /// <summary>A branch's owned compressed prefix below its anchor, empty when it branches at its anchor.</summary>
        internal readonly ReadOnlyMemory<byte> Encoding;
        /// <summary>A leaf's key, or a branch's left leaf key.</summary>
        internal readonly TKey LeafKey;
        internal readonly TKey RightLeafKey;
        /// <summary>A leaf's hash, or a branch's left child hash.</summary>
        internal readonly ValueHash256 LeftHash;
        internal readonly ValueHash256 RightHash;
        /// <summary>The hash already computed for this branch, or default when it must be computed.</summary>
        internal readonly ValueHash256 KnownHash;
        /// <summary>The compressed-prefix bit count of the encoding <see cref="KnownHash"/> is the hash of.</summary>
        internal readonly ushort KnownHashBitCount;
        /// <summary>Which children of a branch are leaves: <see cref="Subtree.LeftLeaf"/> and <see cref="Subtree.RightLeaf"/> bits.</summary>
        internal readonly byte LeafChildren;
        internal readonly NodeGroupPath Path;
        /// <summary>The change in stored size across the groups this result was folded from, still owed to the caller's boundary slot.</summary>
        internal long SizeDelta;

        private FoldResult(in FoldResult source, in ValueHash256 knownHash, int knownHashBitCount)
        {
            this = source;
            KnownHash = knownHash;
            KnownHashBitCount = (ushort)knownHashBitCount;
        }

        internal FoldResult(TKey key, in ValueHash256 hash)
        {
            Kind = NodeKind.Leaf;
            LeafKey = key;
            LeftHash = hash;
        }

        /// <param name="prefix">The owned compressed prefix below this branch's anchor, empty when it branches at its anchor.</param>
        internal FoldResult(NodeGroupPath path, in ValueHash256 left, in ValueHash256 right, TKey leftLeafKey, TKey rightLeafKey, byte leafChildren,
            ReadOnlyMemory<byte> prefix)
        {
            Kind = NodeKind.Branch;
            Encoding = prefix;
            LeftHash = left;
            RightHash = right;
            Path = path;
            LeafKey = leftLeafKey;
            RightLeafKey = rightLeafKey;
            LeafChildren = leafChildren;
        }

        /// <param name="knownHash">The hash of this branch's encoding with a <paramref name="knownHashBitCount"/>-bit prefix.</param>
        internal FoldResult(NodeGroupPath path, in ValueHash256 left, in ValueHash256 right, TKey leftLeafKey, TKey rightLeafKey, byte leafChildren,
            ReadOnlyMemory<byte> prefix, in ValueHash256 knownHash, int knownHashBitCount)
            : this(path, left, right, leftLeafKey, rightLeafKey, leafChildren, prefix)
        {
            KnownHash = knownHash;
            KnownHashBitCount = (ushort)knownHashBitCount;
        }

        /// <summary>This result carrying the hash of its encoding with a <paramref name="bitCount"/>-bit prefix, so that encoding is not hashed again.</summary>
        internal readonly FoldResult WithKnownHash(in ValueHash256 hash, int bitCount) => new(this, hash, bitCount);

        internal readonly bool IsEmpty => Kind == NodeKind.Empty;
        internal readonly bool IsLeaf => Kind == NodeKind.Leaf;
        internal readonly ValueHash256 LeafHash => LeftHash;
        internal readonly bool HasLeftLeaf => (LeafChildren & Subtree.LeftLeaf) != 0;
        internal readonly bool HasRightLeaf => (LeafChildren & Subtree.RightLeaf) != 0;

        internal readonly TraversalSubtree Borrow(PbtTraversalPath cursor) => new(cursor, Kind switch
        {
            NodeKind.Leaf => new Subtree(LeafKey, LeftHash),
            NodeKind.Branch => new Subtree(Path, LeftHash, RightHash, LeafKey, RightLeafKey, LeafChildren, Encoding, KnownHash, KnownHashBitCount),
            _ => default,
        });

        internal static FoldResult Move(ref FoldResult source)
        {
            FoldResult result = source;
            source = default;
            return result;
        }

        internal static FoldResult TakeFrom<TSourceKey, TSourcePath>(ref TrieUpdater<TSourceKey, TSourcePath>.FoldResult source)
            where TSourceKey : struct, IPbtKey<TSourceKey>
            where TSourcePath : struct, IPbtNodePath<TSourcePath>
        {
            FoldResult result = default;
            if (!source.IsEmpty)
            {
                if (source.IsLeaf)
                    result = new(TKey.Create(source.LeafKey.Bytes), source.LeafHash);
                else
                {
                    Debug.Assert(source.Kind == NodeKind.Branch);
                    result = new(source.Path, source.LeftHash, source.RightHash,
                        source.HasLeftLeaf ? TKey.Create(source.LeafKey.Bytes) : default,
                        source.HasRightLeaf ? TKey.Create(source.RightLeafKey.Bytes) : default,
                        source.LeafChildren, source.Encoding);
                }
            }
            result.SizeDelta = source.SizeDelta;
            source = default;
            return result;
        }
    }

    /// <summary>Where the frame reads a slot's node from, or that the slot holds a fold's result.</summary>
    /// <remarks>
    /// Decomposition never carries a node here: it records where the node is, so an untouched subtree is left in its
    /// frame until composition claims it, and a boundary node is sliced out only by the fold that consumes it.
    /// </remarks>
    internal readonly struct DecompositionEntry
    {
        private readonly byte _source;

        internal DecompositionEntry(EntrySource source, int sourcePosition)
        {
            Debug.Assert(source != EntrySource.Node, "A decomposed entry names where its node is read from.");
            _source = (byte)((sourcePosition << 2) | (int)source);
        }

        internal EntrySource Source => (EntrySource)(_source & 3);
        internal int SourcePosition => _source >> 2;
        internal bool IsEmpty => _source == 0;
    }
}
