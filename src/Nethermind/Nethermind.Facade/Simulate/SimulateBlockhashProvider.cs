// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Logging;

namespace Nethermind.Facade.Simulate;

public sealed class SimulateBlockhashProvider(IBlockhashProvider blockhashProvider, IBlockTree blockTree)
    : IBlockhashProvider
{
    public Hash256? GetBlockHash(BlockHeader currentBlock, in long number)
    {
        var bestKnown = blockTree.BestKnownNumber;
        return bestKnown < number && blockTree.BestSuggestedHeader is not null
            ? blockhashProvider.GetBlockHash(blockTree.BestSuggestedHeader!, in bestKnown)
            : blockhashProvider.GetBlockHash(currentBlock, in number);
    }
}
