// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Messages;
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
    /// <exception cref="InvalidBlockException">Re-executing the block does not reproduce its header.</exception>
    public Witness GetWitnessForExistingBlock(BlockHeader parentHeader, Block block, IBlockTracer tracer, CancellationToken token = default)
    {
        // The scope and the witness must share one parent: the witness walks the pre-state at parentHeader's root.
        using IDisposable scope = worldState.BeginScope(parentHeader);

        // Processing writes execution artifacts back onto the block it is given, and callers pass the block tree's
        // shared instance, so execute a copy.
        Block isolated = block.WithReplacedHeader(block.Header.Clone());
        try
        {
            // NoValidation is required, not an optimisation: the shared validator is the merge plugin's
            // InvalidBlockInterceptor, which records into the process-wide InvalidChainTracker, so a replay that
            // diverges on this node would make the Engine API reject the block and its descendants. The header is
            // compared with the execution outcome below instead.
            (Block processed, _) = blockProcessor.ProcessOne(isolated, ProcessingOptions.ReadOnlyChain | ProcessingOptions.NoValidation,
                tracer, specProvider.GetSpec(block.Header), token);
            ThrowIfNotReproduced(block, processed);
        }
        finally
        {
            isolated.DisposeAccountChanges();
        }

        return worldState.GetWitness(parentHeader);
    }

    /// <remarks>The header hash commits to every execution outcome the block validator compares.</remarks>
    private static void ThrowIfNotReproduced(Block block, Block processed)
    {
        if (processed.Hash == block.Hash) return;

        string error = processed.Header.StateRoot != block.Header.StateRoot
            ? BlockErrorMessages.InvalidStateRoot(block.Header.StateRoot!, processed.Header.StateRoot!)
            : BlockErrorMessages.InvalidHeaderHash(block.Hash!, processed.Hash!);
        throw new InvalidBlockException(block, error);
    }
}
