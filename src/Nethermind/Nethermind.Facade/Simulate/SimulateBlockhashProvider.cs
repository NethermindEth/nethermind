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
    private ILogger _logger = logManager.GetClassLogger();

    public Hash256? GetBlockhash(BlockHeader currentBlock, long number, IReleaseSpec spec)
    {
        long bestKnown = blockTree.BestKnownNumber;
        _logger.Error($"Call: {blockTree.GetType().FullName}. current: {currentBlock.Number}, number: {number}, bestKnown: {bestKnown}");
        return bestKnown < number && blockTree.BestSuggestedHeader is not null
            ? blockhashProvider.GetBlockhash(blockTree.BestSuggestedHeader!, number, spec)
            : blockhashProvider.GetBlockhash(currentBlock, number, spec);
    }

    public Task Prefetch(BlockHeader currentBlock, CancellationToken token) => blockhashProvider.Prefetch(currentBlock, token);
}
