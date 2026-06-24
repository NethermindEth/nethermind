// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Logging;

namespace Nethermind.Facade.Simulate;

public sealed class SimulateBlockhashProvider(IBlockhashProvider blockhashProvider, IBlockTree blockTree, ILogManager logManager)
    : IBlockhashProvider
{
    private readonly ILogger _logger = logManager.GetClassLogger<SimulateBlockhashProvider>();

    public Hash256? GetBlockhash(BlockHeader currentBlock, long number, IReleaseSpec spec)
    {
        long bestKnown = blockTree.BestKnownNumber;
        (BlockHeader header, long target) = bestKnown < number && blockTree.BestSuggestedHeader is not null
            ? (blockTree.BestSuggestedHeader!, bestKnown)
            : (currentBlock, number);

        // eth_simulateV1 is best-effort: an unresolvable ancestor hash must push 0 (null) per EVM semantics
        // rather than fail the whole request, so we rely on the non-throwing resolution path.
        if (!blockhashProvider.TryGetBlockhash(header, target, spec, out Hash256? hash) && _logger.IsTrace)
        {
            _logger.Trace($"BLOCKHASH for {header.Number} -> {target} unresolvable in simulate context; returning 0");
        }

        return hash;
    }

    public Task Prefetch(BlockHeader currentBlock, CancellationToken token) => blockhashProvider.Prefetch(currentBlock, token);
}
