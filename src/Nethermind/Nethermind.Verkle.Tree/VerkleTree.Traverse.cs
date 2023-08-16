// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core.Extensions;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Fields.FrEElement;
using Nethermind.Verkle.Tree.Nodes;
using Nethermind.Verkle.Tree.Utils;


namespace Nethermind.Verkle.Tree;

public partial class VerkleTree
{
    private void UpdateTreeCommitments(Span<byte> stem, LeafUpdateDelta leafUpdateDelta, bool forSync = false)
    {
        TraverseContext context = new(stem, leafUpdateDelta) { ForSync = forSync };
        Banderwagon rootDelta = TraverseBranch(context);
        if(_logger.IsTrace) _logger.Trace($"RootDelta To Apply: {rootDelta.ToBytes().ToHexString()}");
        UpdateRootNode(rootDelta);
    }

    private Banderwagon TraverseBranch(TraverseContext traverseContext)
    {
        byte childIndex = traverseContext.Stem[traverseContext.CurrentIndex];
        byte[] absolutePath = traverseContext.Stem[..(traverseContext.CurrentIndex + 1)].ToArray();

        InternalNode? child = GetInternalNode(absolutePath);
        if (child is null || (child.IsStem && child.InternalCommitment.Point.Equals(new Banderwagon())))
        {
            if(_logger.IsTrace) _logger.Trace($"Create New Stem Node");
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
            Banderwagon branchDeltaHash = TraverseBranch(traverseContext);
            traverseContext.CurrentIndex -= 1;
            if(_logger.IsTrace) _logger.Trace($"TraverseBranch Delta:{branchDeltaHash.ToBytes().ToHexString()}");

            FrE deltaHash = child.UpdateCommitment(branchDeltaHash);
            SetInternalNode(absolutePath, child);

            return Committer.ScalarMul(deltaHash, childIndex);
        }

        traverseContext.CurrentIndex += 1;
        (Banderwagon stemDeltaHash, bool changeStemToBranch) = TraverseStem(child, traverseContext);
        traverseContext.CurrentIndex -= 1;
        if(_logger.IsTrace) _logger.Trace($"TraverseStem Delta:{stemDeltaHash.ToBytes().ToHexString()}");

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

    private (Banderwagon, bool) TraverseStem(InternalNode node, TraverseContext traverseContext)
    {
        Debug.Assert(node.IsStem);

        (List<byte> sharedPath, byte? pathDiffIndexOld, byte? pathDiffIndexNew) =
            VerkleUtils.GetPathDifference(node.Stem!.Bytes, traverseContext.Stem.ToArray());

        if (sharedPath.Count != 31)
        {
            int relativePathLength = sharedPath.Count - traverseContext.CurrentIndex;
            // byte[] relativeSharedPath = sharedPath.ToArray()[traverseContext.CurrentIndex..].ToArray();
            byte oldLeafIndex = pathDiffIndexOld ?? throw new ArgumentException();
            byte newLeafIndex = pathDiffIndexNew ?? throw new ArgumentException();
            // node share a path but not the complete stem.

            // the internal node will be denoted by their sharedPath
            // 1. create SuffixNode for the traverseContext.Key - get the delta of the commitment
            // 2. set this suffix as child node of the BranchNode - get the commitment point
            // 3. set the existing suffix as the child - get the commitment point
            // 4. update the internal node with the two commitment points
            InternalNode newStem = new InternalNode(VerkleNodeType.StemNode, traverseContext.Stem.ToArray());
            FrE deltaFrNewStem = newStem.UpdateCommitment(traverseContext.LeafUpdateDelta);
            FrE deltaHashNewStem = deltaFrNewStem + newStem.InitCommitmentHash!.Value;

            // creating the stem node for the new suffix node
            byte[] stemKey = new byte[sharedPath.Count + 1];
            sharedPath.CopyTo(stemKey);
            stemKey[^1] = newLeafIndex;
            SetInternalNode(stemKey, newStem);
            Banderwagon newSuffixCommitmentDelta = Committer.ScalarMul(deltaHashNewStem, newLeafIndex);

            stemKey = new byte[sharedPath.Count + 1];
            sharedPath.CopyTo(stemKey);
            stemKey[^1] = oldLeafIndex;
            SetInternalNode(stemKey, node);

            Banderwagon oldSuffixCommitmentDelta =
                Committer.ScalarMul(node.InternalCommitment.PointAsField, oldLeafIndex);

            Banderwagon deltaCommitment = oldSuffixCommitmentDelta + newSuffixCommitmentDelta;

            Banderwagon internalCommitment = FillSpaceWithBranchNodes(sharedPath.ToArray(), relativePathLength, deltaCommitment);

            return (internalCommitment - node.InternalCommitment.Point, true);
        }

        byte[] absolutePath = traverseContext.Stem[..traverseContext.CurrentIndex].ToArray();
        byte childIndex = traverseContext.Stem[traverseContext.CurrentIndex - 1];
        if (traverseContext.ForSync)
        {
            // 1. create new suffix node
            // 2. update the C1 or C2 - we already know the leafDelta - traverseContext.LeafUpdateDelta
            // 3. update ExtensionCommitment
            // 4. get the delta for commitment - ExtensionCommitment - 0;
            InternalNode stem = new (VerkleNodeType.StemNode, traverseContext.Stem.ToArray());
            FrE deltaFr = stem.UpdateCommitment(traverseContext.LeafUpdateDelta);
            FrE deltaHash = deltaFr + stem.InitCommitmentHash!.Value;

            // 1. Add internal.stem node
            // 2. return delta from ExtensionCommitment
            SetInternalNode(absolutePath, stem);
            return (Committer.ScalarMul(deltaHash, childIndex), false);
        }
        else
        {
            InternalNode updatedStemNode = node.Clone();
            FrE deltaFr = updatedStemNode.UpdateCommitment(traverseContext.LeafUpdateDelta);
            SetInternalNode(absolutePath, updatedStemNode);
            return (Committer.ScalarMul(deltaFr, childIndex), false);
        }
    }

    private Banderwagon FillSpaceWithBranchNodes(byte[] path, int length, Banderwagon deltaPoint)
    {
        for (int i = 0; i < length; i++)
        {
            InternalNode newInternalNode = new(VerkleNodeType.BranchNode);
            FrE upwardsDelta = newInternalNode.UpdateCommitment(deltaPoint);
            SetInternalNode(path[..^i], newInternalNode);
            deltaPoint = Committer.ScalarMul(upwardsDelta, path[path.Length - i - 1]);
        }

        return deltaPoint;
    }

    public ref struct TraverseContext
    {
        public LeafUpdateDelta LeafUpdateDelta { get; }
        public bool ForSync { get; set; }
        public Span<byte> Stem { get; }
        public int CurrentIndex { get; set; }

        public TraverseContext(Span<byte> stem, LeafUpdateDelta delta)
        {
            Stem = stem;
            CurrentIndex = 0;
            LeafUpdateDelta = delta;
        }
    }
}
