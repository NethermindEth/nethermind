// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using static Nethermind.Pbt.TrieUpdater;

namespace Nethermind.Pbt;

internal static partial class TrieUpdater<TKey, TPath>
    where TKey : unmanaged, IPbtKey<TKey>
    where TPath : struct, IPbtNodePath<TPath>
{
    /// <summary>Appends the node the frontier holds at <paramref name="position"/>, after the descendants the group keeps under it.</summary>
    /// <remarks>
    /// The frontier hands out a fold's result, a boundary node or an untouched block. A fold's result and a boundary node
    /// are anchored at the position that holds them, so each is written with the compressed prefix it owns. An untouched
    /// block is read from the deepest node the group stores above it, which is written with its compressed prefix past
    /// this position only, and so hashed again. A position the group leaves implicit is rebuilt from the children it
    /// does store. Only the descendants are copied: the node itself may still be promoted by an updated sibling's deletion.
    /// </remarks>
    internal static ComposedNode AppendHeld<TFrame>(scoped ref TFrame reader, scoped ref StoredGroupHashes hashes, PbtNodeGroupWriter<TPath> writer, PbtTraversalPath path,
        scoped ref Frontier frontier, scoped Span<FoldResult> results, int position, in LinkParent parent, TrieUpdaterMetrics? metrics)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        Debug.Assert((frontier.Mask & (1u << position)) != 0, "Only a held position is taken.");
        Debug.Assert(position > writer.LastPosition, "Cannot take a PBT node after its output position has passed.");
        NodeGroupPath local = PbtFourLevelGroupGeometry.LocalPathOf(position);
        int copied = reader.CopyRange(writer, position - 2 * local.Width + 2, position);
        if (copied != 0) metrics?.AddBulkCopy(copied);
        ref readonly DecompositionEntry entry = ref frontier.Entries[local.Slot];
        switch (entry.Source)
        {
            case EntrySource.Node:
                FoldResult taken = default;
                frontier.TakeResult(results, local.Slot, ref taken);
                return AppendResult(writer, position, taken);
            case EntrySource.AtPosition when entry.SourcePosition != RootSource:
                ReadOnlyMemory<byte> stored = reader.GetEncoding(entry.SourcePosition);
                return stored.IsEmpty
                    ? AppendImplicitBranch(ref reader, ref hashes, writer, parent, position, metrics)
                    : AppendReanchored(writer, position, local.Length - PbtFourLevelGroupGeometry.LocalPathOf(entry.SourcePosition).Length,
                        PbtNodeReader.FromValidated(stored.Span));
            default:
                FoldResult boundary = default;
                frontier.TakeBoundaryNode(ref reader, ref hashes, local.Slot, metrics).ToFoldResult(path, path.BitDepth, ref boundary);
                return AppendResult(writer, position, boundary);
        }

        // Appends a stored branch anchored skippedBits above position, with the rest of its compressed prefix.
        static ComposedNode AppendReanchored(PbtNodeGroupWriter<TPath> writer, int position, int skippedBits, PbtNodeReader stored)
        {
            int offset = writer.WrittenCount;
            int bitCount = stored.Prefix.BitCount - skippedBits;
            int length = PbtNodeCodec.BranchLength(bitCount, stored.LeftKey.Length, stored.RightKey.Length);
            Span<byte> branch = writer.Append(position, length);
            PbtNodeCodec.CreateBranchEncoding(branch, bitCount, stored.LeftHash, stored.RightHash);
            PbtBitPrefix.CopyBits(stored.Prefix.Bytes, skippedBits, bitCount, branch[3..], 0);
            PbtNodeCodec.WriteBranchTrailer(branch[PbtNodeCodec.BranchPreimageLength(bitCount)..], stored.LeftKey, stored.RightKey);
            return new(offset, length, default);
        }

        // Appends the branch at position that the group leaves implicit, rebuilt from the children it stores.
        // Only an interior prefixless branch is ever left out (PbtNodeGroupCodec.ShouldOmit), so a
        // boundary position or the root with nothing stored, or a child missing, is a corrupt group. The hash its parent's
        // link holds is kept, so the branch is only hashed again if a sibling's deletion promotes it. With that
        // hash known and the branch left out again, its child hashes are only needed by Land, so they are
        // resolved there instead, sparing the rehash of every unchanged node below it.
        static ComposedNode AppendImplicitBranch(scoped ref TFrame reader, scoped ref StoredGroupHashes hashes, PbtNodeGroupWriter<TPath> writer,
            in LinkParent parent, int position, TrieUpdaterMetrics? metrics)
        {
            int width = PbtFourLevelGroupGeometry.WidthOf(position);
            if (width is > 1 and < PbtFourLevelGroupGeometry.BoundarySlots)
            {
                int offset = writer.WrittenCount;
                int length = PbtNodeCodec.BranchLength(0, 0, 0);
                Span<byte> branch = writer.Append(position, length);
                ValueHash256 linkHash = parent.HashOf(position);
                if (linkHash != default)
                {
                    // The link hash only stands in for the child hashes, which omission does not look at.
                    PbtNodeCodec.CreateBranchEncoding(branch, 0, linkHash, linkHash);
                    PbtNodeCodec.WriteBranchTrailer(branch[PbtNodeCodec.BranchPreimageLength(0)..], 0, 0);
                    if (writer.Omits(position, branch)) return new(offset, length, linkHash) { ChildHashesPending = true };
                }

                hashes.GetChildHashes(ref reader, position - width, position - 1, metrics, out ValueHash256 left, out ValueHash256 right);
                if (left != default && right != default)
                {
                    PbtNodeCodec.CreateBranchEncoding(branch, 0, left, right);
                    PbtNodeCodec.WriteBranchTrailer(branch[PbtNodeCodec.BranchPreimageLength(0)..], 0, 0);
                    return new(offset, length, linkHash);
                }
            }
            throw new InvalidDataException("A referenced PBT node is missing.");
        }
    }

    /// <summary>Appends a fold's result anchored at <paramref name="position"/>, with the compressed prefix it owns.</summary>
    private static ComposedNode AppendResult(PbtNodeGroupWriter<TPath> writer, int position, in FoldResult node)
    {
        int offset = writer.WrittenCount;
        TKey leftKey = node.LeafKey;
        if (node.IsLeaf)
        {
            int leafLength = PbtNodeCodec.LeafLength(leftKey.Length);
            PbtNodeCodec.EncodeLeaf(writer.Append(position, leafLength), leftKey);
            return new(offset, leafLength, node.LeafHash);
        }

        Debug.Assert(node.Path.Length == PbtFourLevelGroupGeometry.LocalPathOf(position).Length, "A held result is anchored at the position that holds it.");
        CompressedPrefix prefix = node.Encoding.IsEmpty ? default : CompressedPrefix.FromValidated(node.Encoding);
        TKey rightKey = node.RightLeafKey;
        int leftKeyLength = node.HasLeftLeaf ? leftKey.Length : 0;
        int rightKeyLength = node.HasRightLeaf ? rightKey.Length : 0;
        int length = PbtNodeCodec.BranchLength(prefix.BitCount, leftKeyLength, rightKeyLength);
        Span<byte> branch = writer.Append(position, length);
        PbtNodeCodec.CreateBranchEncoding(branch, prefix.BitCount, node.LeftHash, node.RightHash);
        prefix.Bytes.CopyTo(branch[3..]);
        Span<byte> trailer = branch[PbtNodeCodec.BranchPreimageLength(prefix.BitCount)..];
        PbtNodeCodec.WriteBranchTrailer(trailer, leftKeyLength, rightKeyLength);
        if (node.HasLeftLeaf) leftKey.Bytes.CopyTo(trailer[PbtNodeCodec.BranchTrailerHeaderLength..]);
        if (node.HasRightLeaf) rightKey.Bytes.CopyTo(trailer[(PbtNodeCodec.BranchTrailerHeaderLength + leftKeyLength)..]);
        bool hashKnown = node.KnownHash != default && prefix.BitCount == node.KnownHashBitCount;
        return new(offset, length, hashKnown ? node.KnownHash : default);
    }

    /// <summary>Rebuilds the group from the frontier into <paramref name="writer"/>, returning its root for the caller to place.</summary>
    /// <remarks>
    /// The walk is post-order, and the writer's buffer is its stack memory: every node is appended at its own position
    /// as soon as it is produced, and what moves up the walk is only where it sits. The parent reads it back from there.
    /// A node that rises over an empty sibling is always the last entry, so it only records the side bits it gains and
    /// is rewritten in place once, at the position it lands on (<see cref="Land"/>); a
    /// node that must not stay in the group, an inlined leaf or an omitted branch, is dropped as soon as its position is
    /// settled, while it is still the last entry, keeping only what its parent needs. The root is returned rather than
    /// kept, anchored at <paramref name="resultDepth"/>.
    /// </remarks>
    internal static void Compose<TFrame>(scoped ref TFrame reader, scoped ref StoredGroupHashes hashes, PbtNodeGroupWriter<TPath> writer, PbtTraversalPath path, int resultDepth,
        TrieUpdaterMetrics? metrics, scoped ref Frontier frontier, scoped Span<FoldResult> results, ref FoldResult result)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        BucketFolds folded = default;
        Compose(ref reader, ref hashes, writer, path, resultDepth, metrics, ref frontier, results, ref folded, ref result);
    }

    /// <inheritdoc cref="Compose{TFrame}(ref TFrame, PbtNodeGroupWriter{TPath}, PbtTraversalPath, int, TrieUpdaterMetrics?, ref Frontier, Span{FoldResult}, ref FoldResult)"/>
    /// <remarks>
    /// The touched slots <paramref name="folds"/> still holds are folded as the walk reaches them, so their results are
    /// appended straight away rather than held.
    /// </remarks>
    [SkipLocalsInit]
    private static void Compose<TFrame>(scoped ref TFrame reader, scoped ref StoredGroupHashes hashes, PbtNodeGroupWriter<TPath> writer, PbtTraversalPath path, int resultDepth,
        TrieUpdaterMetrics? metrics, scoped ref Frontier frontier, scoped Span<FoldResult> results, scoped ref BucketFolds folds, ref FoldResult result)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        Debug.Assert((frontier.Unresolved & ~folds.Unfolded) == 0, "Every touched slot is taken by its fold before composition.");
        uint copies = frontier.Copies;
        // A touched slot Decompose placed the input at is held only once its fold returns a node.
        uint frontierMask = frontier.Mask & ~folds.Positions;
        writer.ReserveFirstBuffer(reader.PayloadLength);
        ComposeFrameBuffer frames = default;
        frames[0].Parent = LinkParentAt(ref reader, frontier, -1);
        int frameCount = 1;
        ComposedNode prevSubtree = default;
        while (frameCount != 0)
        {
            // The top of the stack: the position being visited, which is prevSubtree's parent when one of its children has just completed.
            ref ComposeFrame frame = ref frames[frameCount - 1];
            int position = frame.Path.Position;
            if (frame.Stage == ComposeStage.Descend)
            {
                // First visit, going down. A position is held only when no slot under it was touched: a direct copy,
                // or a frontier entry (a boundary node, a fold's result, or an untouched block). Its node is then the
                // whole subtree, returned up as it is. A node the group stores above a touched slot is never held, so
                // the walk goes down past it and rebuilds it from its children.
                if ((copies & (1u << position)) != 0)
                {
                    // A direct copy is stored with its descendants as one contiguous range, ending at the node itself.
                    int copied = reader.CopyRange(writer, position - 2 * frame.Path.Width + 2, position + 1);
                    metrics?.AddBulkCopy(copied);
                    int length = reader.GetEncoding(position).Length;
                    prevSubtree = new ComposedNode(writer.WrittenCount - length, length, frame.Parent.HashOf(position));
                    frameCount--;
                    continue;
                }
                if ((folds.Positions & (1u << position)) != 0)
                {
                    FoldResult taken = default;
                    folds.Take(ref reader, ref hashes, writer, ref path, ref frontier, frame.Path.Slot, ref taken);
                    prevSubtree = taken.IsEmpty ? default : AppendResult(writer, position, taken);
                    frameCount--;
                    continue;
                }
                if ((frontierMask & (1u << position)) != 0)
                {
                    prevSubtree = AppendHeld(ref reader, ref hashes, writer, path, ref frontier, results, position, frame.Parent, metrics);
                    frameCount--;
                    continue;
                }
                // A boundary slot has no children to go down into, so it returns up empty.
                if (frame.Path.Length == PbtFourLevelGroupGeometry.LevelsPerGroup)
                {
                    prevSubtree = default;
                    frameCount--;
                    continue;
                }

                // Nothing is held here, so keep going down the left child.
                frame.Stage = ComposeStage.AwaitingLeft;
                frame.ChildParent = (frontier.Stored & (1u << position)) != 0 ? LinkParentAt(ref reader, frontier, position) : frame.Parent;
                frames[frameCount] = new(frame.Path.Left, frame.ChildParent);
                frameCount++;
                continue;
            }
            if (frame.Stage == ComposeStage.AwaitingLeft)
            {
                // Back up from the left child, whose node is in prevSubtree. With no right child, that node rises to
                // this position. Otherwise the walk goes down the right child, after settling the left node; with no
                // left node, the right one rises here instead.
                uint rightMask = ((1u << (frame.Path.Width - 1)) - 1) << (position - frame.Path.Width + 1);
                // A right half of touched slots alone may fold away entirely, which must be known before the left node
                // is settled; with no left node there is nothing to settle, and the walk finds out on its way back.
                bool rightIsEmpty = ((frontierMask | copies) & rightMask) == 0
                    && ((folds.Positions & rightMask) == 0
                        || (!prevSubtree.IsEmpty && !folds.TryFoldAhead(ref reader, ref hashes, writer, ref path, ref frontier, frame.Path.Right)));
                if (rightIsEmpty)
                {
                    if (!prevSubtree.IsEmpty) prevSubtree = prevSubtree.Rise(position - frame.Path.Width, 0);
                    frameCount--;
                    continue;
                }

                if (prevSubtree.IsEmpty)
                {
                    // With no left node, the right child's node rises to this position.
                    frame.Stage = ComposeStage.AwaitingOnlyRight;
                }
                else
                {
                    int leftPosition = position - frame.Path.Width;
                    SettleLeft(writer, path, leftPosition, Land(ref reader, ref hashes, writer, prevSubtree, leftPosition, metrics), ref frame, metrics);
                    frame.Stage = ComposeStage.AwaitingRight;
                }
                frames[frameCount] = new(frame.Path.Right, frame.ChildParent);
                frameCount++;
                continue;
            }
            if (frame.Stage == ComposeStage.AwaitingOnlyRight)
            {
                // Back up from the right child of a position with no left node: its node, if any, rises to this position.
                if (!prevSubtree.IsEmpty) prevSubtree = prevSubtree.Rise(position - 1, 1);
                frameCount--;
                continue;
            }
            if (frame.Stage == ComposeStage.AwaitingRight)
            {
                // Back up from the right child with both children present: settle the right one and append the branch
                // over the two, returning it up to the parent frame.
                prevSubtree = AppendBranch(writer, path, position, Land(ref reader, ref hashes, writer, prevSubtree, position - 1, metrics), ref frame, metrics);
                frameCount--;
                continue;
            }
        }
        TakeRoot(writer, path, resultDepth, Land(ref reader, ref hashes, writer, prevSubtree, PbtFourLevelGroupGeometry.RootPosition, metrics), ref result);

        // Settles the left child at leftPosition into frame, dropping it when the group does not keep it.
        // A leaf is inlined into the branch above it, so only its key and hash are kept. An omitted branch is hashed now,
        // while it is still the last entry, since the right subtree is written over it. A kept branch stays in the
        // writer, its preimage read back from there to be hashed together with its sibling's.
        static void SettleLeft(PbtNodeGroupWriter<TPath> writer, scoped in PbtTraversalPath path, int leftPosition, in ComposedNode left, ref ComposeFrame frame,
            TrieUpdaterMetrics? metrics)
        {
            ReadOnlySpan<byte> encoding = writer.Entry(left.Offset, left.Length).Span;
            PbtNodeReader node = PbtNodeReader.FromValidated(encoding);
            frame.LeftIsLeaf = node.IsLeaf;
            frame.LeftHash = left.Hash;
            frame.LeftPreimageLength = 0;
            if (node.IsLeaf)
            {
                frame.LeftKey = TKey.Create(node.Key);
                writer.DropLast(leftPosition);
                return;
            }
            if (writer.Omits(leftPosition, encoding))
            {
                if (frame.LeftHash == default)
                {
                    metrics?.IncrementNodeHashes();
                    frame.LeftHash = Blake3Hash.Hash(node.Preimage);
                }
                writer.DropLast(leftPosition);
                return;
            }
            writer.ValidateEntry(path, leftPosition, encoding);
            if (frame.LeftHash == default)
            {
                frame.LeftPreimageOffset = left.Offset;
                frame.LeftPreimageLength = node.Preimage.Length;
            }
        }

        // Settles the right child, the last entry, and appends the branch over it and the frame's left child at position.
        [SkipLocalsInit]
        static ComposedNode AppendBranch(PbtNodeGroupWriter<TPath> writer, scoped in PbtTraversalPath path, int position, in ComposedNode right, ref ComposeFrame frame,
            TrieUpdaterMetrics? metrics)
        {
            int rightPosition = position - 1;
            ReadOnlySpan<byte> encoding = writer.Entry(right.Offset, right.Length).Span;
            PbtNodeReader node = PbtNodeReader.FromValidated(encoding);
            bool rightIsLeaf = node.IsLeaf;
            TKey rightKey = rightIsLeaf ? TKey.Create(node.Key) : default;
            ValueHash256 rightHash = right.Hash;
            ReadOnlySpan<byte> rightPreimage = rightHash == default ? node.Preimage : default;
            ReadOnlySpan<byte> leftPreimage = frame.LeftPreimageLength == 0 ? default : writer.Entry(frame.LeftPreimageOffset, frame.LeftPreimageLength).Span;
            HashPending(leftPreimage, ref frame.LeftHash, rightPreimage, ref rightHash, metrics);
            if (rightIsLeaf || writer.Omits(rightPosition, encoding))
                writer.DropLast(rightPosition);
            else
                writer.ValidateEntry(path, rightPosition, encoding);

            int leftKeyLength = frame.LeftIsLeaf ? frame.LeftKey.Length : 0;
            int rightKeyLength = rightIsLeaf ? rightKey.Length : 0;
            int offset = writer.WrittenCount;
            int length = PbtNodeCodec.BranchLength(0, leftKeyLength, rightKeyLength);
            Span<byte> branch = writer.Append(position, length);
            PbtNodeCodec.CreateBranchEncoding(branch, 0, frame.LeftHash, rightHash);
            Span<byte> trailer = branch[PbtNodeCodec.BranchPreimageLength(0)..];
            PbtNodeCodec.WriteBranchTrailer(trailer, leftKeyLength, rightKeyLength);
            if (frame.LeftIsLeaf) frame.LeftKey.Bytes.CopyTo(trailer[PbtNodeCodec.BranchTrailerHeaderLength..]);
            if (rightIsLeaf) rightKey.Bytes.CopyTo(trailer[(PbtNodeCodec.BranchTrailerHeaderLength + leftKeyLength)..]);
            return new(offset, length, default);

            // Hashes the sibling preimages still pending, together when both are.
            static void HashPending(ReadOnlySpan<byte> leftPreimage, ref ValueHash256 leftHash, ReadOnlySpan<byte> rightPreimage, ref ValueHash256 rightHash, TrieUpdaterMetrics? metrics)
            {
                if (leftPreimage.IsEmpty && rightPreimage.IsEmpty) return;
                if (leftPreimage.IsEmpty)
                {
                    metrics?.IncrementNodeHashes();
                    rightHash = Blake3Hash.Hash(rightPreimage);
                }
                else if (rightPreimage.IsEmpty)
                {
                    metrics?.IncrementNodeHashes();
                    leftHash = Blake3Hash.Hash(leftPreimage);
                }
                else
                {
                    metrics?.AddNodeHashes(2);
                    Blake3Hash.HashTwo(leftPreimage, rightPreimage, out leftHash, out rightHash);
                }
            }
        }
    }

    /// <summary>Moves the last entry, a node that rose over empty siblings, up to <paramref name="position"/>, where it lands.</summary>
    /// <remarks>
    /// A branch gains the side bits it rose over in front of its compressed prefix, and so needs hashing again; a leaf's
    /// encoding does not depend on its position. An implicit branch appended without its child hashes has them resolved here.
    /// </remarks>
    [SkipLocalsInit]
    private static ComposedNode Land<TFrame>(scoped ref TFrame reader, scoped ref StoredGroupHashes hashes, PbtNodeGroupWriter<TPath> writer, in ComposedNode node, int position,
        TrieUpdaterMetrics? metrics)
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
            hashes.GetChildHashes(ref reader, childPosition - width, childPosition - 1, metrics, out leftHash, out rightHash);
        }
        CompressedPrefix prefix = stored.Prefix;
        int riseBitCount = node.RiseBitCount;
        int bitCount = prefix.BitCount + riseBitCount;
        int length = PbtNodeCodec.BranchLength(bitCount, stored.LeftKey.Length, stored.RightKey.Length);
        Span<byte> encoding = writer.Append(position, length);
        PbtNodeCodec.CreateBranchEncoding(encoding, bitCount, leftHash, rightHash);
        encoding[3] |= (byte)(node.RiseBits << (8 - riseBitCount));
        PbtBitPrefix.CopyBits(prefix.Bytes, 0, prefix.BitCount, encoding[3..], riseBitCount);
        PbtNodeCodec.WriteBranchTrailer(encoding[PbtNodeCodec.BranchPreimageLength(bitCount)..], stored.LeftKey, stored.RightKey);
        return new(node.Offset, length, default);
    }

    /// <summary>Detaches the group's root, the last entry, as a result anchored at <paramref name="resultDepth"/>, and drops it from the group.</summary>
    private static void TakeRoot(PbtNodeGroupWriter<TPath> writer, PbtTraversalPath path, int resultDepth, in ComposedNode root, ref FoldResult result)
    {
        if (root.IsEmpty)
        {
            result = default;
            return;
        }
        PbtNodeReader node = PbtNodeReader.FromValidated(writer.Entry(root.Offset, root.Length).Span);
        if (node.IsLeaf) result = new(TKey.Create(node.Key), root.Hash);
        else ReanchoredRoot(path, resultDepth, node, ref result);
        writer.DropLast(PbtFourLevelGroupGeometry.RootPosition);

        // A group's root branch, stored at the group's depth, as a result anchored at resultDepth.
        // A prefix jump leaves resultDepth above the group, so the bits in between are read from path.
        static void ReanchoredRoot(scoped in PbtTraversalPath path, int resultDepth, scoped in PbtNodeReader root, ref FoldResult result)
        {
            CompressedPrefix prefix = root.Prefix;
            int anchorDepth = path.BitDepth;
            int splitDepth = anchorDepth + prefix.BitCount;
            int localLength = Math.Min(splitDepth - resultDepth, PbtFourLevelGroupGeometry.LevelsPerGroup);
            int slot = 0;
            for (int bit = resultDepth; bit < resultDepth + localLength; bit++)
                slot = (slot << 1) | (bit < anchorDepth ? GetBit(path.Bytes, bit) : GetBit(prefix.Bytes, bit - anchorDepth));

            int ownedStart = resultDepth + localLength;
            scoped Span<byte> ownedPrefix = default;
            if (ownedStart < splitDepth)
            {
                ownedPrefix = stackalloc byte[sizeof(ushort) + PbtBitPrefix.ByteCount(splitDepth - ownedStart)];
                // Zeroed, because the bits are copied in by disjunction.
                ownedPrefix.Clear();
                BinaryPrimitives.WriteUInt16BigEndian(ownedPrefix, (ushort)(splitDepth - ownedStart));
                Span<byte> bits = ownedPrefix[sizeof(ushort)..];
                if (ownedStart < anchorDepth) PbtBitPrefix.CopyBits(path.Bytes, ownedStart, anchorDepth - ownedStart, bits, 0);
                int prefixStart = Math.Max(ownedStart, anchorDepth);
                if (prefixStart < splitDepth) PbtBitPrefix.CopyBits(prefix.Bytes, prefixStart - anchorDepth, splitDepth - prefixStart, bits, prefixStart - ownedStart);
            }

            result = new(new NodeGroupPath(slot << (PbtFourLevelGroupGeometry.LevelsPerGroup - localLength), localLength),
                root.LeftHash, root.RightHash,
                root.LeftKey.IsEmpty ? default : TKey.Create(root.LeftKey),
                root.RightKey.IsEmpty ? default : TKey.Create(root.RightKey),
                (byte)((root.LeftKey.IsEmpty ? 0 : LeftLeaf) | (root.RightKey.IsEmpty ? 0 : RightLeaf)), ownedPrefix);
        }
    }

    /// <summary>What a frame waits for: set before its child is pushed, and handled once that child returns up.</summary>
    private enum ComposeStage : byte { Descend, AwaitingLeft, AwaitingRight, AwaitingOnlyRight }

    private struct ComposeFrame(NodeGroupPath path, in LinkParent parent)
    {
        internal NodeGroupPath Path = path;
        /// <summary>The links of the deepest node the group stores above this position.</summary>
        internal LinkParent Parent = parent;
        /// <summary>The links of the deepest node the group stores above this position's children, set as the walk goes down.</summary>
        internal LinkParent ChildParent;
        internal ComposeStage Stage;
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

    [InlineArray(PbtFourLevelGroupGeometry.LevelsPerGroup + 1)]
    private struct ComposeFrameBuffer
    {
        private ComposeFrame _element;
    }

    /// <summary>The touched slots a serial composition folds as its walk reaches them, in ascending slot order.</summary>
    /// <remarks>
    /// A right half holding only touched slots must be known not to fold away before its left sibling is settled, so the
    /// walk may fold ahead into it. The first node that returns is kept until the walk reaches its slot, which happens
    /// before the walk could fold ahead again, so one is enough. The default instance has nothing left to fold.
    /// </remarks>
    private ref struct BucketFolds(FoldContext context, Span<PbtWriteOperation<TKey>> operations, int bitDepth, PartitionOutcome partition)
    {
        private readonly FoldContext _context = context;
        private readonly ReadOnlySpan<int> _counts = partition.Counts;
        private readonly int _bitDepth = bitDepth;
        private readonly int _knownCommonPrefixLength = partition.Plan.KnownCommonPrefixLength;
        private readonly bool _isSorted = partition.Plan.IsSorted;
        private Span<PbtWriteOperation<TKey>> _operations = operations;
        private int _countIndex;
        private FoldResult _foldedAhead;

        /// <summary>The touched slots not folded yet.</summary>
        internal int Unfolded { get; private set; } = partition.UsedMask;

        /// <summary>The boundary positions of the slots not folded yet and of the node folded ahead, which the walk takes from here.</summary>
        internal uint Positions { get; private set; } = BoundaryPositions(partition.UsedMask);

        /// <summary>Takes the node at <paramref name="slot"/>, folding it unless it was folded ahead.</summary>
        internal void Take<TFrame>(scoped ref TFrame reader, scoped ref StoredGroupHashes hashes, PbtNodeGroupWriter<TPath> writer, ref PbtTraversalPath path,
            scoped ref Frontier frontier, int slot, ref FoldResult result)
            where TFrame : struct, IGroupFrame<TKey, TPath>
        {
            Positions &= ~(1u << BoundaryPosition(slot));
            if ((Unfolded >> slot & 1) == 0) FoldResult.Move(ref _foldedAhead, ref result);
            else FoldNext(ref reader, ref hashes, writer, ref path, ref frontier, slot, ref result);
        }

        /// <summary>Folds the touched slots under <paramref name="subtree"/> until one returns a node, which is kept for <see cref="Take"/>.</summary>
        /// <returns>Whether a node was found; false when every touched slot under <paramref name="subtree"/> folded away.</returns>
        internal bool TryFoldAhead<TFrame>(scoped ref TFrame reader, scoped ref StoredGroupHashes hashes, PbtNodeGroupWriter<TPath> writer, ref PbtTraversalPath path,
            scoped ref Frontier frontier, NodeGroupPath subtree)
            where TFrame : struct, IGroupFrame<TKey, TPath>
        {
            int slots = ((1 << subtree.Width) - 1) << subtree.Slot;
            while ((Unfolded & slots) != 0)
            {
                int slot = BitOperations.TrailingZeroCount(Unfolded);
                FoldNext(ref reader, ref hashes, writer, ref path, ref frontier, slot, ref _foldedAhead);
                if (!_foldedAhead.IsEmpty) return true;
                Positions &= ~(1u << BoundaryPosition(slot));
            }
            return false;
        }

        private void FoldNext<TFrame>(scoped ref TFrame reader, scoped ref StoredGroupHashes hashes, PbtNodeGroupWriter<TPath> writer, ref PbtTraversalPath path,
            scoped ref Frontier frontier, int slot, ref FoldResult result)
            where TFrame : struct, IGroupFrame<TKey, TPath>
        {
            Debug.Assert(slot == BitOperations.TrailingZeroCount(Unfolded), "Touched slots are folded in ascending order, which is the order their operations are in.");
            Unfolded &= Unfolded - 1;
            int count = _counts[_countIndex++];
            Span<PbtWriteOperation<TKey>> bucket = _operations[..count];
            _operations = _operations[count..];
            BoundaryNode boundary = TakeBoundary(ref reader, ref hashes, path, ref frontier, slot, _context.Metrics);
            path.AppendMut(slot);
            FoldMutations(_context, ref reader, ref hashes, writer, boundary, bucket, ref path,
                _bitDepth + PbtFourLevelGroupGeometry.LevelsPerGroup, _bitDepth, new BucketPlan(default, _knownCommonPrefixLength, _isSorted), ref result);
            path.Truncate(_bitDepth);
            writer.AddDescendantDelta(slot, result.SizeDelta);
        }

        private static uint BoundaryPositions(int slots)
        {
            uint positions = 0;
            for (; slots != 0; slots &= slots - 1) positions |= 1u << BoundaryPosition(BitOperations.TrailingZeroCount(slots));
            return positions;
        }
    }
}
