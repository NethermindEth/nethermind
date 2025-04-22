// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Core.Extensions;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Fields.FrEElement;
using Nethermind.Verkle.Tree.TreeNodes;
using Nethermind.Verkle.Tree.Utils;

namespace Nethermind.Verkle.Tree;

public partial class VerkleTree
{
    private void UpdateTreeCommitments(Span<byte> stem, LeafUpdateDelta leafUpdateDelta, bool forSync = false)
    {
        if (_logger.IsTrace) _logger.Trace($"Updating Tree Commitments Stem:{stem.ToHexString()} forSync:{forSync}");
        TraverseContext context = new(stem, leafUpdateDelta) { ForSync = forSync };
        Banderwagon rootDelta = TraverseBranch(ref context);
        if (_logger.IsTrace) _logger.Trace($"RootDelta To Apply: {rootDelta.ToBytes().ToHexString()}");
        UpdateRootNode(rootDelta);
    }

    private Banderwagon TraverseBranch(ref TraverseContext traverseContext)
    {
        var childIndex = traverseContext.Stem[traverseContext.CurrentIndex];
        Span<byte> absolutePath = traverseContext.Stem[..(traverseContext.CurrentIndex + 1)];

        InternalNode? child = GetInternalNode(absolutePath);
        if (child is null || (child.IsStem && Banderwagon.Equals(child.InternalCommitment.Point, Banderwagon.Identity)))
        {
            if (_logger.IsTrace) _logger.Trace("Create New Stem Node");
            // 1. create new suffix node
            // 2. update the C1 or C2 - we already know the leafDelta - traverseContext.LeafUpdateDelta
            // 3. update ExtensionCommitment
            // 4. get the delta for commitment - ExtensionCommitment - 0;
            InternalNode stem = new(VerkleNodeType.StemNode, traverseContext.Stem.ToArray());
            FrE deltaFr = stem.UpdateCommitment(traverseContext.LeafUpdateDelta);
            FrE deltaHash = deltaFr + stem.InitCommitmentHash!.Value;

            // 1. Add internal.stem node
            // 2. return delta from ExtensionCommitment
            SetInternalNode(absolutePath, stem);
            return Committer.ScalarMul(deltaHash, childIndex);
        }

        if (child.IsBranchNode)
        {
            traverseContext.CurrentIndex += 1;
            Banderwagon branchDeltaHash = TraverseBranch(ref traverseContext);
            traverseContext.CurrentIndex -= 1;
            if (_logger.IsTrace) _logger.Trace($"TraverseBranch Delta:{branchDeltaHash.ToBytes().ToHexString()}");

            FrE deltaHash = child.UpdateCommitment(branchDeltaHash);
            SetInternalNode(absolutePath, child);

            return Committer.ScalarMul(deltaHash, childIndex);
        }

        traverseContext.CurrentIndex += 1;
        var changeStemToBranch = TraverseStem(child, ref traverseContext, out Banderwagon stemDeltaHash);
        traverseContext.CurrentIndex -= 1;
        if (_logger.IsTrace) _logger.Trace($"TraverseStem Delta:{stemDeltaHash.ToBytes().ToHexString()}");

        if (changeStemToBranch)
        {
            InternalNode newChild = new(VerkleNodeType.BranchNode);
            newChild.InternalCommitment.AddPoint(child.InternalCommitment.Point);
            // since this is a new child, this would be just the parentDeltaHash.PointToField
            // now since there was a node before and that value is deleted - we need to subtract
            // that from the delta as well
            FrE deltaHash = newChild.UpdateCommitment(stemDeltaHash);
            SetInternalNode(absolutePath, newChild);
            return Committer.ScalarMul(deltaHash, childIndex);
        }

        // in case of stem, no need to update the child commitment - because this commitment is the suffix commitment
        // pass on the update to upper level
        return stemDeltaHash;
    }

