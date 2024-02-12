// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Verkle;

namespace Nethermind.Verkle.Tree.TreeNodes;

public static class VerkleNodes
{
    public static InternalNode CreateStatelessBranchNode(Commitment commitment)
    {
        return new InternalNode(VerkleNodeType.BranchNode, commitment) { IsStateless = true };
    }

    public static InternalNode CreateBranchNode(Commitment commitment)
    {
        return new InternalNode(VerkleNodeType.BranchNode, commitment);
    }

    public static InternalNode CreateStatelessStemNode(Stem stem, Commitment internalCommitment)
    {
        return new InternalNode(VerkleNodeType.StemNode, stem, null, null, internalCommitment) { IsStateless = true };
    }

    public static InternalNode CreateStatelessStemNode(Stem stem, Commitment? c1, Commitment? c2,
        Commitment internalCommitment, bool isStateless)
    {
        return new InternalNode(VerkleNodeType.StemNode, stem, c1, c2, internalCommitment) { IsStateless = isStateless };
    }

    public static InternalNode CreateStatelessStemNode(Stem stem)
    {
        return new InternalNode(VerkleNodeType.StemNode, stem) { IsStateless = true };
    }
}
