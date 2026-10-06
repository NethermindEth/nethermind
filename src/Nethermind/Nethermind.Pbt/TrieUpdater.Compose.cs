// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using static Nethermind.Pbt.TrieUpdater;

namespace Nethermind.Pbt;

internal static partial class TrieUpdater<TKey, TPath>
    where TKey : unmanaged, IPbtKey<TKey>
    where TPath : struct, IPbtNodePath<TPath>
{
    /// <summary>Moves the last entry, a node that rose over empty siblings, up to <paramref name="position"/>, where it lands.</summary>
    /// <remarks>
    /// A branch gains the side bits it rose over in front of its compressed prefix, and so needs hashing again; a leaf's
    /// encoding does not depend on its position. An implicit branch appended without its child hashes has them resolved here.
    /// A branch landing at the root of a group on a byte boundary takes the group's last path byte back into its inline keys,
    /// which <paramref name="path"/>, the group's, supplies.
    /// </remarks>
    [SkipLocalsInit]
    private static ComposedNode Land<TFrame>(scoped ref TFrame reader, scoped ref StoredGroupHashes hashes, PbtNodeGroupWriter<TPath> writer,
        scoped in PbtTraversalPath path, in ComposedNode node, int position)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        if (node.RiseBitCount == 0) return node;
        int childPosition = node.EntryPosition;
        // The entry is rewritten over itself, so it is read from a copy.
        Span<byte> previous = stackalloc byte[node.Length];
        writer.Entry(node.Offset, node.Length).Span.CopyTo(previous);
        writer.DropLast(childPosition);
        PbtNodeReader stored = PbtNodeReader.FromValidated(previous);
        if (stored.IsLeaf)
        {
            previous.CopyTo(writer.Append(position, node.Length));
            return new(node.Offset, node.Length, node.Hash);
        }

        ValueHash256 leftHash = stored.LeftHash;
        ValueHash256 rightHash = stored.RightHash;
        if (node.ChildHashesPending)
        {
            int width = PbtFourLevelGroupGeometry.WidthOf(childPosition);
            hashes.GetChildHashesPaired(ref reader, childPosition - width, childPosition - 1, out leftHash, out rightHash);
        }
        CompressedPrefix prefix = stored.Prefix;
        int riseBitCount = node.RiseBitCount;
        int bitCount = prefix.BitCount + riseBitCount;
        int fromKeyOffset = writer.KeyOffsetAt(childPosition), toKeyOffset = writer.KeyOffsetAt(position);
        int length = PbtNodeCodec.BranchPreimageLength(bitCount) + PbtNodeCodec.BranchTrailerHeaderLength + PbtNodeCodec.RebasedKeysLength(stored, fromKeyOffset, toKeyOffset);
        Span<byte> encoding = writer.Append(position, length);
        PbtNodeCodec.CreateBranchEncoding(encoding, bitCount, leftHash, rightHash);
        encoding[3] |= (byte)(node.RiseBits << (8 - riseBitCount));
        PbtBitPrefix.CopyBits(prefix.Bytes, 0, prefix.BitCount, encoding[3..], riseBitCount);
        PbtNodeCodec.WriteRebasedBranchTrailer(encoding[PbtNodeCodec.BranchPreimageLength(bitCount)..], stored, fromKeyOffset, toKeyOffset, path.Bytes);
        return new(node.Offset, length, default);
    }

    /// <summary>Detaches the group's root, the last entry, as a result anchored at <paramref name="resultDepth"/>, and drops it from the group.</summary>
    internal static void TakeRoot(PbtNodeGroupWriter<TPath> writer, PbtTraversalPath path, int resultDepth, in ComposedNode root, ref FoldResult result)
    {
        if (root.IsEmpty)
        {
            result = default;
            return;
        }
        ReadOnlyMemory<byte> encoding = writer.Entry(root.Offset, root.Length);
        PbtNodeReader node = PbtNodeReader.FromValidated(encoding.Span);
        if (node.IsLeaf) result = new(TKey.Create(node.Key), root.Hash);
        else new BoundaryNode(encoding, path.BitDepth, root.Hash, LeafSource.None).ToFoldResult(path, resultDepth, ref result);
        writer.DropLast(PbtFourLevelGroupGeometry.RootPosition);
    }

    private struct ComposeFrame
    {
        internal ValueHash256 LeftHash;
        /// <summary>Where the left child's preimage awaiting its sibling sits in the writer.</summary>
        internal int LeftPreimageOffset;
        /// <summary>The length of the left child's preimage awaiting its sibling, or zero when its hash is already known.</summary>
        internal int LeftPreimageLength;
        internal TKey LeftKey;
        internal bool LeftIsLeaf;
    }

    /// <summary>A node composition has appended to the writer, read back from there by its parent.</summary>
    internal readonly struct ComposedNode(int offset, int length, in ValueHash256 hash)
    {
        internal readonly int Offset = offset;
        internal readonly int Length = length;
        /// <summary>The node's hash, or default while its preimage waits in the writer to be hashed with its sibling's.</summary>
        internal readonly ValueHash256 Hash = hash;
        /// <summary>Whether the encoding is an omitted implicit branch whose child hashes were not resolved, which only <see cref="Land"/> needs.</summary>
        internal bool ChildHashesPending { get; init; }
        /// <summary>The position the entry is appended at, while it has risen above it.</summary>
        internal int EntryPosition { get; private init; }
        /// <summary>The side bits the node rose over, the last one highest, still to be put in front of its compressed prefix.</summary>
        internal byte RiseBits { get; private init; }
        internal byte RiseBitCount { get; private init; }
        internal bool IsEmpty => Length == 0;

        /// <summary>Records a rise from <paramref name="childPosition"/> over an empty sibling, with the node on <paramref name="side"/>, which <see cref="Land"/> applies.</summary>
        internal ComposedNode Rise(int childPosition, int side)
        {
            Debug.Assert(RiseBitCount < PbtFourLevelGroupGeometry.LevelsPerGroup, "A node rises at most to the group root.");
            return this with
            {
                EntryPosition = RiseBitCount == 0 ? childPosition : EntryPosition,
                RiseBits = (byte)((side << RiseBitCount) | RiseBits),
                RiseBitCount = (byte)(RiseBitCount + 1)
            };
        }
    }
}
