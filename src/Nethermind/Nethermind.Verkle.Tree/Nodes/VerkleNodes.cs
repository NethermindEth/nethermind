// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Verkle;
using Nethermind.Verkle.Tree.Utils;

namespace Nethermind.Verkle.Tree.Nodes;

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
        return new(VerkleNodeType.StemNode, stem, null, null, internalCommitment) { IsStateless = true };
    }

    public static InternalNode CreateStatelessStemNode(Stem stem, Commitment? c1, Commitment? c2, Commitment internalCommitment)
    {
        return new(VerkleNodeType.StemNode, stem, c1, c2, internalCommitment) { IsStateless = true };
    }

    public static InternalNode CreateStatelessStemNode(Stem stem)
    {
        return new(VerkleNodeType.StemNode, stem) { IsStateless = true };
    }
}
