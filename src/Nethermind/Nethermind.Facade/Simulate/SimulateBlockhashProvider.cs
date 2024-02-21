// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Facade.Simulate;

public class SimulateBlockhashProvider : BlockhashProvider
{
    public SimulateBlockhashProvider(IBlockTree blockTree, ILogManager? logManager) : base(blockTree, logManager)
    {
    }

    public override Hash256 GetBlockhash(BlockHeader currentBlock, in long number)
    {
        var bestKnown = BlockTree.BestKnownNumber;
        if (bestKnown < number && BlockTree.BestSuggestedHeader != null)
        {
            return base.GetBlockhash(BlockTree.BestSuggestedHeader!, in bestKnown);
        }

        return base.GetBlockhash(currentBlock, in number);
    }
}