    private bool TraverseStem(InternalNode node, ref TraverseContext traverseContext, out Banderwagon stemDeltaHash)
    {
        Debug.Assert(node.IsStem);

        int sharedPathCount =
            VerkleUtils.GetPathDifference(node.Stem!.BytesAsSpan, traverseContext.Stem);

        if (sharedPathCount != 31)
        {
            TraverseInnerStem(node, ref traverseContext, sharedPathCount, out stemDeltaHash);
            return true;
        }

        Span<byte> absolutePath = traverseContext.Stem[..traverseContext.CurrentIndex];
        var childIndex = traverseContext.Stem[traverseContext.CurrentIndex - 1];
        if (traverseContext.ForSync)
        {
            // 1. create new suffix node
            // 2. update the C1 or C2 - we already know the leafDelta - traverseContext.LeafUpdateDelta
            // 3. update ExtensionCommitment
            // 4. get the delta for commitment - ExtensionCommitment - 0;
            InternalNode stem = new(VerkleNodeType.StemNode, traverseContext.Stem.ToArray());
            FrE deltaFr = stem.UpdateCommitment(traverseContext.LeafUpdateDelta);
            FrE deltaHash = deltaFr + stem.InitCommitmentHash!.Value;

            // 1. Add internal.stem node
            // 2. return delta from ExtensionCommitment
            SetInternalNode(absolutePath, stem);
            stemDeltaHash = Committer.ScalarMul(deltaHash, childIndex);
            return false;
        }
        else
        {
            InternalNode updatedStemNode = node.Clone();
            FrE deltaFr = updatedStemNode.UpdateCommitment(traverseContext.LeafUpdateDelta);
            SetInternalNode(absolutePath, updatedStemNode);
            stemDeltaHash = Committer.ScalarMul(deltaFr, childIndex);
            return false;
        }
    }

    private void TraverseInnerStem(InternalNode node, ref TraverseContext traverseContext, int sharedPathCount, out Banderwagon stemDeltaHash)
    {
        var relativePathLength = sharedPathCount - traverseContext.CurrentIndex;
        var oldLeafIndex = node.Stem!.BytesAsSpan[sharedPathCount];
        var newLeafIndex = traverseContext.Stem[sharedPathCount];
        Span<byte> sharedPath = traverseContext.Stem[..sharedPathCount];
        // node share a path but not the complete stem.

        // the internal node will be denoted by their sharedPath
        // 1. create SuffixNode for the traverseContext.Key - get the delta of the commitment
        // 2. set this suffix as child node of the BranchNode - get the commitment point
        // 3. set the existing suffix as the child - get the commitment point
        // 4. update the internal node with the two commitment points
        var newStem = new InternalNode(VerkleNodeType.StemNode, traverseContext.Stem.ToArray());
        FrE deltaFrNewStem = newStem.UpdateCommitment(traverseContext.LeafUpdateDelta);
        FrE deltaHashNewStem = deltaFrNewStem + newStem.InitCommitmentHash!.Value;

        // creating the stem node for the new suffix node
        var stemKey = new byte[sharedPathCount + 1];
        sharedPath.CopyTo(stemKey);
        stemKey[^1] = newLeafIndex;
        SetInternalNode(stemKey, newStem);
        Banderwagon newSuffixCommitmentDelta = Committer.ScalarMul(deltaHashNewStem, newLeafIndex);

        stemKey = new byte[sharedPathCount + 1];
        sharedPath.CopyTo(stemKey);
        stemKey[^1] = oldLeafIndex;
        SetInternalNode(stemKey, node);

        Banderwagon oldSuffixCommitmentDelta =
            Committer.ScalarMul(node.InternalCommitment.PointAsField, oldLeafIndex);

        Banderwagon deltaCommitment = oldSuffixCommitmentDelta + newSuffixCommitmentDelta;

        Banderwagon internalCommitment =
            FillSpaceWithBranchNodes(sharedPath, relativePathLength, deltaCommitment);

        stemDeltaHash = internalCommitment - node.InternalCommitment.Point;
    }

    private Banderwagon FillSpaceWithBranchNodes(in ReadOnlySpan<byte> path, int length, Banderwagon deltaPoint)
    {
        for (var i = 0; i < length; i++)
        {
            InternalNode newInternalNode = new(VerkleNodeType.BranchNode);
            FrE upwardsDelta = newInternalNode.UpdateCommitment(deltaPoint);
            SetInternalNode(path[..^i], newInternalNode);
            deltaPoint = Committer.ScalarMul(upwardsDelta, path[path.Length - i - 1]);
        }

        return deltaPoint;
    }

    public ref struct TraverseContext(Span<byte> stem, LeafUpdateDelta delta)
    {
        public LeafUpdateDelta LeafUpdateDelta { get; } = delta;
        public bool ForSync { get; init; }
        public Span<byte> Stem { get; } = stem;
        public int CurrentIndex { get; set; } = 0;
    }
}
