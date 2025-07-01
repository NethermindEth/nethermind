// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;

namespace Nethermind.Facade.Simulate;

public sealed class SimulateBlockhashProvider(IBlockhashProvider blockhashProvider, IBlockTree blockTree)
    : IBlockhashProvider
{
    public Hash256? GetBlockhash(BlockHeader currentBlock, long number, IReleaseSpec? spec)
    {
        long bestKnown = blockTree.BestKnownNumber;
        return bestKnown < number && blockTree.BestSuggestedHeader is not null
            ? blockhashProvider.GetBlockhash(blockTree.BestSuggestedHeader!, bestKnown, spec)
            : blockhashProvider.GetBlockhash(currentBlock, number, spec);
    }

    public Hash256? GetBlockhash(BlockHeader currentBlock, long number)
    {
        long bestKnown = blockTree.BestKnownNumber;
        return bestKnown < number && blockTree.BestSuggestedHeader is not null
            ? blockhashProvider.GetBlockhash(blockTree.BestSuggestedHeader!, bestKnown)
            : blockhashProvider.GetBlockhash(currentBlock, number);
    }
}
