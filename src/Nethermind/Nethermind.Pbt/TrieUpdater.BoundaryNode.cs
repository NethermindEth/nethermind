// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using Nethermind.Core.Crypto;
using static Nethermind.Pbt.TrieUpdater;

namespace Nethermind.Pbt;

internal static partial class TrieUpdater<TKey, TPath>
    where TKey : unmanaged, IPbtKey<TKey>
    where TPath : struct, IPbtNodePath<TPath>
{
    /// <summary>The node at one boundary slot of a group, read against the traversal cursor that reached it.</summary>
    /// <remarks>
    /// A boundary node is a branch stored at the boundary, a branch anchored above it whose compressed prefix spans
    /// past it, or a leaf. A branch keeps its encoding exactly as the group stores it, together with the absolute
    /// depth that encoding is anchored at, so addressing it from another depth is a comparison rather than a rewrite.
    /// A leaf has no node of its own: it points at the branch that inlines it, which already holds its complete key in
    /// its trailer and its hash in its preimage, and <see cref="LeafSource"/> says which of the two children it is.
    /// The one leaf stored as a node, a single-leaf tree's root, points at that encoding instead.
    /// <see cref="Hash"/> is always known, because decomposition reads it from the parent's child link; a boundary
    /// node is therefore never hashed again to be opened, moved or handed to another thread. An omitted prefixless
    /// branch is never one, since omission only applies to a group's interior
    /// (<see cref="PbtNodeGroupCodec.ShouldOmit"/>) and a spanning branch always carries a prefix.
    /// </remarks>
    internal readonly struct BoundaryNode
    {
        private readonly ReadOnlyMemory<byte> _encoding;
        private readonly ValueHash256 _hash;
        private readonly ushort _anchorDepth;
        private readonly LeafSource _source;

        /// <summary>Creates the leaf <paramref name="parent"/> inlines on the given side.</summary>
        /// <remarks>
        /// The hash is taken from the same branch that holds the key, so the two cannot disagree. The fold compares it
        /// to detect a write that changes nothing, and the branch composed above takes it straight back into its
        /// preimage while the key goes to its trailer, so neither is ever recomputed.
        /// </remarks>
        internal BoundaryNode(ReadOnlyMemory<byte> parent, bool right)
        {
            PbtNodeReader branch = PbtNodeReader.FromValidated(parent.Span);
            Debug.Assert(!(right ? branch.RightKey : branch.LeftKey).IsEmpty, "An inlined leaf's branch holds its key.");
            _encoding = parent;
            _source = right ? LeafSource.ParentRight : LeafSource.ParentLeft;
            _hash = right ? branch.RightHash : branch.LeftHash;
        }

        /// <summary>Creates the leaf a single-leaf tree stores as its root, whose hash is its group's identity.</summary>
        /// <remarks>A leaf encoding holds no hash, so this is the one leaf whose hash comes from outside it.</remarks>
        internal BoundaryNode(ReadOnlyMemory<byte> encoding, in ValueHash256 hash)
        {
            Debug.Assert(PbtNodeReader.FromValidated(encoding.Span).IsLeaf, "A stored leaf carries a leaf encoding.");
            Debug.Assert(hash != default, "A boundary node knows its hash.");
            _encoding = encoding;
            _hash = hash;
            _source = LeafSource.Stored;
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

        private BoundaryNode(ReadOnlyMemory<byte> encoding, int anchorDepth, in ValueHash256 hash, LeafSource source)
        {
            _encoding = encoding;
            _anchorDepth = (ushort)anchorDepth;
            _hash = hash;
            _source = source;
        }

        internal readonly bool IsEmpty => _encoding.IsEmpty;
        /// <summary>The encoding this node is read from: its own, or the branch that inlines it.</summary>
        internal readonly ReadOnlyMemory<byte> Encoding => _encoding;
        internal readonly bool IsLeaf => _source != LeafSource.None;
        /// <summary>Where this leaf's key is read from, or <see cref="LeafSource.None"/> for a branch.</summary>
        internal readonly LeafSource Source => _source;
        /// <summary>A leaf's complete key, read from the branch that inlines it or from its own encoding.</summary>
        internal readonly TKey LeafKey
        {
            get
            {
                Debug.Assert(IsLeaf, "Only a leaf has a complete key.");
                PbtNodeReader node = Reader;
                return TKey.Create(_source switch
                {
                    LeafSource.ParentLeft => node.LeftKey,
                    LeafSource.ParentRight => node.RightKey,
                    _ => node.Key,
                });
            }
        }
        /// <summary>The hash of this node as it is stored, which its parent's link already held.</summary>
        /// <remarks>
        /// It is kept rather than derived because only an inlined leaf could derive it for free, from the branch it
        /// points at. A branch would have to hash its own preimage, which is the work decomposition exists to avoid and
        /// which every node opening a group below it would pay; a stored leaf cannot derive it at all, since a leaf
        /// hashes over its value as well and no value is stored (<see cref="PbtNodeCodec.Hash"/> refuses a leaf).
        /// </remarks>
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
        /// <summary>Which children are leaves: <see cref="LeftLeaf"/> and <see cref="RightLeaf"/> bits.</summary>
        internal readonly byte LeafChildrenMask
        {
            get
            {
                PbtNodeReader reader = Reader;
                return (byte)((reader.LeftKey.IsEmpty ? 0 : LeftLeaf) | (reader.RightKey.IsEmpty ? 0 : RightLeaf));
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
        internal readonly BoundaryNode InlineLeaf(bool right) => new(_encoding, right);

        /// <summary>This node with its encoding copied out of the frame it was read from, so it outlives that frame.</summary>
        /// <remarks>
        /// An inlined leaf is detached as the leaf it is, which copies its own key rather than the branch it was read
        /// from, together with that branch's sibling. Nothing here may keep pointing into the frame: a detached node
        /// is what crosses a thread, where the frame it came from is not even open.
        /// </remarks>
        internal readonly BoundaryNode Owned() => _source switch
        {
            LeafSource.ParentLeft or LeafSource.ParentRight => new BoundaryNode(PbtNodeCodec.EncodeLeaf(LeafKey), _hash),
            _ => _encoding.IsEmpty ? this : new BoundaryNode(_encoding.ToArray(), _anchorDepth, _hash, _source),
        };

        internal static BoundaryNode Move(ref BoundaryNode source)
        {
            BoundaryNode result = source;
            source = default;
            return result;
        }

        /// <summary>Takes a node read under a wider key type, which a fold below the zone nibble continues under its own.</summary>
        /// <remarks>Nothing is decoded: the encoding is the stored one either way, and only reading a key off it is typed.</remarks>
        internal static BoundaryNode TakeFrom<TSourceKey, TSourcePath>(ref TrieUpdater<TSourceKey, TSourcePath>.BoundaryNode source)
            where TSourceKey : unmanaged, IPbtKey<TSourceKey>
            where TSourcePath : struct, IPbtNodePath<TSourcePath>
        {
            BoundaryNode result = new(source.Encoding, source.AnchorDepth, source.Hash, source.Source);
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
            FoldResult reAnchored = default;
            ToFoldResult(cursor, depth, ref reAnchored);
            return reAnchored.Hash(cursor, depth, metrics);
        }

        /// <summary>This node as a composed result addressed at <paramref name="anchorDepth"/>, detached from its frame.</summary>
        /// <remarks>
        /// The result owns everything below that cursor. It keeps the hash of the stored encoding, which the encoder
        /// reuses only when it writes the node back at its own anchor, where that hash is the one it would compute.
        /// </remarks>
        internal readonly void ToFoldResult(scoped in PbtTraversalPath cursor, int anchorDepth, ref FoldResult result)
        {
            Debug.Assert(anchorDepth % PbtFourLevelGroupGeometry.LevelsPerGroup == 0, "A result is anchored at a group depth.");
            if (IsEmpty)
            {
                result = default;
                return;
            }
            if (IsLeaf)
            {
                result = new FoldResult(LeafKey, _hash);
                return;
            }

            int splitDepth = BranchDepth;
            Debug.Assert(splitDepth >= anchorDepth, "A result branches at or below the cursor that addresses it.");
            int localLength = Math.Min(splitDepth - anchorDepth, PbtFourLevelGroupGeometry.LevelsPerGroup);
            int slot = 0;
            for (int bit = anchorDepth; bit < anchorDepth + localLength; bit++) slot = (slot << 1) | PrefixBit(cursor, bit);
            PbtNodeReader reader = Reader;
            result = new FoldResult(new NodeGroupPath(slot << (PbtFourLevelGroupGeometry.LevelsPerGroup - localLength), localLength),
                reader.LeftHash, reader.RightHash,
                reader.LeftKey.IsEmpty ? default : TKey.Create(reader.LeftKey),
                reader.RightKey.IsEmpty ? default : TKey.Create(reader.RightKey),
                LeafChildrenMask,
                OwnedPrefix(cursor, anchorDepth + localLength, splitDepth, stackalloc byte[FoldResult.MaxPrefixLength]), _hash, splitDepth - _anchorDepth);
        }

        /// <summary>The bits from <paramref name="from"/> to <paramref name="splitDepth"/> as a standalone compressed prefix.</summary>
        private readonly Span<byte> OwnedPrefix(scoped in PbtTraversalPath cursor, int from, int splitDepth, Span<byte> buffer)
        {
            int bitCount = splitDepth - from;
            if (bitCount == 0) return default;
            Span<byte> prefix = buffer[..(sizeof(ushort) + PbtBitPrefix.ByteCount(bitCount))];
            // Zeroed, because the bits are copied in by disjunction.
            prefix.Clear();
            BinaryPrimitives.WriteUInt16BigEndian(prefix, (ushort)bitCount);
            Span<byte> bits = prefix[sizeof(ushort)..];
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
