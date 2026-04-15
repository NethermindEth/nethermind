// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Trie;

namespace Nethermind.StateComposition.Diff;

internal sealed partial class TrieDiffWalker
{
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
            int children = CountBranchChildren(branch);
            if (children <= 0) return;
            _depthDelta.BranchOccupancy[children - 1] += sign;
            _depthDelta.TotalBranchNodes += sign;
            _depthDelta.TotalBranchChildren += sign * children;
        }
    }

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
