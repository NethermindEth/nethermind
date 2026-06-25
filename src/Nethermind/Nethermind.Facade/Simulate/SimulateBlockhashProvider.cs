// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    // eth_simulateV1 is best-effort: when the ancestor hash cannot be resolved the inner provider returns
    // null, which the EVM turns into a 0 push per BLOCKHASH semantics rather than failing the whole request.
    public Hash256? GetBlockhash(BlockHeader currentBlock, long number, IReleaseSpec spec)
    {
        long bestKnown = blockTree.BestKnownNumber;
        return bestKnown < number && blockTree.BestSuggestedHeader is not null
            ? blockhashProvider.GetBlockhash(blockTree.BestSuggestedHeader!, bestKnown, spec)
            : blockhashProvider.GetBlockhash(currentBlock, number, spec);
    }

    public Task Prefetch(BlockHeader currentBlock, CancellationToken token) => blockhashProvider.Prefetch(currentBlock, token);
}
