// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Blockchain.BlockHashInState;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.State;

namespace Nethermind.Blockchain;

public static class BlockHashInStateExtension
{
    public static void InitHistoryOnForkBlock(IBlockTree blockTree, BlockHeader currentBlock,
        IReleaseSpec spec, IWorldState stateProvider)
    {
        long current = currentBlock.Number;
        BlockHeader header = currentBlock;
        for (var i = 0; i < Math.Min(Eip2935Constants.RingBufferSize, current); i++)
        {
            // an extra check - don't think it is needed
            if (header.IsGenesis) break;
            BlockHashInStateHandler.AddParentBlockHashToState(header, spec, stateProvider);
            header = blockTree.FindParentHeader(currentBlock, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            if (header is null)
            {
                throw new InvalidDataException(
                    "Parent header cannot be found when initializing BlockHashInState history");
            }
        }
    }
}
