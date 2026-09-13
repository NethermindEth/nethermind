// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;

namespace Nethermind.Facade.Simulate;

public sealed class SimulateBlockhashProvider(
    IBlockhashProvider blockhashProvider,
    IBlockTree blockTree,
    IBlockhashStore blockhashStore)
    : IBlockhashProvider
{
    public Hash256? GetBlockhash(BlockHeader currentBlock, ulong number, IReleaseSpec spec)
    {
        ulong bestKnown = blockTree.BestKnownNumber;
        return bestKnown < number && blockTree.BestSuggestedHeader is not null
            ? blockhashProvider.GetBlockhash(blockTree.BestSuggestedHeader!, bestKnown, spec)
            : blockhashProvider.GetBlockhash(currentBlock, number, spec);
    }

    /// <inheritdoc/>
    /// <remarks>The EIP-7709 path reads the store directly rather than the inner provider: simulate
    /// collapses distinct virtual blocks onto one header while state overrides can rewrite the history
    /// contract between them, so (header, number) does not identify the bytes here and the inner
    /// provider's memo must not be populated or consulted. Bypassing it costs one <see cref="Hash256"/>
    /// per call, which simulate can afford — do not replace it with a shared scratch buffer, the span
    /// must stay valid after the call returns.</remarks>
    public bool TryGetBlockhash(BlockHeader currentBlock, ulong number, IReleaseSpec spec, out ReadOnlySpan<byte> hash)
    {
        ulong bestKnown = blockTree.BestKnownNumber;
        (BlockHeader header, ulong target) = bestKnown < number && blockTree.BestSuggestedHeader is not null
            ? (blockTree.BestSuggestedHeader!, bestKnown)
            : (currentBlock, number);

        if (spec.IsBlockHashInStateAvailable)
        {
            Hash256? fromState = blockhashStore.GetBlockHashFromState(header, target, spec);
            hash = fromState is null ? default : fromState.Bytes;
            return fromState is not null;
        }

        return blockhashProvider.TryGetBlockhash(header, target, spec, out hash);
    }

    public Task Prefetch(BlockHeader currentBlock, CancellationToken token) => blockhashProvider.Prefetch(currentBlock, token);
}
