// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core.Crypto;
using static Nethermind.Pbt.TrieUpdater;

namespace Nethermind.Pbt;

public static partial class TrieUpdater<TKey, TPath>
    where TKey : unmanaged, IPbtKey<TKey>
    where TPath : struct, IPbtNodePath<TPath>
{
    /// <summary>The node at one boundary slot of a group, read against the traversal cursor that reached it.</summary>
    /// <remarks>
    /// A boundary node is a branch stored at the boundary, a branch anchored above it whose compressed prefix spans
    /// past it, or a leaf. A branch keeps its encoding exactly as the group stores it, together with the absolute
    /// depth that encoding is anchored at, so addressing it from another depth is a comparison rather than a rewrite.
    /// A leaf has no node of its own: it points at the branch that inlines it, which already holds its key past the
    /// branch's group path in its trailer and its hash in its preimage, and <see cref="LeafSource"/> says which of the
    /// two children it is. The anchor depth is then that branch's, which fixes how many key bytes the trailer omits.
    /// The one leaf stored as a node, a single-leaf tree's root, points at that encoding instead.
    /// <see cref="Hash"/> is always known, because decomposition reads it from the parent's child link; a boundary
    /// node is therefore never hashed again to be opened, moved or handed to another thread. An omitted prefixless
    /// branch is never one, since omission only applies to a group's interior
    /// (<see cref="PbtNodeGroupCodec.ShouldOmit"/>) and a spanning branch always carries a prefix.
    /// </remarks>
    public readonly struct BoundaryNode
    {
        private readonly ReadOnlyMemory<byte> _encoding;
        private readonly ValueHash256 _hash;
        private readonly ushort _anchorDepth;
        private readonly LeafSource _source;

        /// <summary>Creates the leaf <paramref name="parent"/>, anchored at <paramref name="parentAnchorDepth"/>, inlines on the given side.</summary>
        /// <remarks>
        /// The hash is taken from the same branch that holds the key, so the two cannot disagree. The fold compares it
        /// to detect a write that changes nothing, and the branch composed above takes it straight back into its
        /// preimage while the key goes to its trailer, so neither is ever recomputed.
        /// </remarks>
        public BoundaryNode(ReadOnlyMemory<byte> parent, int parentAnchorDepth, bool right)
        {
            PbtBranchReader branch = PbtBranchReader.FromValidated(parent.Span);
            Debug.Assert(!(right ? branch.RightKeyPostfix : branch.LeftKeyPostfix).IsEmpty, "An inlined leaf's branch holds its key.");
            _encoding = parent;
            _anchorDepth = (ushort)parentAnchorDepth;
            _source = right ? LeafSource.ParentRight : LeafSource.ParentLeft;
            _hash = right ? branch.RightHash : branch.LeftHash;
        }

        /// <summary>Creates the leaf a single-leaf tree stores as its root, whose hash is its group's identity.</summary>
        /// <remarks>A leaf encoding holds no hash, so this is the one leaf whose hash comes from outside it.</remarks>
        public BoundaryNode(ReadOnlyMemory<byte> encoding, in ValueHash256 hash) : this(encoding, 0, hash, LeafSource.Stored)
        {
            Debug.Assert(PbtNodeCodec.IsLeaf(encoding.Span), "A stored leaf carries a leaf encoding.");
            Debug.Assert(hash != default, "A boundary node knows its hash.");
        }

        /// <param name="anchorDepth">The absolute bit depth <paramref name="encoding"/>'s compressed prefix starts at.</param>
        /// <param name="hash">The hash of <paramref name="encoding"/>, taken from the link that referenced it.</param>
        public BoundaryNode(ReadOnlyMemory<byte> encoding, int anchorDepth, in ValueHash256 hash) : this(encoding, anchorDepth, hash, LeafSource.None)
        {
            Debug.Assert(!encoding.IsEmpty && !PbtNodeCodec.IsLeaf(encoding.Span), "A boundary branch carries a stored branch encoding.");
            Debug.Assert(hash != default, "A boundary node knows its hash.");
        }

        private BoundaryNode(ReadOnlyMemory<byte> encoding, int anchorDepth, in ValueHash256 hash, LeafSource source)
        {
            _encoding = encoding;
            _anchorDepth = (ushort)anchorDepth;
            _hash = hash;
            _source = source;
        }

        public readonly bool IsEmpty => _encoding.IsEmpty;
        /// <summary>The encoding this node is read from: its own, or the branch that inlines it.</summary>
        private readonly ReadOnlyMemory<byte> Encoding => _encoding;
        public readonly bool IsLeaf => _source != LeafSource.None;
        /// <summary>Where this leaf's key is read from, or <see cref="LeafSource.None"/> for a branch.</summary>
        private readonly LeafSource Source => _source;
        /// <summary>The number of leading key bytes this node's inline keys omit, which a cursor through it supplies.</summary>
        public readonly int KeyOffset => PbtNodeCodec.InlineKeyOffset(_anchorDepth);
        /// <summary>A leaf's key past <see cref="KeyOffset"/>, read from the branch that inlines it or from its own encoding.</summary>
        public readonly ReadOnlySpan<byte> LeafKeyPostfix
        {
            get
            {
                Debug.Assert(IsLeaf, "Only a leaf has a complete key.");
                return _source switch
                {
                    LeafSource.ParentLeft => Reader.LeftKeyPostfix,
                    LeafSource.ParentRight => Reader.RightKeyPostfix,
                    _ => PbtNodeCodec.LeafKey(_encoding.Span),
                };
            }
        }
        /// <summary>A leaf's complete key, completed from <paramref name="cursor"/>, a path through its branch's group.</summary>
        public readonly TKey LeafKey(scoped in PbtTraversalPath cursor) => CompleteKey(cursor, LeafKeyPostfix);
        /// <summary>The hash of this node as it is stored, which its parent's link already held.</summary>
        /// <remarks>
        /// It is kept rather than derived because only an inlined leaf could derive it for free, from the branch it
        /// points at. A branch would have to hash its own preimage, which is the work decomposition exists to avoid and
        /// which every node opening a group below it would pay; a stored leaf cannot derive it at all, since a leaf
        /// hashes over its value as well and no value is stored (<see cref="PbtNodeCodec.Hash"/> refuses a leaf).
        /// </remarks>
        public readonly ValueHash256 Hash => _hash;
        /// <summary>The absolute depth this node's encoding is anchored at, never deeper than its boundary slot.</summary>
        public readonly int AnchorDepth => _anchorDepth;
        /// <summary>The branch this node is read from: its own, or the one that inlines it.</summary>
        public readonly PbtBranchReader Reader
        {
            get
            {
                Debug.Assert(_source != LeafSource.Stored, "A stored root leaf has no branch encoding.");
                return PbtBranchReader.FromValidated(_encoding.Span);
            }
        }
        private readonly CompressedPrefix Prefix => Reader.Prefix;
        /// <summary>The absolute depth this branch splits at, past its compressed prefix.</summary>
        public readonly int BranchDepth => _anchorDepth + Prefix.BitCount;
        public readonly ValueHash256 LeftHash => Reader.LeftHash;
        public readonly ValueHash256 RightHash => Reader.RightHash;
        public readonly TKey LeftLeafKey(scoped in PbtTraversalPath cursor) => CompleteKey(cursor, Reader.LeftKeyPostfix);
        public readonly TKey RightLeafKey(scoped in PbtTraversalPath cursor) => CompleteKey(cursor, Reader.RightKeyPostfix);

        private readonly TKey CompleteKey(scoped in PbtTraversalPath cursor, ReadOnlySpan<byte> keyPostfix)
        {
            int keyOffset = KeyOffset;
            Debug.Assert(cursor.BitDepth >= keyOffset * 8, "The cursor covers the key bytes the encoding omits.");
            return PbtKeyOperations.CreateKey<TKey>(cursor.Bytes[..keyOffset], keyPostfix);
        }
        /// <summary>Which children are leaves: <see cref="LeftLeaf"/> and <see cref="RightLeaf"/> bits.</summary>
        public readonly byte LeafChildrenMask
        {
            get
            {
                PbtBranchReader reader = Reader;
                return (byte)((reader.LeftKeyPostfix.IsEmpty ? 0 : LeftLeaf) | (reader.RightKeyPostfix.IsEmpty ? 0 : RightLeaf));
            }
        }

        /// <summary>The bit at <paramref name="bit"/> of the path through this node, taken from the cursor above its anchor.</summary>
        private readonly int PrefixBit(scoped in PbtTraversalPath cursor, int bit) => bit < _anchorDepth
            ? GetBit(cursor.Bytes, bit)
            : GetBit(Prefix.Bytes, bit - _anchorDepth);

        /// <summary>The first bit at or after <paramref name="start"/> where <paramref name="key"/> leaves this node's path.</summary>
        public readonly int FirstDifferingBit(scoped in PbtTraversalPath cursor, TKey key, int start)
        {
            int end = Math.Min(BranchDepth, key.BitLength);
            int anchorEnd = Math.Min(_anchorDepth, end);
            if (start < anchorEnd)
            {
                int difference = PbtKeyOperations.FirstDifferingBit(cursor.Bytes, key.Bytes, start);
                if (difference < anchorEnd) return difference;
                start = anchorEnd;
            }
            return start < end ? _anchorDepth + Prefix.MatchingBits(key, _anchorDepth) : end;
        }

        /// <summary>The boundary slot of the group at <paramref name="bitDepth"/> that this branch's prefix passes through.</summary>
        public readonly int BranchSlot(scoped in PbtTraversalPath cursor, int bitDepth)
        {
            Debug.Assert(BranchDepth >= bitDepth + PbtThreeLevelGroupGeometry.LevelsPerGroup);
            int slot = 0;
            for (int bit = bitDepth; bit < bitDepth + PbtThreeLevelGroupGeometry.LevelsPerGroup; bit++)
                slot = (slot << 1) | PrefixBit(cursor, bit);
            return slot;
        }

        /// <summary>The leaf this branch inlines on one side, which stores no node of its own.</summary>
        public readonly BoundaryNode InlineLeaf(bool right) => new(_encoding, _anchorDepth, right);

        /// <summary>The boundary node <paramref name="frame"/> stores at <paramref name="position"/>.</summary>
        /// <param name="hash">The node's hash: the one its parent's link holds where a link names it, and otherwise the one its encoding needs.</param>
        public static BoundaryNode StoredAt<TFrame>(scoped ref TFrame frame, int position, in ValueHash256 hash)
            where TFrame : struct, IGroupFrame<TKey, TPath>
        {
            ReadOnlyMemory<byte> encoding = frame.GetEncoding(position);
            if (encoding.IsEmpty) throw new InvalidDataException("A referenced PBT node is missing.");
            if (PbtNodeCodec.IsLeaf(encoding.Span)) return new(encoding, hash);
            return new(encoding, AnchorDepthAt(ref frame, position), hash);
        }

        /// <summary>The leaf inlined in the branch <paramref name="frame"/> stores at <paramref name="position"/>, which stores no node of its own.</summary>
        public static BoundaryNode InlineLeafAt<TFrame>(scoped ref TFrame frame, int position, bool right)
            where TFrame : struct, IGroupFrame<TKey, TPath> =>
            new(frame.GetEncoding(position), AnchorDepthAt(ref frame, position), right);

        private static int AnchorDepthAt<TFrame>(scoped ref TFrame frame, int position)
            where TFrame : struct, IGroupFrame<TKey, TPath> =>
            frame.BitDepth + PbtThreeLevelGroupGeometry.LocalPathOf(position).Length;

        /// <summary>Takes a node read under a wider key type, which a fold below the root group continues under its own.</summary>
        /// <remarks>Nothing is decoded: the encoding is the stored one either way, and only reading a key off it is typed.</remarks>
        public static BoundaryNode TakeFrom<TSourceKey, TSourcePath>(in TrieUpdater<TSourceKey, TSourcePath>.BoundaryNode source)
            where TSourceKey : unmanaged, IPbtKey<TSourceKey>
            where TSourcePath : struct, IPbtNodePath<TSourcePath> =>
            new(source.Encoding, source.AnchorDepth, source.Hash, source.Source);

        /// <summary>The hash of this node as the group at <paramref name="depth"/> is keyed by.</summary>
        /// <remarks>
        /// A node addressed at its own anchor keys its group by the hash it already carries. A prefix jump addresses it
        /// deeper, where its shorter encoding hashes differently, and that re-anchored hash is what published the group.
        /// </remarks>
        public readonly ValueHash256 HashAt(int depth) =>
            IsEmpty || IsLeaf || depth == _anchorDepth ? _hash : PbtNodeCodec.HashReanchored(Reader, depth - _anchorDepth);
    }
}
