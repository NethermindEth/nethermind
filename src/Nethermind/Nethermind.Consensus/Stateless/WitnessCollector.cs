// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus.Stateless;

public interface IExistingBlockWitnessCollector
{
    Witness GetWitnessForExistingBlock(BlockHeader parentHeader, Block block, CancellationToken token = default);

    /// <summary>As <see cref="GetWitnessForExistingBlock(BlockHeader, Block, CancellationToken)"/>, tracing the block's
    /// execution with <paramref name="tracer"/>.</summary>
    Witness GetWitnessForExistingBlock(BlockHeader parentHeader, Block block, IBlockTracer tracer, CancellationToken token = default);
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
    public Witness GetWitnessForExistingBlock(BlockHeader parentHeader, Block block, CancellationToken token = default) =>
        GetWitnessForExistingBlock(parentHeader, block, NullBlockTracer.Instance, token);

    /// <inheritdoc/>
    public Witness GetWitnessForExistingBlock(BlockHeader parentHeader, Block block, IBlockTracer tracer, CancellationToken token = default)
    {
        // The scope and the witness must share one parent: the witness walks the pre-state at parentHeader's root.
        using IDisposable _ = worldState.BeginScope(parentHeader);
        blockProcessor.ProcessOne(block, ProcessingOptions.ReadOnlyChain, tracer, specProvider.GetSpec(block.Header), token);
        return worldState.GetWitness(parentHeader);
    }
}
