// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using Nethermind.Core.Crypto;
using static Nethermind.Pbt.TrieUpdater;

namespace Nethermind.Pbt;

internal static partial class TrieUpdater<TKey, TPath>
    where TKey : struct, IPbtKey<TKey>
    where TPath : struct, IPbtNodePath<TPath>
{
    /// <summary>The node at one boundary slot of a group, read against the traversal cursor that reached it.</summary>
    /// <remarks>
    /// A boundary node keeps its encoding exactly as the group stores it, together with the absolute depth that
    /// encoding is anchored at, so addressing it from another depth is a comparison rather than a rewrite.
    /// <see cref="Hash"/> is always the hash of that encoding, because decomposition reads it from the parent's child
    /// link; a boundary node is therefore never hashed again to be opened, moved or handed to another thread.
    /// It is a leaf, a branch stored at the boundary, or a branch anchored above the boundary whose compressed prefix
    /// spans past it. An omitted prefixless branch is never one, since omission only applies to a group's interior
    /// (<see cref="PbtNodeGroupCodec.ShouldOmit"/>) and a spanning branch always carries a prefix.
    /// </remarks>
    internal readonly struct BoundaryNode
    {
        private readonly ReadOnlyMemory<byte> _encoding;
        private readonly ValueHash256 _hash;
        private readonly TKey _leafKey;
        private readonly ushort _anchorDepth;
        private readonly bool _isLeaf;

        /// <summary>Creates the leaf a parent branch inlines, or the leaf a single-leaf tree stores as its root.</summary>
        internal BoundaryNode(TKey key, in ValueHash256 hash)
        {
            _leafKey = key;
            _hash = hash;
            _isLeaf = true;
        }

        /// <param name="anchorDepth">The absolute bit depth <paramref name="encoding"/>'s compressed prefix starts at.</param>
        /// <param name="hash">The hash of <paramref name="encoding"/>, taken from the link that referenced it.</param>
        internal BoundaryNode(ReadOnlyMemory<byte> encoding, int anchorDepth, in ValueHash256 hash)
        {
            Debug.Assert(!encoding.IsEmpty && !PbtNodeReader.FromValidated(encoding.Span).IsLeaf, "A boundary branch carries a stored branch encoding.");
            Debug.Assert(hash != default, "A boundary node knows its hash.");
            _encoding = encoding;
            _anchorDepth = (ushort)anchorDepth;
            _hash = hash;
        }

        internal readonly bool IsEmpty => !_isLeaf && _encoding.IsEmpty;
        /// <summary>The branch encoding exactly as its group stores it.</summary>
        internal readonly ReadOnlyMemory<byte> Encoding => _encoding;
        internal readonly bool IsLeaf => _isLeaf;
        /// <summary>A leaf's complete key.</summary>
        internal readonly TKey LeafKey => _leafKey;
        /// <summary>The hash of this node as it is stored, which its parent's link already held.</summary>
        internal readonly ValueHash256 Hash => _hash;
        /// <summary>The absolute depth this node's encoding is anchored at, never deeper than its boundary slot.</summary>
        internal readonly int AnchorDepth => _anchorDepth;
        internal readonly PbtNodeReader Reader => PbtNodeReader.FromValidated(_encoding.Span);
        internal readonly CompressedPrefix Prefix => Reader.Prefix;
        /// <summary>The absolute depth this branch splits at, past its compressed prefix.</summary>
        internal readonly int BranchDepth => _anchorDepth + Prefix.BitCount;
        internal readonly ValueHash256 LeftHash => Reader.LeftHash;
        internal readonly ValueHash256 RightHash => Reader.RightHash;
        internal readonly bool HasLeftLeaf => !Reader.LeftKey.IsEmpty;
        internal readonly bool HasRightLeaf => !Reader.RightKey.IsEmpty;
        internal readonly TKey LeftLeafKey => TKey.Create(Reader.LeftKey);
        internal readonly TKey RightLeafKey => TKey.Create(Reader.RightKey);
        /// <summary>Which children are leaves: <see cref="Subtree.LeftLeaf"/> and <see cref="Subtree.RightLeaf"/> bits.</summary>
        internal readonly byte LeafChildrenMask
        {
            get
            {
                PbtNodeReader reader = Reader;
                return (byte)((reader.LeftKey.IsEmpty ? 0 : Subtree.LeftLeaf) | (reader.RightKey.IsEmpty ? 0 : Subtree.RightLeaf));
            }
        }

        /// <summary>The bit at <paramref name="bit"/> of the path through this node, taken from the cursor above its anchor.</summary>
        internal readonly int PrefixBit(scoped in PbtTraversalPath cursor, int bit) => bit < _anchorDepth
            ? GetBit(cursor.Bytes, bit)
            : GetBit(Prefix.Bytes, bit - _anchorDepth);

        /// <summary>The first bit at or after <paramref name="start"/> where <paramref name="key"/> leaves this node's path.</summary>
        internal readonly int FirstDifferingBit(scoped in PbtTraversalPath cursor, TKey key, int start)
        {
            int end = Math.Min(BranchDepth, key.BitLength);
            int anchorEnd = Math.Min(_anchorDepth, end);
            if (start < anchorEnd)
            {
                int difference = PbtKeyOperations.FirstDifferingBit(cursor.Bytes, key.Bytes, start);
                if (difference < anchorEnd) return difference;
                start = anchorEnd;
            }
            return start < end ? _anchorDepth + MatchingPrefixBits(Prefix, key, _anchorDepth) : end;
        }

        /// <summary>The leaf this branch inlines on one side, which stores no node of its own.</summary>
        internal readonly BoundaryNode InlineLeaf(bool right)
        {
            PbtNodeReader node = Reader;
            return right ? new(TKey.Create(node.RightKey), node.RightHash) : new(TKey.Create(node.LeftKey), node.LeftHash);
        }

        /// <summary>This node with its encoding copied out of the frame it was read from, so it outlives that frame.</summary>
        internal readonly BoundaryNode Owned() => _encoding.IsEmpty ? this : new(_encoding.ToArray(), _anchorDepth, _hash);

        internal static BoundaryNode Move(ref BoundaryNode source)
        {
            BoundaryNode result = source;
            source = default;
            return result;
        }

        /// <summary>Takes a node read under a wider key type, which a fold below the zone nibble continues under its own.</summary>
        /// <remarks>The encoding and the hash are the stored ones either way; only the leaf key changes representation.</remarks>
        internal static BoundaryNode TakeFrom<TSourceKey, TSourcePath>(ref TrieUpdater<TSourceKey, TSourcePath>.BoundaryNode source)
            where TSourceKey : struct, IPbtKey<TSourceKey>
            where TSourcePath : struct, IPbtNodePath<TSourcePath>
        {
            BoundaryNode result = source.IsEmpty
                ? default
                : source.IsLeaf
                    ? new BoundaryNode(TKey.Create(source.LeafKey.Bytes), source.Hash)
                    : new BoundaryNode(source.Encoding, source.AnchorDepth, source.Hash);
            source = default;
            return result;
        }

        /// <summary>The hash of this node as the group at <paramref name="depth"/> is keyed by.</summary>
        /// <remarks>
        /// A node addressed at its own anchor keys its group by the hash it already carries. A prefix jump addresses it
        /// deeper, where its shorter encoding hashes differently, and that re-anchored hash is what published the group.
        /// </remarks>
        internal readonly ValueHash256 HashAt(scoped in PbtTraversalPath cursor, int depth, TrieUpdaterMetrics? metrics)
        {
            if (IsEmpty || IsLeaf || depth == _anchorDepth) return _hash;
            OwnedSubtree reAnchored = ToOwnedSubtree(cursor, depth);
            return reAnchored.Borrow(cursor).Hash(depth, metrics);
        }

        /// <summary>This node as a composed result addressed at <paramref name="anchorDepth"/>, detached from its frame.</summary>
        /// <remarks>
        /// The result owns everything below that cursor. It keeps the hash of the stored encoding, which the encoder
        /// reuses only when it writes the node back at its own anchor, where that hash is the one it would compute.
        /// </remarks>
        internal readonly OwnedSubtree ToOwnedSubtree(scoped in PbtTraversalPath cursor, int anchorDepth)
        {
            Debug.Assert(anchorDepth % PbtFourLevelGroupGeometry.LevelsPerGroup == 0, "A result is anchored at a group depth.");
            if (IsEmpty) return default;
            if (IsLeaf) return new OwnedSubtree(new Subtree(_leafKey, _hash));

            int splitDepth = BranchDepth;
            Debug.Assert(splitDepth >= anchorDepth, "A result branches at or below the cursor that addresses it.");
            int localLength = Math.Min(splitDepth - anchorDepth, PbtFourLevelGroupGeometry.LevelsPerGroup);
            int slot = 0;
            for (int bit = anchorDepth; bit < anchorDepth + localLength; bit++) slot = (slot << 1) | PrefixBit(cursor, bit);
            PbtNodeReader reader = Reader;
            Subtree branch = new(new NodeGroupPath(slot << (PbtFourLevelGroupGeometry.LevelsPerGroup - localLength), localLength),
                reader.LeftHash, reader.RightHash,
                reader.LeftKey.IsEmpty ? default : TKey.Create(reader.LeftKey),
                reader.RightKey.IsEmpty ? default : TKey.Create(reader.RightKey),
                LeafChildrenMask,
                OwnedPrefix(cursor, anchorDepth + localLength, splitDepth));
            return new OwnedSubtree(branch.WithKnownHash(_hash, splitDepth - _anchorDepth));
        }

        /// <summary>The bits from <paramref name="from"/> to <paramref name="splitDepth"/> as a standalone compressed prefix.</summary>
        private readonly ReadOnlyMemory<byte> OwnedPrefix(scoped in PbtTraversalPath cursor, int from, int splitDepth)
        {
            int bitCount = splitDepth - from;
            if (bitCount == 0) return default;
            // Zeroed, because the bits are copied in by disjunction.
            byte[] prefix = new byte[sizeof(ushort) + PbtBitPrefix.ByteCount(bitCount)];
            BinaryPrimitives.WriteUInt16BigEndian(prefix, (ushort)bitCount);
            Span<byte> bits = prefix.AsSpan(sizeof(ushort));
            int cursorEnd = Math.Min(splitDepth, _anchorDepth);
            if (from < cursorEnd) PbtBitPrefix.CopyBits(cursor.Bytes, from, cursorEnd - from, bits, 0);
            if (splitDepth > _anchorDepth)
            {
                int start = Math.Max(from, _anchorDepth);
                PbtBitPrefix.CopyBits(Prefix.Bytes, start - _anchorDepth, splitDepth - start, bits, start - from);
            }
            return prefix;
        }
    }
}
