// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Blockchain;

/// <summary>
/// Just call exit if blocktree report added block is more than a specific number.
/// </summary>
public class ExitOnBlockNumberHandler
{
    public ExitOnBlockNumberHandler(IBlockTree blockTree, IProcessExitSource processExitSource, long? initConfigExitOnBlockNumber)
    {
        blockTree.BlockAddedToMain += (sender, args) =>
        {
            if (args.Block.Number >= initConfigExitOnBlockNumber)
            {
                processExitSource.Exit(0);
            }
        };
    }
}
