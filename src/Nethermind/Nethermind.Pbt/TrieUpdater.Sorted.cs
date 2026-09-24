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
    /// node is appended to the group as soon as it is composed.
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
        GroupFrameReader<TKey, TPath> reader = new(store, 0, currentRoot, metrics);
        using (new GroupFrameReader<TKey, TPath>.Scope(ref reader))
        {
            using PbtNodeGroupWriter<TPath> writer = new(0, context.MemoryProvider, context.PrefixlessBranchOmission);
            BoundaryNode root = reader.TakeRoot(path);
            FoldResult result = FoldSortedRange(context, ref reader, writer, root, sortedOperations, ref path, 0, 0);
            ValueHash256 hash = writer.WriteRoot(path, result, metrics);
            PublishGroup(storeWriter, ref reader, writer, path, hash);
            return hash;
        }
    }

    [Conditional("DEBUG")]
    private static void AssertSorted(ReadOnlySpan<PbtWriteOperation<TKey>> operations)
    {
        for (int index = 1; index < operations.Length; index++)
            Debug.Assert(operations[index - 1].Key.CompareTo(operations[index].Key) < 0, "Operations must be in strictly ascending key order.");
    }

    /// <summary>The sorted counterpart of <see cref="FoldMutations"/>, with the in-frame recursion in place of bucketing.</summary>
    [SkipLocalsInit]
    private static FoldResult FoldSortedRange(FoldContext context, ref GroupFrameReader<TKey, TPath> ownerReader, PbtNodeGroupWriter<TPath> ownerWriter,
        scoped in BoundaryNode input, ReadOnlySpan<PbtWriteOperation<TKey>> operations, ref PbtTraversalPath path, int bitDepth, int resultDepth)
    {
        Debug.Assert(path.BitDepth == bitDepth);
        TrieUpdaterMetrics? metrics = context.Metrics;
        BoundaryNode current = input;
        if (operations.IsEmpty) return current.ToFoldResult(path, resultDepth);

        if (current.IsEmpty)
        {
            if (operations.Length == 1)
                return operations[0].Value == default ? default : CreateLeaf(operations[0], metrics);
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
                    FoldResult leaf = CreateLeaf(operation, metrics);
                    return leaf.LeafHash == current.Hash ? current.ToFoldResult(path, resultDepth) : leaf;
                }
                if (operation.Value == default) return current.ToFoldResult(path, resultDepth);
                int divergenceDepth = leafKey.FirstDifferingBit(operation.Key, bitDepth);
                if (divergenceDepth < Math.Min(leafKey.BitLength, operation.Key.BitLength))
                    return TwoLeafBranch(new FoldResult(leafKey, current.Hash), CreateLeaf(operation, metrics), divergenceDepth, resultDepth);
            }
        }
        else if (operations.Length == 1 && current.LeafChildrenMask == (LeftLeaf | RightLeaf))
        {
            PbtWriteOperation<TKey> operation = operations[0];
            bool right = operation.Key.Equals(current.RightLeafKey);
            if (right || operation.Key.Equals(current.LeftLeafKey))
            {
                if (operation.Value == default)
                    return right ? new FoldResult(current.LeftLeafKey, current.LeftHash) : new FoldResult(current.RightLeafKey, current.RightHash);
                FoldResult leaf = CreateLeaf(operation, metrics);
                if (leaf.LeafHash == (right ? current.RightHash : current.LeftHash)) return current.ToFoldResult(path, resultDepth);
                FoldResult branch = current.ToFoldResult(path, resultDepth);
                return new FoldResult(branch.Path, right ? branch.LeftHash : leaf.LeafHash, right ? leaf.LeafHash : branch.RightHash,
                    branch.LeafKey, branch.RightLeafKey, branch.LeafChildren, branch.Encoding);
            }
            if (operation.Value == default) return current.ToFoldResult(path, resultDepth);
        }

        // A key ending here sorts before every longer key it prefixes, so it can only be the first.
        if (!TKey.IsFixedLength && bitDepth > 0 && (bitDepth & 7) == 0
            && (operations[0].Key.BitLength == bitDepth || (current.IsLeaf && current.LeafKey.BitLength == bitDepth)))
            return FoldSortedTerminal(context, ref ownerReader, ownerWriter, current, operations, ref path, bitDepth, resultDepth);

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
            FoldResult result = FoldSortedRange(context, ref ownerReader, ownerWriter, current, operations, ref path, groupDepth, resultDepth);
            path.Truncate(bitDepth);
            if (ownerReader.BitDepth == bitDepth)
            {
                ownerWriter.AddDescendantDelta(BoundarySlot(firstKey.Bytes, bitDepth), result.SizeDelta);
                result.SizeDelta = 0;
            }
            return result;
        }

        if (ownerReader.BitDepth == bitDepth)
            return WalkFrame(context, ref ownerReader, ownerWriter, current, operations, path, resultDepth);

        return FoldSortedInOwnFrame(context, in ownerReader, current, operations, ref path, bitDepth, resultDepth);
    }

    /// <summary>Folds a range holding a key that ends at <paramref name="bitDepth"/>, apart from the longer keys, as <see cref="FoldMutations"/> does.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static FoldResult FoldSortedTerminal(FoldContext context, ref GroupFrameReader<TKey, TPath> ownerReader, PbtNodeGroupWriter<TPath> ownerWriter,
        BoundaryNode current, ReadOnlySpan<PbtWriteOperation<TKey>> operations, ref PbtTraversalPath path, int bitDepth, int resultDepth)
    {
        bool hasTerminalLeaf = current.IsLeaf && current.LeafKey.BitLength == bitDepth;
        if (operations[0].Key.BitLength == bitDepth)
        {
            BoundaryNode terminal = hasTerminalLeaf ? BoundaryNode.Move(ref current) : default;
            FoldResult terminalResult = FoldSortedRange(context, ref ownerReader, ownerWriter, terminal, operations[..1], ref path, bitDepth, resultDepth);
            FoldResult descendantResult = FoldSortedRange(context, ref ownerReader, ownerWriter, current, operations[1..], ref path, bitDepth, resultDepth);
            if (!terminalResult.IsEmpty && !descendantResult.IsEmpty) throw new ArgumentException("Tree keys must be prefix-free.", nameof(operations));
            FoldResult result = terminalResult.IsEmpty ? descendantResult : terminalResult;
            result.SizeDelta = terminalResult.SizeDelta + descendantResult.SizeDelta;
            return result;
        }

        BoundaryNode descendants = default;
        FoldResult descendantsResult = FoldSortedRange(context, ref ownerReader, ownerWriter, descendants, operations, ref path, bitDepth, resultDepth);
        if (!descendantsResult.IsEmpty) throw new ArgumentException("Tree keys must be prefix-free.", nameof(operations));
        FoldResult leafResult = current.ToFoldResult(path, resultDepth);
        leafResult.SizeDelta = descendantsResult.SizeDelta;
        return leafResult;
    }

    /// <summary>The sorted counterpart of <see cref="FoldInOwnFrame"/>.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    [SkipLocalsInit]
    private static FoldResult FoldSortedInOwnFrame(FoldContext context, in GroupFrameReader<TKey, TPath> ownerReader, scoped in BoundaryNode current,
        ReadOnlySpan<PbtWriteOperation<TKey>> operations, ref PbtTraversalPath path, int bitDepth, int resultDepth)
    {
        TrieUpdaterMetrics? metrics = context.Metrics;
        GroupFrameReader<TKey, TPath> reader = new(context.Store, bitDepth, metrics);
        using (new GroupFrameReader<TKey, TPath>.Scope(ref reader))
        {
            using PbtNodeGroupWriter<TPath> writer = new(bitDepth, context.MemoryProvider, context.PrefixlessBranchOmission);
            ResolveAbsentGroup(ref reader, in ownerReader, path, current);
            if (!reader.IsResolved) reader.SetGroupHash(current.HashAt(path, bitDepth, metrics));
            FoldResult result = WalkFrame(context, ref reader, writer, current, operations, path, resultDepth);
            PbtTraversalPath resultCursor = path.Truncated(stackalloc byte[PbtBitPrefix.ByteCount(TPath.MaxBitDepth)], resultDepth);
            ValueHash256 hash = result.Hash(resultCursor, bitDepth, metrics);
            result.SizeDelta = PublishGroup(context.Writer, ref reader, writer, path, hash);
            if (result.Kind == NodeKind.Branch) result = result.WithKnownHash(hash, result.BranchDepth(resultCursor) - bitDepth);
            return result;
        }
    }

    /// <summary>Rebuilds the open frame's group from <paramref name="input"/> and the range, returning its root for the caller to place.</summary>
    private static FoldResult WalkFrame(FoldContext context, ref GroupFrameReader<TKey, TPath> reader, PbtNodeGroupWriter<TPath> writer,
        scoped in BoundaryNode input, ReadOnlySpan<PbtWriteOperation<TKey>> operations, PbtTraversalPath path, int resultDepth)
    {
        int bitDepth = path.BitDepth;
        // Only a branch splitting inside the group has anything stored in it; the root position is the input itself.
        uint stored = !input.IsEmpty && !input.IsLeaf && input.BranchDepth < bitDepth + PbtFourLevelGroupGeometry.LevelsPerGroup
            ? reader.StoredPositions(path) & ~(1u << PbtFourLevelGroupGeometry.RootPosition)
            : 0;
        writer.ReserveFirstBuffer(reader.PayloadLength);
        SortedWalk walk = new(context, ref reader, writer, path, input, stored);
        try
        {
            ComposedNode root = Walk(ref walk, default, input.IsEmpty ? default : new Cover(CoverKind.Input, RootSource), operations);
            return TakeRoot(writer, path, resultDepth, Land(ref reader, writer, path, root, PbtFourLevelGroupGeometry.RootPosition));
        }
        finally
        {
            if (walk.FoldedAhead is { } foldedAhead) ArrayPool<FoldResult>.Shared.Return(foldedAhead);
        }
    }

    /// <summary>Composes the node at <paramref name="local"/> from its <paramref name="cover"/> and the operations below it, appending it to the group.</summary>
    /// <remarks>Mirrors one frame of <see cref="Compose"/>: the returned node is the writer's last entry, or empty.</remarks>
    [SkipLocalsInit]
    private static ComposedNode Walk(scoped ref SortedWalk walk, NodeGroupPath local, Cover cover, ReadOnlySpan<PbtWriteOperation<TKey>> operations)
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
            walk.Reader.GetChildHashesPaired(walk.Path, position - local.Width, position - 1, out _, out _);
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
        bool leftPending = SettleLeftSorted(walk.Writer, walk.Path, leftPosition, left.RiseBitCount == 0 ? left : Land(ref walk.Reader, walk.Writer, walk.Path, left, leftPosition), ref frame, omittedLeft);
        ComposedNode right = Walk(ref walk, local.Right, rightCover, rightOperations);
        Debug.Assert(!right.IsEmpty, "A right half known to survive composes a node.");
        return AppendBranchSorted(walk.Writer, walk.Path, position, right.RiseBitCount == 0 ? right : Land(ref walk.Reader, walk.Writer, walk.Path, right, position - 1), ref frame,
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
    }

    /// <summary><see cref="AppendImplicitBranch"/>, hashing the omitted levels below it pairwise.</summary>
    private static ComposedNode AppendImplicitBranchSorted(scoped ref GroupFrameReader<TKey, TPath> reader, PbtNodeGroupWriter<TPath> writer, PbtTraversalPath path,
        int position)
    {
        int width = PbtFourLevelGroupGeometry.WidthOf(position);
        if (width is > 1 and < PbtFourLevelGroupGeometry.BoundarySlots)
        {
            int offset = writer.WrittenCount;
            int length = PbtNodeCodec.BranchLength(0, 0, 0);
            Span<byte> branch = writer.Append(position, length);
            ValueHash256 seeded = reader.SeededHash(position);
            if (seeded != default)
            {
                PbtNodeCodec.CreateBranchEncoding(branch, 0, seeded, seeded);
                PbtNodeCodec.WriteBranchTrailer(branch[PbtNodeCodec.BranchPreimageLength(0)..], 0, 0);
                if (writer.Omits(position, branch)) return new(offset, length, seeded) { ChildHashesPending = true };
            }

            reader.GetChildHashesPaired(path, position - width, position - 1, out ValueHash256 left, out ValueHash256 right);
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
    private static bool TryWalkSingle(scoped ref SortedWalk walk, NodeGroupPath local, Cover cover, in PbtWriteOperation<TKey> operation, out ComposedNode node)
    {
        node = default;
        if (cover.IsEmpty)
        {
            if (operation.Value != default) node = AppendResult(walk.Writer, local.Position, CreateLeaf(operation, walk.Context.Metrics));
            return true;
        }
        if (!operation.Key.Equals(walk.LeafKey(cover)))
        {
            if (operation.Value != default) return false;
            node = AppendUntouched(ref walk, local, cover);
            return true;
        }
        if (operation.Value == default) return true;
        FoldResult leaf = CreateLeaf(operation, walk.Context.Metrics);
        node = leaf.LeafHash == walk.LeafHash(cover) ? AppendUntouched(ref walk, local, cover) : AppendResult(walk.Writer, local.Position, leaf);
        return true;
    }

    /// <summary>Appends the unchanged subtree under <paramref name="local"/>, the node its cover holds there with the descendants the group keeps.</summary>
    private static ComposedNode AppendUntouched(scoped ref SortedWalk walk, NodeGroupPath local, Cover cover)
    {
        int position = local.Position;
        switch (cover.Kind)
        {
            case CoverKind.Empty:
                return default;
            case CoverKind.Stored when cover.Position == position:
                {
                    // A node stored at its own position is one contiguous range with its descendants, ending at the node.
                    int copied = walk.Reader.CopyRange(walk.Path, walk.Writer, position - 2 * local.Width + 2, position + 1);
                    walk.Context.Metrics?.AddBulkCopy(copied);
                    int length = walk.Reader.GetEncoding(walk.Path, position).Length;
                    return new ComposedNode(walk.Writer.WrittenCount - length, length, walk.Reader.SeededHash(position));
                }
            case CoverKind.Implicit:
                CopyDescendants(ref walk, local);
                return AppendImplicitBranchSorted(ref walk.Reader, walk.Writer, walk.Path, position);
        }

        if (walk.IsLeaf(cover)) return AppendResult(walk.Writer, position, new FoldResult(walk.LeafKey(cover), walk.LeafHash(cover)));

        // A branch anchored above this position, whose compressed prefix passes through it.
        PbtNodeReader node = walk.Node(cover, out int anchorDepth);
        int depth = walk.BitDepth + local.Length;
        if (anchorDepth + node.Prefix.BitCount < walk.BitDepth + PbtFourLevelGroupGeometry.LevelsPerGroup) CopyDescendants(ref walk, local);
        return AppendReanchored(walk.Writer, position, depth - anchorDepth, node);
    }

    private static void CopyDescendants(scoped ref SortedWalk walk, NodeGroupPath local)
    {
        int position = local.Position;
        int copied = walk.Reader.CopyRange(walk.Path, walk.Writer, position - 2 * local.Width + 2, position);
        if (copied != 0) walk.Context.Metrics?.AddBulkCopy(copied);
    }

    /// <summary>Folds the boundary slot <paramref name="local"/> in the group below and appends its result, as <see cref="BucketFolds"/> does.</summary>
    private static ComposedNode FoldSlot(scoped ref SortedWalk walk, NodeGroupPath local, Cover cover, ReadOnlySpan<PbtWriteOperation<TKey>> operations)
    {
        FoldResult result = FoldSlotResult(ref walk, local, cover, operations);
        return result.IsEmpty ? default : AppendResult(walk.Writer, local.Position, result);
    }

    [SkipLocalsInit]
    private static FoldResult FoldSlotResult(scoped ref SortedWalk walk, NodeGroupPath local, Cover cover, ReadOnlySpan<PbtWriteOperation<TKey>> operations)
    {
        int slot = local.Slot;
        if ((walk.FoldedAheadMask >> slot & 1) != 0)
        {
            walk.FoldedAheadMask &= ~(1 << slot);
            return FoldResult.Move(ref walk.FoldedAhead![slot]);
        }

        BoundaryNode boundary = walk.Boundary(cover);
        int bitDepth = walk.BitDepth;
        PbtTraversalPath slotPath = walk.Path;
        slotPath.AppendMut(slot);
        FoldResult result = FoldSortedRange(walk.Context, ref walk.Reader, walk.Writer, boundary, operations, ref slotPath,
            bitDepth + PbtFourLevelGroupGeometry.LevelsPerGroup, bitDepth);
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
    private static bool Survives(scoped ref SortedWalk walk, NodeGroupPath local, Cover cover, ReadOnlySpan<PbtWriteOperation<TKey>> operations)
    {
        if (cover.IsEmpty) return false;
        if (operations.IsEmpty) return true;
        if (walk.IsLeaf(cover)) return !Contains(operations, walk.LeafKey(cover));
        if (local.Length == PbtFourLevelGroupGeometry.LevelsPerGroup)
        {
            int slot = local.Slot;
            if ((walk.FoldedAheadMask >> slot & 1) != 0) return !walk.FoldedAhead![slot].IsEmpty;
            FoldResult result = FoldSlotResult(ref walk, local, cover, operations);
            walk.FoldedAhead ??= ArrayPool<FoldResult>.Shared.Rent(PbtFourLevelGroupGeometry.BoundarySlots);
            walk.FoldedAheadMask |= 1 << slot;
            bool survives = !result.IsEmpty;
            walk.FoldedAhead[slot] = result;
            return survives;
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
    private ref struct SortedWalk
    {
        internal readonly FoldContext Context;
        internal ref GroupFrameReader<TKey, TPath> Reader;
        internal readonly PbtNodeGroupWriter<TPath> Writer;
        internal readonly PbtTraversalPath Path;
        internal readonly int BitDepth;
        private readonly BoundaryNode _input;
        private readonly uint _stored;
        internal FoldResult[]? FoldedAhead;
        internal int FoldedAheadMask;

        internal SortedWalk(FoldContext context, ref GroupFrameReader<TKey, TPath> reader, PbtNodeGroupWriter<TPath> writer,
            PbtTraversalPath path, in BoundaryNode input, uint stored)
        {
            Context = context;
            Reader = ref reader;
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
            : PbtNodeReader.FromValidated(Reader.GetEncoding(Path, cover.Position).Span);

        /// <summary>The branch <paramref name="cover"/> holds, with the absolute depth its compressed prefix starts at.</summary>
        internal readonly PbtNodeReader Node(Cover cover, out int anchorDepth)
        {
            if (cover.Kind == CoverKind.Input)
            {
                anchorDepth = _input.AnchorDepth;
                return _input.Reader;
            }
            anchorDepth = BitDepth + PbtFourLevelGroupGeometry.LocalPathOf(cover.Position).Length;
            return PbtNodeReader.FromValidated(Reader.GetEncoding(Path, cover.Position).Span);
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
            if (linkHash != default) Reader.SeedHash(position, linkHash);
            if ((_stored & (1u << position)) != 0) return new Cover(CoverKind.Stored, position);
            if (local.Length < PbtFourLevelGroupGeometry.LevelsPerGroup) return new Cover(CoverKind.Implicit, position);
            throw new InvalidDataException("A referenced PBT node is missing.");
        }

        /// <summary>The boundary node <paramref name="cover"/> holds at a boundary slot, which the fold below it consumes.</summary>
        internal readonly BoundaryNode Boundary(Cover cover) => cover.Kind switch
        {
            CoverKind.Empty => default,
            CoverKind.Input => _input,
            CoverKind.Stored => Reader.TakeBoundaryNode(Path, cover.Position),
            CoverKind.InlineLeaf => cover.Position == RootSource ? _input.InlineLeaf(cover.Right) : Reader.TakeInlineLeaf(Path, cover.Position, cover.Right),
            _ => throw new InvalidDataException("A boundary node is never left implicit."),
        };
    }
}
