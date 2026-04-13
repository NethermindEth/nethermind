// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Trie;

namespace Nethermind.StateComposition.Diff;

internal sealed partial class TrieDiffWalker
{
    /// <summary>Record a branch node add/remove in the depth delta arrays.</summary>
    private void RecordDepthBranch(TrieNode branch, int depth, bool isStorage, bool added)
    {
        int sign = added ? 1 : -1;
        long bytes = branch.FullRlp.Length;
        if (isStorage)
        {
            _depthDelta.StorageFullNodes[depth] += sign;
            _depthDelta.StorageNodeBytes[depth] += sign * bytes;
        }
        else
        {
            _depthDelta.AccountFullNodes[depth] += sign;
            _depthDelta.AccountNodeBytes[depth] += sign * bytes;
            // Branch occupancy histogram
            int children = CountBranchChildren(branch);
            if (children > 0)
            {
                _depthDelta.BranchOccupancy[children - 1] += sign;
                _depthDelta.TotalBranchNodesDelta += sign;
                _depthDelta.TotalBranchChildrenDelta += sign * children;
            }
        }
    }

    /// <summary>Record an extension node add/remove in the depth delta arrays.</summary>
    private void RecordDepthShort(long rlpLen, int depth, bool isStorage, bool added)
    {
        int sign = added ? 1 : -1;
        if (isStorage)
        {
            _depthDelta.StorageShortNodes[depth] += sign;
            _depthDelta.StorageNodeBytes[depth] += sign * rlpLen;
        }
        else
        {
            _depthDelta.AccountShortNodes[depth] += sign;
            _depthDelta.AccountNodeBytes[depth] += sign * rlpLen;
        }
    }

    /// <summary>
    /// Record a leaf node add/remove in the depth delta arrays.
    /// Both ShortNodes (Geth convention: leaf is a shortNode) and ValueNodes are updated.
    /// ValueNodes[depth] stores physical leaves (unshifted); the +1 shift is applied at metrics time.
    /// </summary>
    private void RecordDepthLeaf(long rlpLen, int depth, bool isStorage, bool added)
    {
        int sign = added ? 1 : -1;
        if (isStorage)
        {
            _depthDelta.StorageShortNodes[depth] += sign;
            _depthDelta.StorageValueNodes[depth] += sign;
            _depthDelta.StorageNodeBytes[depth] += sign * rlpLen;
        }
        else
        {
            _depthDelta.AccountShortNodes[depth] += sign;
            _depthDelta.AccountValueNodes[depth] += sign;
            _depthDelta.AccountNodeBytes[depth] += sign * rlpLen;
        }
    }
}
