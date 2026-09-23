// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;

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
        // The scope and the witness must share one parent: the witness walks the pre-state at parentHeader's root.
        using IDisposable _ = worldState.BeginScope(parentHeader);
        blockProcessor.ProcessOne(block, ProcessingOptions.ReadOnlyChain, NullBlockTracer.Instance, specProvider.GetSpec(block.Header));
        return worldState.GetWitness(parentHeader);
    }
}
