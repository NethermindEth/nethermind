// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using static Nethermind.Pbt.TrieUpdater;

namespace Nethermind.Pbt;

internal static partial class TrieUpdater<TKey, TPath>
    where TKey : unmanaged, IPbtKey<TKey>
    where TPath : struct, IPbtNodePath<TPath>
{
    /// <summary>A group-local node a fold composed, or a leaf.</summary>
    /// <remarks>
    /// Only the root leaf of a single-leaf tree is stored as a node; every other leaf is inlined in its parent branch,
    /// so a leaf is only ever its key and hash. A branch carries the keys of its leaf children, flagged by
    /// <see cref="LeafChildren"/> because fixed-length key types have no empty value. A branch reaching past its group
    /// also carries the compressed prefix below it, so that it stays readable against the cursor that placed it rather
    /// than against a group path of its own.
    /// </remarks>
    internal ref struct Subtree
    {
        internal const byte LeftLeaf = 1;
        internal const byte RightLeaf = 2;

        /// <summary>A leaf's hash, or a branch's left child hash.</summary>
        internal readonly ValueHash256 HashOrLeft;
        private readonly ValueHash256 _right;
        /// <summary>The hash already computed for this node, or default when it must be computed.</summary>
        internal readonly ValueHash256 KnownHash;
        internal readonly NodeKind Kind;
        internal readonly ReadOnlySpan<byte> Encoding;
        /// <summary>A leaf's key, or a composed branch's left leaf key.</summary>
        internal readonly TKey LeafKey;
        private readonly TKey _rightLeafKey;
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

        /// <param name="prefix">The owned compressed prefix below this branch's anchor, empty when it branches at its anchor.</param>
        internal Subtree(NodeGroupPath path, in ValueHash256 left, in ValueHash256 right, TKey leftLeafKey, TKey rightLeafKey, byte leafChildren,
            ReadOnlySpan<byte> prefix)
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
            ReadOnlySpan<byte> prefix, in ValueHash256 knownHash, int knownHashBitCount)
            : this(path, left, right, leftLeafKey, rightLeafKey, leafChildren, prefix)
        {
            KnownHash = knownHash;
            KnownHashBitCount = (ushort)knownHashBitCount;
        }

        internal readonly NodeGroupPath Path { get; }
        internal readonly bool IsEmpty => Kind == NodeKind.Empty;
        internal readonly bool IsLeaf => Kind == NodeKind.Leaf;
        internal readonly ValueHash256 LeafHash => HashOrLeft;
        internal readonly CompressedPrefix Prefix => Encoding.IsEmpty ? default : CompressedPrefix.FromValidated(Encoding);
        internal readonly ValueHash256 LeftHash => HashOrLeft;
        internal readonly ValueHash256 RightHash => _right;
        internal readonly bool HasLeftLeaf => (LeafChildren & LeftLeaf) != 0;
        internal readonly bool HasRightLeaf => (LeafChildren & RightLeaf) != 0;
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
    }

    /// <summary>A node paired with its borrowed source-group cursor, distinct from its eventual placement.</summary>
    /// <remarks>The source prefix and encoding must remain valid until the view is consumed.</remarks>
    internal ref struct TraversalSubtree
    {
        internal Subtree Node;
        internal readonly PbtTraversalPath GroupPath;

        internal TraversalSubtree(PbtTraversalPath groupPath, Subtree node)
        {
            GroupPath = groupPath;
            Node = node;
        }

        internal readonly bool IsEmpty => Node.IsEmpty;
        internal readonly bool IsLeaf => Node.IsLeaf;
        internal readonly int AnchorDepth => GroupPath.BitDepth + LocalPath.Length;
        internal readonly int BranchDepth => AnchorDepth + LocalPrefix.BitCount;

        private readonly NodeGroupPath LocalPath => Node.Path;
        private readonly CompressedPrefix LocalPrefix => Node.Prefix;

        /// <summary>Clears this view once its node has been placed.</summary>
        internal void Clear() => Node = default;

        /// <summary>The stored length at <paramref name="depth"/>; a leaf is only stored as the tree root.</summary>
        internal readonly int EncodedLength(int depth) => IsLeaf
            ? PbtNodeCodec.LeafLength(Node.LeafKey.Length)
            : PbtNodeCodec.BranchLength(BranchDepth - depth, Node.LeftLeafKeyLength, Node.RightLeafKeyLength);

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

            return EncodeBranch(encoding, depth, BranchDepth - depth, out preimageLength);
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
            CopyBranchBits(GroupPath, LocalPath, LocalPrefix, depth, bitCount, encoding.Slice(3, PbtBitPrefix.ByteCount(bitCount)));
            return PbtNodeCodec.BranchPreimageLength(bitCount);
        }

        internal static void CopyBranchBits(scoped in PbtTraversalPath groupPath, NodeGroupPath localPath, CompressedPrefix localPrefix,
            int start, int count, Span<byte> destination)
        {
            int anchorDepth = groupPath.BitDepth + localPath.Length;
            int end = start + count;
            int groupEnd = Math.Min(end, groupPath.BitDepth);
            if (start < groupEnd)
                PbtBitPrefix.CopyBits(groupPath.Bytes, start, groupEnd - start, destination, 0);
            for (int bit = Math.Max(start, groupPath.BitDepth); bit < Math.Min(end, anchorDepth); bit++)
                destination[(bit - start) >> 3] |= (byte)(localPath.GetBit(bit - groupPath.BitDepth) << (7 - ((bit - start) & 7)));
            int prefixStart = Math.Max(start, anchorDepth);
            if (prefixStart < end)
                PbtBitPrefix.CopyBits(localPrefix.Bytes, prefixStart - anchorDepth, end - prefixStart, destination, prefixStart - start);
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
        /// <summary>The longest owned compressed prefix: its bit count, then bits that never outrun the longest key.</summary>
        internal const int MaxPrefixLength = sizeof(ushort) + PbtStorageTreeKey.MaxLength;

        internal readonly NodeKind Kind;
        private PrefixBuffer _prefix;
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
            ReadOnlySpan<byte> prefix)
        {
            Kind = NodeKind.Branch;
            prefix.CopyTo(_prefix);
            LeftHash = left;
            RightHash = right;
            Path = path;
            LeafKey = leftLeafKey;
            RightLeafKey = rightLeafKey;
            LeafChildren = leafChildren;
        }

        /// <param name="knownHash">The hash of this branch's encoding with a <paramref name="knownHashBitCount"/>-bit prefix.</param>
        internal FoldResult(NodeGroupPath path, in ValueHash256 left, in ValueHash256 right, TKey leftLeafKey, TKey rightLeafKey, byte leafChildren,
            ReadOnlySpan<byte> prefix, in ValueHash256 knownHash, int knownHashBitCount)
            : this(path, left, right, leftLeafKey, rightLeafKey, leafChildren, prefix)
        {
            KnownHash = knownHash;
            KnownHashBitCount = (ushort)knownHashBitCount;
        }

        /// <summary>A branch's owned compressed prefix below its anchor, empty when it branches at its anchor.</summary>
        [UnscopedRef]
        internal readonly ReadOnlySpan<byte> Encoding
        {
            get
            {
                ReadOnlySpan<byte> prefix = _prefix;
                int bitCount = BinaryPrimitives.ReadUInt16BigEndian(prefix);
                return bitCount == 0 ? default : prefix[..(sizeof(ushort) + PbtBitPrefix.ByteCount(bitCount))];
            }
        }

        /// <summary>This result carrying the hash of its encoding with a <paramref name="bitCount"/>-bit prefix, so that encoding is not hashed again.</summary>
        internal readonly FoldResult WithKnownHash(in ValueHash256 hash, int bitCount) => new(this, hash, bitCount);

        internal readonly bool IsEmpty => Kind == NodeKind.Empty;
        internal readonly bool IsLeaf => Kind == NodeKind.Leaf;
        internal readonly ValueHash256 LeafHash => LeftHash;
        internal readonly bool HasLeftLeaf => (LeafChildren & Subtree.LeftLeaf) != 0;
        internal readonly bool HasRightLeaf => (LeafChildren & Subtree.RightLeaf) != 0;
        [UnscopedRef]
        private readonly CompressedPrefix Prefix => Encoding.IsEmpty ? default : CompressedPrefix.FromValidated(Encoding);

        /// <summary>The depth this branch splits at when read against <paramref name="cursor"/>.</summary>
        internal readonly int BranchDepth(scoped in PbtTraversalPath cursor) => cursor.BitDepth + Path.Length + Prefix.BitCount;

        /// <summary>The hash of this node written at <paramref name="depth"/> when read against <paramref name="cursor"/>.</summary>
        [SkipLocalsInit]
        internal readonly ValueHash256 Hash(scoped in PbtTraversalPath cursor, int depth, TrieUpdaterMetrics? metrics)
        {
            if (IsEmpty) return default;
            if (IsLeaf) return LeafHash;
            int bitCount = BranchDepth(cursor) - depth;
            if (KnownHash != default && bitCount == KnownHashBitCount) return KnownHash;
            Span<byte> preimage = stackalloc byte[PbtNodeCodec.BranchPreimageLength(bitCount)];
            PbtNodeCodec.CreateBranchEncoding(preimage, bitCount, LeftHash, RightHash);
            TraversalSubtree.CopyBranchBits(cursor, Path, Prefix, depth, bitCount, preimage.Slice(3, PbtBitPrefix.ByteCount(bitCount)));
            metrics?.IncrementNodeHashes();
            return Blake3Hash.Hash(preimage);
        }

        [UnscopedRef]
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
            where TSourceKey : unmanaged, IPbtKey<TSourceKey>
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

    [InlineArray(FoldResult.MaxPrefixLength)]
    internal struct PrefixBuffer { private byte _element; }

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
