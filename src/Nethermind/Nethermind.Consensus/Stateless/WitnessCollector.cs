// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus.Stateless;

public interface IExistingBlockWitnessCollector
{
    Witness GetWitnessForExistingBlock(BlockHeader parentHeader, Block block);
}

public class WitnessCollector(
    WitnessGeneratingWorldState worldState,
    IBlockProcessor blockProcessor,
    ISpecProvider specProvider) : IExistingBlockWitnessCollector
{
    public Witness GetWitnessForExistingBlock(BlockHeader parentHeader, Block block)
    {
        if (!worldState.TryBeginScope(parentHeader, out IDisposable? scopeCloser))
        {
            throw new InvalidOperationException(
                $"Witness collector: missing state for parent {parentHeader.ToString(BlockHeader.Format.Short)}.");
        }

        using IDisposable scope = scopeCloser;
        blockProcessor.ProcessOne(block, ProcessingOptions.ReadOnlyChain, NullBlockTracer.Instance, specProvider.GetSpec(block.Header));
        return worldState.GetWitness(parentHeader);
    }
}
