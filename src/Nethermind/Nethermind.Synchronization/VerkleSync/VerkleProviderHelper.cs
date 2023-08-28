// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.State;
using Nethermind.State.Snap;
using Nethermind.Verkle.Tree.Sync;

namespace Nethermind.Synchronization.VerkleSync;

public class VerkleProviderHelper
{
    public static (AddRangeResult result, bool moreChildrenToRight) AddSubTreeRange(
            VerkleStateTree tree,
            long blockNumber,
            byte[] expectedRootHash,
            byte[] startingHash,
            byte[] limitHash,
            PathWithSubTree[] subTrees,
            byte[][] proofs = null
        )
    {
        byte[] lastStem = subTrees[^1].Path.Bytes;

        foreach (PathWithSubTree? subTree in subTrees)
        {
            tree.InsertStemBatch(subTree.Path.Bytes, subTree.SubTree);
            tree.Commit();
        }

        if (tree.StateRoot.Bytes != expectedRootHash)
        {
            return (AddRangeResult.DifferentRootHash, true);
        }

        tree.CommitTree(blockNumber);

        return (AddRangeResult.OK, true);
    }


}
