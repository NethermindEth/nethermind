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
        internal ValueHash256 KnownHash;
        /// <summary>The compressed-prefix bit count of the encoding <see cref="KnownHash"/> is the hash of.</summary>
        internal ushort KnownHashBitCount;
        /// <summary>Which children of a branch are leaves: <see cref="LeftLeaf"/> and <see cref="RightLeaf"/> bits.</summary>
        internal readonly byte LeafChildren;
        internal readonly NodeGroupPath Path;
        /// <summary>The change in stored size across the groups this result was folded from, still owed to the caller's boundary slot.</summary>
        internal long SizeDelta;

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

        internal readonly bool IsEmpty => Kind == NodeKind.Empty;
        internal readonly bool IsLeaf => Kind == NodeKind.Leaf;
        internal readonly ValueHash256 LeafHash => LeftHash;
        internal readonly bool HasLeftLeaf => (LeafChildren & LeftLeaf) != 0;
        internal readonly bool HasRightLeaf => (LeafChildren & RightLeaf) != 0;
        private readonly int LeftLeafKeyLength => HasLeftLeaf ? LeafKey.Length : 0;
        private readonly int RightLeafKeyLength => HasRightLeaf ? RightLeafKey.Length : 0;
        [UnscopedRef]
        private readonly CompressedPrefix Prefix => Encoding.IsEmpty ? default : CompressedPrefix.FromValidated(Encoding);

        /// <summary>The depth this branch splits at when read against <paramref name="cursor"/>.</summary>
        internal readonly int BranchDepth(scoped in PbtTraversalPath cursor) => cursor.BitDepth + Path.Length + Prefix.BitCount;

        /// <summary>The hash of this node written at <paramref name="depth"/> when read against <paramref name="cursor"/>.</summary>
        [SkipLocalsInit]
        internal readonly ValueHash256 Hash(scoped in PbtTraversalPath cursor, int depth)
        {
            if (IsEmpty) return default;
            if (IsLeaf) return LeafHash;
            int bitCount = BranchDepth(cursor) - depth;
            if (KnownHash != default && bitCount == KnownHashBitCount) return KnownHash;
            Span<byte> preimage = stackalloc byte[PbtNodeCodec.BranchPreimageLength(bitCount)];
            WriteBranchPreimage(cursor, depth, bitCount, preimage);
            return Blake3Hash.Hash(preimage);
        }

        /// <summary>The stored length of this node written at <paramref name="depth"/> when read against <paramref name="cursor"/>; a leaf is only stored as the tree root.</summary>
        internal readonly int EncodedLength(scoped in PbtTraversalPath cursor, int depth) => IsLeaf
            ? PbtNodeCodec.LeafLength(LeafKey.Length)
            : PbtNodeCodec.BranchLength(BranchDepth(cursor) - depth, LeftLeafKeyLength, RightLeafKeyLength);

        /// <summary>Writes this node at <paramref name="depth"/> when read against <paramref name="cursor"/>, returning its hash.</summary>
        internal readonly ValueHash256 EncodeAt(scoped in PbtTraversalPath cursor, int depth, Span<byte> encoding)
        {
            if (IsLeaf)
            {
                PbtNodeCodec.EncodeLeaf(encoding, LeafKey);
                return LeafHash;
            }

            int bitCount = BranchDepth(cursor) - depth;
            int preimageLength = WriteBranchPreimage(cursor, depth, bitCount, encoding);
            Span<byte> trailer = encoding[preimageLength..];
            int leftLength = LeftLeafKeyLength;
            PbtNodeCodec.WriteBranchTrailer(trailer, leftLength, RightLeafKeyLength);
            // Each key is copied in its own statement: the spans borrow defensive copies of the readonly fields, and two
            // such same-typed temporaries in one call would share a slot.
            if (HasLeftLeaf) LeafKey.Bytes.CopyTo(trailer[PbtNodeCodec.BranchTrailerHeaderLength..]);
            if (HasRightLeaf) RightLeafKey.Bytes.CopyTo(trailer[(PbtNodeCodec.BranchTrailerHeaderLength + leftLength)..]);
            // An omitted branch rebuilt at its own anchor, or a published group root written into its parent
            // group, is the node already hashed at this prefix length.
            if (KnownHash != default && bitCount == KnownHashBitCount) return KnownHash;
            return Blake3Hash.Hash(encoding[..preimageLength]);
        }

        /// <summary>Writes the preimage of this branch addressed at <paramref name="depth"/>, returning its length.</summary>
        private readonly int WriteBranchPreimage(scoped in PbtTraversalPath cursor, int depth, int bitCount, Span<byte> encoding)
        {
            // Promotion absorbs the source anchor's skipped bits into the relative compressed prefix.
            PbtNodeCodec.CreateBranchEncoding(encoding, bitCount, LeftHash, RightHash);
            CopyBranchBits(cursor, Path, Prefix, depth, bitCount, encoding.Slice(3, PbtBitPrefix.ByteCount(bitCount)));
            return PbtNodeCodec.BranchPreimageLength(bitCount);

            static void CopyBranchBits(scoped in PbtTraversalPath groupPath, NodeGroupPath localPath, CompressedPrefix localPrefix,
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

        internal static void Move(ref FoldResult source, ref FoldResult destination)
        {
            destination = source;
            source = default;
        }

        internal static void TakeFrom<TSourceKey, TSourcePath>(ref TrieUpdater<TSourceKey, TSourcePath>.FoldResult source, ref FoldResult result)
            where TSourceKey : unmanaged, IPbtKey<TSourceKey>
            where TSourcePath : struct, IPbtNodePath<TSourcePath>
        {
            result = default;
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
    }
}
