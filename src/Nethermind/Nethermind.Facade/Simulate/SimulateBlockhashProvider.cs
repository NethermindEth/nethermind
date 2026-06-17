// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;

namespace Nethermind.Facade.Simulate;

public sealed class SimulateBlockhashProvider(IBlockhashProvider blockhashProvider, IBlockTree blockTree)
    : IBlockhashProvider
{
    public Hash256? GetBlockhash(BlockHeader currentBlock, long number, IReleaseSpec spec)
    {
        long bestKnown = blockTree.BestKnownNumber;
        try
        {
            return bestKnown < number && blockTree.BestSuggestedHeader is not null
                ? blockhashProvider.GetBlockhash(blockTree.BestSuggestedHeader!, bestKnown, spec)
                : blockhashProvider.GetBlockhash(currentBlock, number, spec);
        }
        catch (InvalidDataException)
        {
            // eth_simulateV1 is best-effort: when an ancestor block hash cannot be
            // resolved in the simulate context, return 0 (the EVM BLOCKHASH
            // out-of-window result) instead of failing the whole request. The
            // underlying provider throws here only on the simulate path; canonical
            // block processing always has the hash available.
            return null;
        }
    }

    public Task Prefetch(BlockHeader currentBlock, CancellationToken token) => blockhashProvider.Prefetch(currentBlock, token);
}
