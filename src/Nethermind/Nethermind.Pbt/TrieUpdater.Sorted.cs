// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using static Nethermind.Pbt.TrieUpdater;

namespace Nethermind.Pbt;

internal static partial class TrieUpdater<TKey, TPath>
    where TKey : unmanaged, IPbtKey<TKey>
    where TPath : struct, IPbtNodePath<TPath>
{
    /// <summary>Applies key-sorted <paramref name="sortedOperations"/> on one thread and returns the resulting canonical root.</summary>
    /// <remarks>
    /// Produces the same groups as <see cref="UpdateRoot(IPbtStore, in ValueHash256, PbtWriteBatch{TKey}, PbtPrefixlessBranchOmission)"/>.
    /// Instead of bucketing each group by nibble, decomposing it and composing it back, a single recursion descends one
    /// bit at a time, splitting the sorted range where the bit turns to one and carrying the stored node covering each
    /// position alongside. The recursion returns in post-order, the order a group stores its positions in, so every
    /// node is appended to the group as soon as it is composed. A fold below a boundary slot hands back the final
    /// encoding of the node its parent group stores there.
    /// </remarks>
    /// <param name="sortedOperations">Operations in strictly ascending key order.</param>
    [SkipLocalsInit]
    internal static ValueHash256 UpdateRootSorted(IPbtStore store, in ValueHash256 currentRoot, ReadOnlySpan<PbtWriteOperation<TKey>> sortedOperations,
        PbtPrefixlessBranchOmission prefixlessBranchOmission, TrieUpdaterMetrics? metrics = null, IRefCountingMemoryProvider? memoryProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        AssertSorted(sortedOperations);
        if (sortedOperations.IsEmpty) return currentRoot;
        using IPbtConcurrentWriter storeWriter = store.CreateWriter();
        FoldContext context = new(store, storeWriter, memoryProvider ?? PooledRefCountingMemoryProvider.Instance, metrics, null, null, default, prefixlessBranchOmission);
        Span<byte> pathBuffer = stackalloc byte[PbtBitPrefix.ByteCount(TPath.MaxBitDepth)];
        PbtTraversalPath path = new(pathBuffer);
        Span<byte> encoding = stackalloc byte[MaxNodeLength];
        if (!GroupFrameReader<TKey, TPath>.TryLoad(store, path, currentRoot, metrics, out GroupFrameReader<TKey, TPath> reader))
        {
            AbsentGroupFrame<TKey, TPath> empty = new(0, metrics);
            return FoldSortedRoot(context, ref empty, default, sortedOperations, ref path, encoding);
        }
        using (new GroupFrameReader<TKey, TPath>.Scope(ref reader))
            return FoldSortedRoot(context, ref reader, reader.TakeRoot(), sortedOperations, ref path, encoding);
    }

    /// <summary>Folds the tree root's group and publishes it, returning the new root hash.</summary>
    private static ValueHash256 FoldSortedRoot<TFrame>(FoldContext context, ref TFrame reader, scoped in BoundaryNode root,
        ReadOnlySpan<PbtWriteOperation<TKey>> operations, ref PbtTraversalPath path, scoped Span<byte> encoding)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        using PbtNodeGroupWriter<TPath> writer = new(0, context.MemoryProvider, context.PrefixlessBranchOmission);
        StoredGroupHashes hashes = default;
        SlotNode result = FoldSortedRange(context, ref reader, ref hashes, writer, root, operations, ref path, 0, 0, encoding);
        ValueHash256 hash = default;
        if (!result.IsEmpty)
        {
            ReadOnlySpan<byte> node = encoding[..result.Length];
            node.CopyTo(writer.Append(PbtFourLevelGroupGeometry.RootPosition, node.Length));
            hash = result.Hash != default ? result.Hash : HashBranch(node, context.Metrics);
        }
        PublishGroup(context.Writer, ref reader, writer, path, hash);
        return hash;
    }

    [Conditional("DEBUG")]
    private static void AssertSorted(ReadOnlySpan<PbtWriteOperation<TKey>> operations)
    {
        for (int index = 1; index < operations.Length; index++)
            Debug.Assert(operations[index - 1].Key.CompareTo(operations[index].Key) < 0, "Operations must be in strictly ascending key order.");
    }

    /// <summary>The longest node encoding: a branch whose prefix and both inlined keys are as long as a key can be.</summary>
    private const int MaxNodeLength = PbtNodeCodec.MaxBranchPreimageLength + PbtNodeCodec.BranchTrailerHeaderLength + 2 * PbtStorageTreeKey.MaxLength;

    /// <summary>A node a fold encoded for its caller to place, anchored at the depth the caller places it at.</summary>
    private struct SlotNode(int length, in ValueHash256 hash)
    {
        /// <summary>The encoding's length, or zero when the subtree folded away.</summary>
        internal readonly int Length = length;
        /// <summary>The node's hash, or default when its encoding still has to be hashed.</summary>
        internal readonly ValueHash256 Hash = hash;
        /// <summary>The change in stored size across the groups this node was folded from, still owed to the caller's boundary slot.</summary>
        internal long SizeDelta;
        internal readonly bool IsEmpty => Length == 0;
    }

    /// <summary>The sorted counterpart of <see cref="FoldMutations"/>, with the in-frame recursion in place of bucketing.</summary>
    /// <param name="anchorDepth">The depth the caller places the result at; the range and <paramref name="input"/> share the path down to <paramref name="bitDepth"/>.</param>
    /// <param name="encoding">Receives the result's encoding, at least <see cref="MaxNodeLength"/> bytes.</param>
    [SkipLocalsInit]
    private static SlotNode FoldSortedRange<TFrame>(FoldContext context, ref TFrame ownerReader, ref StoredGroupHashes ownerHashes, PbtNodeGroupWriter<TPath> ownerWriter,
        scoped in BoundaryNode input, ReadOnlySpan<PbtWriteOperation<TKey>> operations, ref PbtTraversalPath path, int bitDepth, int anchorDepth, scoped Span<byte> encoding)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        Debug.Assert(path.BitDepth == bitDepth);
        TrieUpdaterMetrics? metrics = context.Metrics;
        BoundaryNode current = input;
        if (operations.IsEmpty) return EncodeReanchored(current, anchorDepth, encoding);

        if (current.IsEmpty)
        {
            if (operations.Length == 1)
                return operations[0].Value == default ? default : EncodeLeaf(operations[0].Key, HashLeaf(operations[0], metrics), encoding);
        }
        else if (current.IsLeaf)
        {
            if (operations.Length == 1)
            {
                PbtWriteOperation<TKey> operation = operations[0];
                TKey leafKey = current.LeafKey;
                if (operation.Key.Equals(leafKey))
                {
                    if (operation.Value == default) return default;
                    return EncodeLeaf(leafKey, HashLeaf(operation, metrics), encoding);
                }
                if (operation.Value == default) return EncodeLeaf(leafKey, current.Hash, encoding);
                int divergenceDepth = leafKey.FirstDifferingBit(operation.Key, bitDepth);
                if (divergenceDepth < Math.Min(leafKey.BitLength, operation.Key.BitLength))
                    return EncodeTwoLeafBranch(leafKey, current.Hash, operation.Key, HashLeaf(operation, metrics), divergenceDepth, anchorDepth, encoding);
            }
        }
        else if (operations.Length == 1 && current.LeafChildrenMask == (LeftLeaf | RightLeaf))
        {
            PbtWriteOperation<TKey> operation = operations[0];
            bool right = operation.Key.Equals(current.RightLeafKey);
            if (right || operation.Key.Equals(current.LeftLeafKey))
            {
                if (operation.Value == default)
                    return right ? EncodeLeaf(current.LeftLeafKey, current.LeftHash, encoding) : EncodeLeaf(current.RightLeafKey, current.RightHash, encoding);
                ValueHash256 leafHash = HashLeaf(operation, metrics);
                if (leafHash == (right ? current.RightHash : current.LeftHash)) return EncodeReanchored(current, anchorDepth, encoding);
                PbtNodeReader branch = current.Reader;
                int length = EncodeReanchored(branch, anchorDepth - current.AnchorDepth, right ? branch.LeftHash : leafHash, right ? leafHash : branch.RightHash, encoding);
                return new SlotNode(length, default);
            }
            if (operation.Value == default) return EncodeReanchored(current, anchorDepth, encoding);
        }

        // A key ending here sorts before every longer key it prefixes, so it can only be the first.
        if (!TKey.IsFixedLength && bitDepth > 0 && (bitDepth & 7) == 0
            && (operations[0].Key.BitLength == bitDepth || (current.IsLeaf && current.LeafKey.BitLength == bitDepth)))
            return FoldSortedTerminal(context, ref ownerReader, ref ownerHashes, ownerWriter, current, operations, ref path, bitDepth, anchorDepth, encoding);

        TKey firstKey = operations[0].Key;
        int branchDepth = operations.Length == 1 ? firstKey.BitLength : firstKey.FirstDifferingBit(operations[^1].Key, bitDepth);
        if (current.IsLeaf)
            branchDepth = Math.Min(branchDepth, current.LeafKey.FirstDifferingBit(firstKey, bitDepth));
        else if (!current.IsEmpty)
            branchDepth = Math.Min(branchDepth, current.FirstDifferingBit(path, firstKey, bitDepth));
        int groupDepth = branchDepth / PbtFourLevelGroupGeometry.LevelsPerGroup * PbtFourLevelGroupGeometry.LevelsPerGroup;
        if (groupDepth > bitDepth)
        {
            path.AppendKey(firstKey.Bytes, groupDepth);
            SlotNode result = FoldSortedRange(context, ref ownerReader, ref ownerHashes, ownerWriter, current, operations, ref path, groupDepth, anchorDepth, encoding);
            path.Truncate(bitDepth);
            if (ownerReader.BitDepth == bitDepth)
            {
                ownerWriter.AddDescendantDelta(BoundarySlot(firstKey.Bytes, bitDepth), result.SizeDelta);
                result.SizeDelta = 0;
            }
            return result;
        }

        if (ownerReader.BitDepth == bitDepth)
        {
            // Only the tree root folds in the frame it was handed; its root is detached for the caller to write back.
            ComposedNode root = WalkFrame(context, ref ownerReader, ref ownerHashes, ownerWriter, current, operations, path);
            if (root.IsEmpty) return default;
            ownerWriter.Entry(root.Offset, root.Length).Span.CopyTo(encoding);
            ownerWriter.DropLast(PbtFourLevelGroupGeometry.RootPosition);
            return new SlotNode(root.Length, root.Hash);
        }

        return FoldSortedInOwnFrame(context, ref ownerReader, current, operations, ref path, bitDepth, anchorDepth, encoding);
    }

    /// <summary>Folds a range holding a key that ends at <paramref name="bitDepth"/>, apart from the longer keys, as <see cref="FoldMutations"/> does.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    [SkipLocalsInit]
    private static SlotNode FoldSortedTerminal<TFrame>(FoldContext context, ref TFrame ownerReader, ref StoredGroupHashes ownerHashes, PbtNodeGroupWriter<TPath> ownerWriter,
        BoundaryNode current, ReadOnlySpan<PbtWriteOperation<TKey>> operations, ref PbtTraversalPath path, int bitDepth, int anchorDepth, scoped Span<byte> encoding)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        Span<byte> other = stackalloc byte[MaxNodeLength];
        bool hasTerminalLeaf = current.IsLeaf && current.LeafKey.BitLength == bitDepth;
        if (operations[0].Key.BitLength == bitDepth)
        {
            BoundaryNode terminal = hasTerminalLeaf ? BoundaryNode.Move(ref current) : default;
            SlotNode terminalResult = FoldSortedRange(context, ref ownerReader, ref ownerHashes, ownerWriter, terminal, operations[..1], ref path, bitDepth, anchorDepth, other);
            SlotNode descendantResult = FoldSortedRange(context, ref ownerReader, ref ownerHashes, ownerWriter, current, operations[1..], ref path, bitDepth, anchorDepth, encoding);
            if (!terminalResult.IsEmpty && !descendantResult.IsEmpty) throw new ArgumentException("Tree keys must be prefix-free.", nameof(operations));
            SlotNode result = descendantResult;
            if (!terminalResult.IsEmpty)
            {
                other[..terminalResult.Length].CopyTo(encoding);
                result = terminalResult;
            }
            result.SizeDelta = terminalResult.SizeDelta + descendantResult.SizeDelta;
            return result;
        }

        BoundaryNode descendants = default;
        SlotNode descendantsResult = FoldSortedRange(context, ref ownerReader, ref ownerHashes, ownerWriter, descendants, operations, ref path, bitDepth, anchorDepth, other);
        if (!descendantsResult.IsEmpty) throw new ArgumentException("Tree keys must be prefix-free.", nameof(operations));
        SlotNode leafResult = EncodeReanchored(current, anchorDepth, encoding);
        leafResult.SizeDelta = descendantsResult.SizeDelta;
        return leafResult;
    }

    /// <summary>The sorted counterpart of <see cref="FoldMutations"/>'s own-frame fold, opening the group absent or stored.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    [SkipLocalsInit]
    private static SlotNode FoldSortedInOwnFrame<TFrame>(FoldContext context, ref TFrame ownerReader, scoped in BoundaryNode current,
        ReadOnlySpan<PbtWriteOperation<TKey>> operations, ref PbtTraversalPath path, int bitDepth, int anchorDepth, scoped Span<byte> encoding)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        TrieUpdaterMetrics? metrics = context.Metrics;
        if (IsAbsentGroup(current, bitDepth))
        {
            // The owner holds the size of everything below the boundary slot on the way here, which a spanning branch carries down.
            AbsentGroupFrame<TKey, TPath> absent = AbsentFrame(current, path, bitDepth, ownerReader.DescendantBytes(BoundarySlot(path.Bytes, ownerReader.BitDepth)), metrics);
            return FoldSortedAndPublish(context, ref absent, current, operations, ref path, bitDepth, anchorDepth, encoding);
        }
        GroupFrameReader<TKey, TPath> reader = new(context.Store, path, HashAt(current, bitDepth, metrics), metrics);
        using (new GroupFrameReader<TKey, TPath>.Scope(ref reader))
            return FoldSortedAndPublish(context, ref reader, current, operations, ref path, bitDepth, anchorDepth, encoding);
    }

    /// <summary>Folds the group of <paramref name="reader"/> and publishes it, detaching its root as the encoding its caller places.</summary>
    [SkipLocalsInit]
    private static SlotNode FoldSortedAndPublish<TFrame>(FoldContext context, ref TFrame reader, scoped in BoundaryNode current,
        ReadOnlySpan<PbtWriteOperation<TKey>> operations, ref PbtTraversalPath path, int bitDepth, int anchorDepth, scoped Span<byte> encoding)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        TrieUpdaterMetrics? metrics = context.Metrics;
        using PbtNodeGroupWriter<TPath> writer = new(bitDepth, context.MemoryProvider, context.PrefixlessBranchOmission);
        StoredGroupHashes hashes = default;
        ComposedNode root = WalkFrame(context, ref reader, ref hashes, writer, current, operations, path);
        SlotNode result = default;
        ValueHash256 groupHash = default;
        if (!root.IsEmpty)
        {
            ReadOnlySpan<byte> node = writer.Entry(root.Offset, root.Length).Span;
            groupHash = root.Hash != default ? root.Hash : HashBranch(node, metrics);
            result = anchorDepth == bitDepth || PbtNodeReader.FromValidated(node).IsLeaf
                ? Copy(node, groupHash, encoding)
                : new SlotNode(EncodeLifted(PbtNodeReader.FromValidated(node), path, anchorDepth, encoding), default);
            writer.DropLast(PbtFourLevelGroupGeometry.RootPosition);
        }
        result.SizeDelta = PublishGroup(context.Writer, ref reader, writer, path, groupHash);
        return result;
    }

    /// <summary>Rebuilds the open frame's group from <paramref name="input"/> and the range, leaving its root as the last entry, at the root position.</summary>
    private static ComposedNode WalkFrame<TFrame>(FoldContext context, ref TFrame reader, ref StoredGroupHashes hashes, PbtNodeGroupWriter<TPath> writer,
        scoped in BoundaryNode input, ReadOnlySpan<PbtWriteOperation<TKey>> operations, PbtTraversalPath path)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        int bitDepth = path.BitDepth;
        // Only a branch splitting inside the group has anything stored in it; the root position is the input itself.
        uint stored = !input.IsEmpty && !input.IsLeaf && input.BranchDepth < bitDepth + PbtFourLevelGroupGeometry.LevelsPerGroup
            ? reader.StoredPositions & ~(1u << PbtFourLevelGroupGeometry.RootPosition)
            : 0;
        writer.ReserveFirstBuffer(reader.PayloadLength);
        SortedWalk<TFrame> walk = new(context, ref reader, ref hashes, writer, path, input, stored);
        try
        {
            ComposedNode root = Walk(ref walk, default, input.IsEmpty ? default : new Cover(CoverKind.Input, RootSource), operations);
            return root.IsEmpty ? default : Land(ref reader, ref hashes, writer, root, PbtFourLevelGroupGeometry.RootPosition, context.Metrics);
        }
        finally
        {
            if (walk.FoldedAhead is { } foldedAhead) ArrayPool<byte>.Shared.Return(foldedAhead);
            if (walk.FoldedAheadNodes is { } foldedAheadNodes) ArrayPool<SlotNode>.Shared.Return(foldedAheadNodes);
        }
    }

    private static SlotNode Copy(ReadOnlySpan<byte> node, in ValueHash256 hash, Span<byte> encoding)
    {
        node.CopyTo(encoding);
        return new SlotNode(node.Length, hash);
    }

    private static ValueHash256 HashBranch(ReadOnlySpan<byte> encoding, TrieUpdaterMetrics? metrics)
    {
        metrics?.IncrementNodeHashes();
        return Blake3Hash.Hash(PbtNodeReader.FromValidated(encoding).Preimage);
    }

    private static ValueHash256 HashLeaf(in PbtWriteOperation<TKey> operation, TrieUpdaterMetrics? metrics)
    {
        metrics?.IncrementNodeHashes();
        // Copied out first: a span taken off a readonly reference's property would point at a hidden temporary.
        TKey key = operation.Key;
        ValueHash256 value = operation.Value;
        return PbtNodeCodec.HashLeaf(key.Bytes, value.Bytes);
    }

    private static SlotNode EncodeLeaf(TKey key, in ValueHash256 hash, Span<byte> encoding)
    {
        PbtNodeCodec.EncodeLeaf(encoding, key);
        return new SlotNode(PbtNodeCodec.LeafLength(key.Length), hash);
    }

    /// <summary>Encodes <paramref name="node"/> anchored at <paramref name="anchorDepth"/>, at or below its own anchor.</summary>
    private static SlotNode EncodeReanchored(scoped in BoundaryNode node, int anchorDepth, Span<byte> encoding)
    {
        if (node.IsEmpty) return default;
        if (node.IsLeaf) return EncodeLeaf(node.LeafKey, node.Hash, encoding);
        PbtNodeReader branch = node.Reader;
        int skippedBits = anchorDepth - node.AnchorDepth;
        return new SlotNode(EncodeReanchored(branch, skippedBits, branch.LeftHash, branch.RightHash, encoding), skippedBits == 0 ? node.Hash : default);
    }

    /// <summary>Encodes <paramref name="stored"/> with the first <paramref name="skippedBits"/> of its prefix dropped and the given child hashes.</summary>
    private static int EncodeReanchored(scoped PbtNodeReader stored, int skippedBits, in ValueHash256 left, in ValueHash256 right, Span<byte> encoding)
    {
        CompressedPrefix prefix = stored.Prefix;
        int bitCount = prefix.BitCount - skippedBits;
        int length = PbtNodeCodec.BranchLength(bitCount, stored.LeftKey.Length, stored.RightKey.Length);
        PbtNodeCodec.CreateBranchEncoding(encoding, bitCount, left, right);
        if (bitCount != 0) PbtBitPrefix.CopyBits(prefix.Bytes, skippedBits, bitCount, encoding[3..], 0);
        PbtNodeCodec.WriteBranchTrailer(encoding[PbtNodeCodec.BranchPreimageLength(bitCount)..length], stored.LeftKey, stored.RightKey);
        return length;
    }

    /// <summary>Encodes a group's root branch, stored at the group's depth, anchored higher at <paramref name="anchorDepth"/> after a prefix jump.</summary>
    /// <remarks>The bits between the two depths are the path's, which the jump skipped along the range's shared prefix.</remarks>
    private static int EncodeLifted(scoped PbtNodeReader root, scoped in PbtTraversalPath path, int anchorDepth, Span<byte> encoding)
    {
        CompressedPrefix prefix = root.Prefix;
        int liftedBits = path.BitDepth - anchorDepth;
        int bitCount = prefix.BitCount + liftedBits;
        int length = PbtNodeCodec.BranchLength(bitCount, root.LeftKey.Length, root.RightKey.Length);
        PbtNodeCodec.CreateBranchEncoding(encoding, bitCount, root.LeftHash, root.RightHash);
        PbtBitPrefix.CopyBits(path.Bytes, anchorDepth, liftedBits, encoding[3..], 0);
        if (prefix.BitCount != 0) PbtBitPrefix.CopyBits(prefix.Bytes, 0, prefix.BitCount, encoding[3..], liftedBits);
        PbtNodeCodec.WriteBranchTrailer(encoding[PbtNodeCodec.BranchPreimageLength(bitCount)..length], root.LeftKey, root.RightKey);
        return length;
    }

    /// <summary>The branch over two leaves whose keys first differ at <paramref name="branchDepth"/>, anchored at <paramref name="anchorDepth"/>.</summary>
    private static SlotNode EncodeTwoLeafBranch(TKey first, in ValueHash256 firstHash, TKey second, in ValueHash256 secondHash, int branchDepth, int anchorDepth,
        Span<byte> encoding)
    {
        bool firstIsLeft = first.GetBit(branchDepth) == 0;
        TKey leftKey = firstIsLeft ? first : second;
        TKey rightKey = firstIsLeft ? second : first;
        int bitCount = branchDepth - anchorDepth;
        int length = PbtNodeCodec.BranchLength(bitCount, leftKey.Length, rightKey.Length);
        PbtNodeCodec.CreateBranchEncoding(encoding, bitCount, firstIsLeft ? firstHash : secondHash, firstIsLeft ? secondHash : firstHash);
        if (bitCount != 0) PbtBitPrefix.CopyBits(first.Bytes, anchorDepth, bitCount, encoding[3..], 0);
        PbtNodeCodec.WriteBranchTrailer(encoding[PbtNodeCodec.BranchPreimageLength(bitCount)..length], leftKey.Bytes, rightKey.Bytes);
        return new SlotNode(length, default);
    }

    /// <summary>The hash of <paramref name="node"/> as the group at <paramref name="depth"/> is keyed by, as <see cref="BoundaryNode.HashAt"/>.</summary>
    [SkipLocalsInit]
    private static ValueHash256 HashAt(scoped in BoundaryNode node, int depth, TrieUpdaterMetrics? metrics)
    {
        if (node.IsEmpty || node.IsLeaf || depth == node.AnchorDepth) return node.Hash;
        Span<byte> encoding = stackalloc byte[MaxNodeLength];
        int length = EncodeReanchored(node.Reader, depth - node.AnchorDepth, node.LeftHash, node.RightHash, encoding);
        return HashBranch(encoding[..length], metrics);
    }

    /// <summary>Composes the node at <paramref name="local"/> from its <paramref name="cover"/> and the operations below it, appending it to the group.</summary>
    /// <remarks>Mirrors one frame of <see cref="Compose"/>: the returned node is the writer's last entry, or empty.</remarks>
    [SkipLocalsInit]
    private static ComposedNode Walk<TFrame>(scoped ref SortedWalk<TFrame> walk, NodeGroupPath local, Cover cover, ReadOnlySpan<PbtWriteOperation<TKey>> operations)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        if (operations.IsEmpty) return AppendUntouched(ref walk, local, cover);
        if (local.Length == PbtFourLevelGroupGeometry.LevelsPerGroup) return FoldSlot(ref walk, local, cover, operations);
        if (operations.Length == 1 && (cover.IsEmpty || walk.IsLeaf(cover)) && TryWalkSingle(ref walk, local, cover, operations[0], out ComposedNode single))
            return single;

        int depth = walk.BitDepth + local.Length;
        int split = SplitIndex(operations, depth);
        walk.ChildCovers(local, cover, out Cover leftCover, out Cover rightCover);
        int position = local.Position;
        // Two touched boundary nodes each need their old hash to key the group below, so both are hashed together.
        if (local.Length == PbtFourLevelGroupGeometry.LevelsPerGroup - 1 && split != 0 && split != operations.Length
            && leftCover.Kind == CoverKind.Stored && rightCover.Kind == CoverKind.Stored)
            walk.Hashes.GetChildHashesPaired(ref walk.Reader, position - local.Width, position - 1, walk.Context.Metrics, out _, out _);
        ComposedNode left = Walk(ref walk, local.Left, leftCover, operations[..split]);
        ReadOnlySpan<PbtWriteOperation<TKey>> rightOperations = operations[split..];
        bool rightIsEmpty = rightCover.IsEmpty
            ? !HasSet(rightOperations)
            : !left.IsEmpty && !rightOperations.IsEmpty && !HasSet(rightOperations) && !Survives(ref walk, local.Right, rightCover, rightOperations);
        if (rightIsEmpty) return left.IsEmpty ? default : left.Rise(position - local.Width, 0);

        TrieUpdaterMetrics? metrics = walk.Context.Metrics;
        if (left.IsEmpty)
        {
            ComposedNode only = Walk(ref walk, local.Right, rightCover, rightOperations);
            return only.IsEmpty ? default : only.Rise(position - 1, 1);
        }

        ComposeFrame frame = new(local);
        int leftPosition = position - local.Width;
        OmittedPreimage omittedLeft = default;
        bool leftPending = SettleLeftSorted(walk.Writer, walk.Path, leftPosition, left.RiseBitCount == 0 ? left : Land(ref walk.Reader, ref walk.Hashes, walk.Writer, left, leftPosition, metrics), ref frame, omittedLeft);
        ComposedNode right = Walk(ref walk, local.Right, rightCover, rightOperations);
        Debug.Assert(!right.IsEmpty, "A right half known to survive composes a node.");
        return AppendBranchSorted(walk.Writer, walk.Path, position, right.RiseBitCount == 0 ? right : Land(ref walk.Reader, ref walk.Hashes, walk.Writer, right, position - 1, metrics), ref frame,
            leftPending ? (ReadOnlySpan<byte>)omittedLeft : default, metrics);
    }

    /// <summary><see cref="SettleLeft"/>, keeping an omitted branch's preimage in <paramref name="omitted"/> so it is hashed together with its sibling.</summary>
    /// <returns>Whether <paramref name="omitted"/> holds the left child's preimage, still to be hashed.</returns>
    private static bool SettleLeftSorted(PbtNodeGroupWriter<TPath> writer, scoped in PbtTraversalPath path, int leftPosition, in ComposedNode left, ref ComposeFrame frame,
        Span<byte> omitted)
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
            return false;
        }
        if (writer.Omits(leftPosition, encoding))
        {
            bool pending = frame.LeftHash == default;
            if (pending) node.Preimage.CopyTo(omitted);
            writer.DropLast(leftPosition);
            return pending;
        }
        writer.ValidateEntry(path, leftPosition, encoding);
        if (frame.LeftHash == default)
        {
            frame.LeftPreimageOffset = left.Offset;
            frame.LeftPreimageLength = node.Preimage.Length;
        }
        return false;
    }

    /// <summary><see cref="AppendBranch"/>, reading a pending left preimage from <paramref name="omittedLeft"/> when the left child was omitted.</summary>
    [SkipLocalsInit]
    private static ComposedNode AppendBranchSorted(PbtNodeGroupWriter<TPath> writer, scoped in PbtTraversalPath path, int position, in ComposedNode right, ref ComposeFrame frame,
        ReadOnlySpan<byte> omittedLeft, TrieUpdaterMetrics? metrics)
    {
        int rightPosition = position - 1;
        ReadOnlySpan<byte> encoding = writer.Entry(right.Offset, right.Length).Span;
        PbtNodeReader node = PbtNodeReader.FromValidated(encoding);
        bool rightIsLeaf = node.IsLeaf;
        TKey rightKey = rightIsLeaf ? TKey.Create(node.Key) : default;
        ValueHash256 rightHash = right.Hash;
        ReadOnlySpan<byte> rightPreimage = rightHash == default ? node.Preimage : default;
        ReadOnlySpan<byte> leftPreimage = !omittedLeft.IsEmpty ? omittedLeft
            : frame.LeftPreimageLength == 0 ? default : writer.Entry(frame.LeftPreimageOffset, frame.LeftPreimageLength).Span;
        HashPendingPair(leftPreimage, ref frame.LeftHash, rightPreimage, ref rightHash, metrics);
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
    }

    /// <summary><see cref="AppendImplicitBranch"/>, hashing the omitted levels below it pairwise.</summary>
    private static ComposedNode AppendImplicitBranchSorted<TFrame>(scoped ref TFrame reader, scoped ref StoredGroupHashes hashes, PbtNodeGroupWriter<TPath> writer,
        int position, TrieUpdaterMetrics? metrics)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        int width = PbtFourLevelGroupGeometry.WidthOf(position);
        if (width is > 1 and < PbtFourLevelGroupGeometry.BoundarySlots)
        {
            int offset = writer.WrittenCount;
            int length = PbtNodeCodec.BranchLength(0, 0, 0);
            Span<byte> branch = writer.Append(position, length);
            ValueHash256 seeded = hashes.KnownHash(position);
            if (seeded != default)
            {
                PbtNodeCodec.CreateBranchEncoding(branch, 0, seeded, seeded);
                PbtNodeCodec.WriteBranchTrailer(branch[PbtNodeCodec.BranchPreimageLength(0)..], 0, 0);
                if (writer.Omits(position, branch)) return new(offset, length, seeded) { ChildHashesPending = true };
            }

            hashes.GetChildHashesPaired(ref reader, position - width, position - 1, metrics, out ValueHash256 left, out ValueHash256 right);
            if (left != default && right != default)
            {
                PbtNodeCodec.CreateBranchEncoding(branch, 0, left, right);
                PbtNodeCodec.WriteBranchTrailer(branch[PbtNodeCodec.BranchPreimageLength(0)..], 0, 0);
                return new(offset, length, seeded);
            }
        }
        throw new InvalidDataException("A referenced PBT node is missing.");
    }

    /// <summary>The preimage of an omitted prefixless branch, which has no inline leaves.</summary>
    [InlineArray(3 + 2 * ValueHash256.MemorySize)]
    private struct OmittedPreimage
    {
        private byte _element;
    }

    /// <summary>Applies the one operation below an empty or leaf <paramref name="cover"/> when no second leaf results.</summary>
    /// <returns>False when the operation inserts a key beside the leaf, which the walk splits down to where the two diverge.</returns>
    private static bool TryWalkSingle<TFrame>(scoped ref SortedWalk<TFrame> walk, NodeGroupPath local, Cover cover, in PbtWriteOperation<TKey> operation, out ComposedNode node)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        node = default;
        if (cover.IsEmpty)
        {
            if (operation.Value != default) node = AppendLeaf(walk.Writer, local.Position, operation.Key, HashLeaf(operation, walk.Context.Metrics));
            return true;
        }
        if (!operation.Key.Equals(walk.LeafKey(cover)))
        {
            if (operation.Value != default) return false;
            node = AppendUntouched(ref walk, local, cover);
            return true;
        }
        if (operation.Value == default) return true;
        ValueHash256 leafHash = HashLeaf(operation, walk.Context.Metrics);
        node = leafHash == walk.LeafHash(cover) ? AppendUntouched(ref walk, local, cover) : AppendLeaf(walk.Writer, local.Position, operation.Key, leafHash);
        return true;
    }

    /// <summary>Appends the unchanged subtree under <paramref name="local"/>, the node its cover holds there with the descendants the group keeps.</summary>
    private static ComposedNode AppendUntouched<TFrame>(scoped ref SortedWalk<TFrame> walk, NodeGroupPath local, Cover cover)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        int position = local.Position;
        switch (cover.Kind)
        {
            case CoverKind.Empty:
                return default;
            case CoverKind.Stored when cover.Position == position:
                {
                    // A node stored at its own position is one contiguous range with its descendants, ending at the node.
                    int copied = walk.Reader.CopyRange(walk.Writer, position - 2 * local.Width + 2, position + 1);
                    walk.Context.Metrics?.AddBulkCopy(copied);
                    int length = walk.Reader.GetEncoding(position).Length;
                    return new ComposedNode(walk.Writer.WrittenCount - length, length, walk.Hashes.KnownHash(position));
                }
            case CoverKind.Implicit:
                CopyDescendants(ref walk, local);
                return AppendImplicitBranchSorted(ref walk.Reader, ref walk.Hashes, walk.Writer, position, walk.Context.Metrics);
        }

        if (walk.IsLeaf(cover)) return AppendLeaf(walk.Writer, position, walk.LeafKey(cover), walk.LeafHash(cover));

        // A branch anchored above this position, whose compressed prefix passes through it.
        PbtNodeReader node = walk.Node(cover, out int anchorDepth);
        int depth = walk.BitDepth + local.Length;
        if (anchorDepth + node.Prefix.BitCount < walk.BitDepth + PbtFourLevelGroupGeometry.LevelsPerGroup) CopyDescendants(ref walk, local);
        return AppendReanchoredSorted(walk.Writer, position, depth - anchorDepth, node);
    }

    private static void CopyDescendants<TFrame>(scoped ref SortedWalk<TFrame> walk, NodeGroupPath local)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        int position = local.Position;
        int copied = walk.Reader.CopyRange(walk.Writer, position - 2 * local.Width + 2, position);
        if (copied != 0) walk.Context.Metrics?.AddBulkCopy(copied);
    }

    /// <summary>Appends a stored branch anchored <paramref name="skippedBits"/> above <paramref name="position"/>, with the rest of its compressed prefix.</summary>
    private static ComposedNode AppendReanchoredSorted(PbtNodeGroupWriter<TPath> writer, int position, int skippedBits, scoped PbtNodeReader stored)
    {
        int offset = writer.WrittenCount;
        int length = PbtNodeCodec.BranchLength(stored.Prefix.BitCount - skippedBits, stored.LeftKey.Length, stored.RightKey.Length);
        EncodeReanchored(stored, skippedBits, stored.LeftHash, stored.RightHash, writer.Append(position, length));
        return new(offset, length, default);
    }

    /// <summary>Hashes the sibling preimages still pending, together when both are.</summary>
    private static void HashPendingPair(ReadOnlySpan<byte> leftPreimage, ref ValueHash256 leftHash, ReadOnlySpan<byte> rightPreimage, ref ValueHash256 rightHash,
        TrieUpdaterMetrics? metrics)
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

    private static ComposedNode AppendLeaf(PbtNodeGroupWriter<TPath> writer, int position, TKey key, in ValueHash256 hash)
    {
        int offset = writer.WrittenCount;
        int length = PbtNodeCodec.LeafLength(key.Length);
        PbtNodeCodec.EncodeLeaf(writer.Append(position, length), key);
        return new ComposedNode(offset, length, hash);
    }

    /// <summary>Folds the boundary slot <paramref name="local"/> in the group below and appends the node it returns, as <see cref="BucketFolds"/> does.</summary>
    [SkipLocalsInit]
    private static ComposedNode FoldSlot<TFrame>(scoped ref SortedWalk<TFrame> walk, NodeGroupPath local, Cover cover, ReadOnlySpan<PbtWriteOperation<TKey>> operations)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        int slot = local.Slot;
        Span<byte> encoding = stackalloc byte[MaxNodeLength];
        SlotNode result;
        scoped ReadOnlySpan<byte> node;
        if ((walk.FoldedAheadMask >> slot & 1) != 0)
        {
            walk.FoldedAheadMask &= ~(1 << slot);
            result = walk.FoldedAheadNodes![slot];
            node = walk.FoldedAhead.AsSpan(slot * MaxNodeLength, result.Length);
        }
        else
        {
            result = FoldSlotBelow(ref walk, local, cover, operations, encoding);
            node = encoding[..result.Length];
        }
        if (result.IsEmpty) return default;
        int offset = walk.Writer.WrittenCount;
        node.CopyTo(walk.Writer.Append(local.Position, node.Length));
        return new ComposedNode(offset, node.Length, result.Hash);
    }

    /// <summary>Folds the group below the boundary slot <paramref name="local"/>, encoding the node the slot then holds into <paramref name="encoding"/>.</summary>
    private static SlotNode FoldSlotBelow<TFrame>(scoped ref SortedWalk<TFrame> walk, NodeGroupPath local, Cover cover, ReadOnlySpan<PbtWriteOperation<TKey>> operations, scoped Span<byte> encoding)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        int slot = local.Slot;
        BoundaryNode boundary = walk.Boundary(cover);
        int bitDepth = walk.BitDepth;
        int slotDepth = bitDepth + PbtFourLevelGroupGeometry.LevelsPerGroup;
        PbtTraversalPath slotPath = walk.Path;
        slotPath.AppendMut(slot);
        SlotNode result = FoldSortedRange(walk.Context, ref walk.Reader, ref walk.Hashes, walk.Writer, boundary, operations, ref slotPath, slotDepth, slotDepth, encoding);
        slotPath.Truncate(bitDepth);
        walk.Writer.AddDescendantDelta(slot, result.SizeDelta);
        return result;
    }

    /// <summary>Whether anything under <paramref name="local"/> survives deletions that are all <paramref name="operations"/> hold.</summary>
    /// <remarks>
    /// A sibling must be known to survive before its left neighbour is settled, as in <see cref="Compose"/>. The keys are
    /// matched against the cover down to the boundary slots, whose groups are folded ahead and kept for the walk.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Survives<TFrame>(scoped ref SortedWalk<TFrame> walk, NodeGroupPath local, Cover cover, ReadOnlySpan<PbtWriteOperation<TKey>> operations)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        if (cover.IsEmpty) return false;
        if (operations.IsEmpty) return true;
        if (walk.IsLeaf(cover)) return !Contains(operations, walk.LeafKey(cover));
        if (local.Length == PbtFourLevelGroupGeometry.LevelsPerGroup)
        {
            int slot = local.Slot;
            if ((walk.FoldedAheadMask >> slot & 1) != 0) return !walk.FoldedAheadNodes![slot].IsEmpty;
            walk.FoldedAhead ??= ArrayPool<byte>.Shared.Rent(PbtFourLevelGroupGeometry.BoundarySlots * MaxNodeLength);
            walk.FoldedAheadNodes ??= ArrayPool<SlotNode>.Shared.Rent(PbtFourLevelGroupGeometry.BoundarySlots);
            SlotNode result = FoldSlotBelow(ref walk, local, cover, operations, walk.FoldedAhead.AsSpan(slot * MaxNodeLength, MaxNodeLength));
            walk.FoldedAheadNodes![slot] = result;
            walk.FoldedAheadMask |= 1 << slot;
            return !result.IsEmpty;
        }

        int split = SplitIndex(operations, walk.BitDepth + local.Length);
        walk.ChildCovers(local, cover, out Cover leftCover, out Cover rightCover);
        if ((split == 0 && !leftCover.IsEmpty) || (split == operations.Length && !rightCover.IsEmpty)) return true;
        return Survives(ref walk, local.Left, leftCover, operations[..split]) || Survives(ref walk, local.Right, rightCover, operations[split..]);
    }

    /// <summary>The index of the first operation whose key has a one at <paramref name="bit"/>, which every key of the range reaches.</summary>
    private static int SplitIndex(ReadOnlySpan<PbtWriteOperation<TKey>> operations, int bit)
    {
        int low = 0;
        int high = operations.Length;
        while (high - low > 8)
        {
            int middle = (low + high) >>> 1;
            if (operations[middle].Key.GetBit(bit) == 0) low = middle + 1;
            else high = middle;
        }
        while (low < high && operations[low].Key.GetBit(bit) == 0) low++;
        return low;
    }

    private static bool HasSet(ReadOnlySpan<PbtWriteOperation<TKey>> operations)
    {
        foreach (ref readonly PbtWriteOperation<TKey> operation in operations)
            if (operation.Value != default) return true;
        return false;
    }

    private static bool Contains(ReadOnlySpan<PbtWriteOperation<TKey>> operations, TKey key)
    {
        int low = 0;
        int high = operations.Length - 1;
        while (low <= high)
        {
            int middle = (low + high) >>> 1;
            int comparison = operations[middle].Key.CompareTo(key);
            if (comparison == 0) return true;
            if (comparison < 0) low = middle + 1;
            else high = middle - 1;
        }
        return false;
    }

    private enum CoverKind : byte
    {
        Empty,
        /// <summary>The frame's input node: its root branch or leaf.</summary>
        Input,
        /// <summary>The branch stored at <see cref="Cover.Position"/>, at or above the covered position.</summary>
        Stored,
        /// <summary>The prefixless branch the group leaves implicit at <see cref="Cover.Position"/>.</summary>
        Implicit,
        /// <summary>The leaf inlined by the branch at <see cref="Cover.Position"/>, or by the input for <see cref="RootSource"/>.</summary>
        InlineLeaf,
    }

    /// <summary>The existing node holding every stored key under a walk position: anchored at or above it, splitting at or below it.</summary>
    private readonly struct Cover(CoverKind kind, int position, bool right = false)
    {
        internal readonly CoverKind Kind = kind;
        internal readonly byte Position = (byte)position;
        internal readonly bool Right = right;
        internal bool IsEmpty => Kind == CoverKind.Empty;
    }

    /// <summary>The frame one in-frame recursion rebuilds, and the slot results it folded ahead of the walk.</summary>
    private ref struct SortedWalk<TFrame>
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        internal readonly FoldContext Context;
        internal ref TFrame Reader;
        internal ref StoredGroupHashes Hashes;
        internal readonly PbtNodeGroupWriter<TPath> Writer;
        internal readonly PbtTraversalPath Path;
        internal readonly int BitDepth;
        private readonly BoundaryNode _input;
        private readonly uint _stored;
        /// <summary>The encodings of the slots folded ahead of the walk, <see cref="MaxNodeLength"/> bytes per slot.</summary>
        internal byte[]? FoldedAhead;
        internal SlotNode[]? FoldedAheadNodes;
        internal int FoldedAheadMask;

        internal SortedWalk(FoldContext context, ref TFrame reader, ref StoredGroupHashes hashes, PbtNodeGroupWriter<TPath> writer,
            PbtTraversalPath path, in BoundaryNode input, uint stored)
        {
            Context = context;
            Reader = ref reader;
            Hashes = ref hashes;
            Writer = writer;
            Path = path;
            BitDepth = path.BitDepth;
            _input = input;
            _stored = stored;
        }

        internal readonly bool IsLeaf(Cover cover) => cover.Kind == CoverKind.InlineLeaf || (cover.Kind == CoverKind.Input && _input.IsLeaf);

        internal readonly TKey LeafKey(Cover cover) => cover.Kind == CoverKind.Input ? _input.LeafKey : TKey.Create(LeafKeyBytes(cover));

        internal readonly ValueHash256 LeafHash(Cover cover)
        {
            if (cover.Kind == CoverKind.Input) return _input.Hash;
            PbtNodeReader parent = Parent(cover);
            return cover.Right ? parent.RightHash : parent.LeftHash;
        }

        private readonly ReadOnlySpan<byte> LeafKeyBytes(Cover cover)
        {
            if (cover.Kind == CoverKind.Input)
            {
                PbtNodeReader input = _input.Reader;
                return _input.Source switch
                {
                    LeafSource.ParentLeft => input.LeftKey,
                    LeafSource.ParentRight => input.RightKey,
                    _ => input.Key,
                };
            }
            PbtNodeReader parent = Parent(cover);
            return cover.Right ? parent.RightKey : parent.LeftKey;
        }

        private readonly PbtNodeReader Parent(Cover cover) => cover.Position == RootSource
            ? _input.Reader
            : PbtNodeReader.FromValidated(Reader.GetEncoding(cover.Position).Span);

        /// <summary>The branch <paramref name="cover"/> holds, with the absolute depth its compressed prefix starts at.</summary>
        internal readonly PbtNodeReader Node(Cover cover, out int anchorDepth)
        {
            if (cover.Kind == CoverKind.Input)
            {
                anchorDepth = _input.AnchorDepth;
                return _input.Reader;
            }
            anchorDepth = BitDepth + PbtFourLevelGroupGeometry.LocalPathOf(cover.Position).Length;
            return PbtNodeReader.FromValidated(Reader.GetEncoding(cover.Position).Span);
        }

        /// <summary>The covers of <paramref name="local"/>'s two children.</summary>
        internal void ChildCovers(NodeGroupPath local, Cover cover, out Cover left, out Cover right)
        {
            left = default;
            right = default;
            int depth = BitDepth + local.Length;
            switch (cover.Kind)
            {
                case CoverKind.Empty:
                    return;
                case CoverKind.Implicit:
                    left = ChildNode(local.Left, default);
                    right = ChildNode(local.Right, default);
                    return;
            }

            if (IsLeaf(cover))
            {
                if (GetBit(LeafKeyBytes(cover), depth) == 0) left = cover;
                else right = cover;
                return;
            }

            PbtNodeReader node = Node(cover, out int anchorDepth);
            CompressedPrefix prefix = node.Prefix;
            int branchDepth = anchorDepth + prefix.BitCount;
            if (branchDepth > depth)
            {
                if (GetBit(prefix.Bytes, depth - anchorDepth) == 0) left = cover;
                else right = cover;
                return;
            }

            Debug.Assert(branchDepth == depth, "A cover splits at or below the position it covers.");
            int source = cover.Kind == CoverKind.Input ? RootSource : cover.Position;
            left = node.LeftKey.IsEmpty ? ChildNode(local.Left, node.LeftHash) : new Cover(CoverKind.InlineLeaf, source, right: false);
            right = node.RightKey.IsEmpty ? ChildNode(local.Right, node.RightHash) : new Cover(CoverKind.InlineLeaf, source, right: true);
        }

        /// <summary>The node anchored at <paramref name="local"/>, named by a link whose hash is seeded when the parent's encoding holds it.</summary>
        private readonly Cover ChildNode(NodeGroupPath local, in ValueHash256 linkHash)
        {
            int position = local.Position;
            if (linkHash != default) Hashes.Seed(position, linkHash);
            if ((_stored & (1u << position)) != 0) return new Cover(CoverKind.Stored, position);
            if (local.Length < PbtFourLevelGroupGeometry.LevelsPerGroup) return new Cover(CoverKind.Implicit, position);
            throw new InvalidDataException("A referenced PBT node is missing.");
        }

        /// <summary>The boundary node <paramref name="cover"/> holds at a boundary slot, which the fold below it consumes.</summary>
        internal readonly BoundaryNode Boundary(Cover cover) => cover.Kind switch
        {
            CoverKind.Empty => default,
            CoverKind.Input => _input,
            CoverKind.Stored => Reader.TakeBoundaryNode(cover.Position, Hashes.GetHash(ref Reader, cover.Position, Context.Metrics)),
            CoverKind.InlineLeaf => cover.Position == RootSource ? _input.InlineLeaf(cover.Right) : Reader.TakeInlineLeaf(cover.Position, cover.Right),
            _ => throw new InvalidDataException("A boundary node is never left implicit."),
        };
    }
}
