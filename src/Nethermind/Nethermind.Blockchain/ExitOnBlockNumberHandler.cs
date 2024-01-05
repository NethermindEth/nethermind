// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Logging;

namespace Nethermind.Blockchain;

/// <summary>
/// Just call exit if blocktree report added block is more than a specific number.
/// </summary>
public class ExitOnBlockNumberHandler
{
    public ExitOnBlockNumberHandler(
        IBlockTree blockTree,
        IProcessExitSource processExitSource,
        long initConfigExitOnBlockNumber,
        ILogManager logManager)
    {
        ILogger logger = logManager.GetClassLogger();

        blockTree.BlockAddedToMain += (sender, args) =>
        {
            if (args.Block.Number >= initConfigExitOnBlockNumber)
            {
                logger.Info($"Block {args.Block.Number} reached. Exiting.");
                processExitSource.Exit(0);
            }
        };
    }
}
