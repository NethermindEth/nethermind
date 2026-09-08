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
    /// <remarks>
    /// Re-processes <paramref name="block"/> directly via <see cref="IBlockProcessor.ProcessOne"/>, so the caller
    /// must have recovered transaction senders first (this bypasses the pipeline's <c>RecoverSignatures</c> step).
    /// </remarks>
    public Witness GetWitnessForExistingBlock(BlockHeader parentHeader, Block block)
    {
        using IDisposable? scope = worldState.BeginScope(parentHeader);
        blockProcessor.ProcessOne(block, ProcessingOptions.ReadOnlyChain, NullBlockTracer.Instance, specProvider.GetSpec(block.Header));
        return worldState.GetWitness(parentHeader);
    }
}
