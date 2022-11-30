// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Baseline.Tree
{
    public interface IBaselineTreeHelper
    {
        BaselineTree RebuildEntireTree(Address treeAddress, Keccak blockHash);

        BaselineTree BuildTree(BaselineTree baselineTree, Address treeAddress, BlockParameter blockFrom, BlockParameter blockTo);

        BaselineTree CreateHistoricalTree(Address address, long blockNumber);

        BaselineTreeNode GetHistoricalLeaf(BaselineTree tree, uint leafIndex, long blockNumber);

        BaselineTreeNode[] GetHistoricalLeaves(BaselineTree tree, uint[] leafIndexes, long blockNumber);
    }
}
