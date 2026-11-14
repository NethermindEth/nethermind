// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Messages;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;

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
    ISpecProvider specProvider,
    IPoSSwitcher poSSwitcher,
    ILogManager logManager) : IExistingBlockWitnessCollector
{
    private readonly ILogger _logger = logManager.GetClassLogger<WitnessCollector>();

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
        // shared instance, so execute a copy. Storage does not persist the post-merge flag and only some callers
        // restore it; without it PREVRANDAO evaluates to the difficulty. A plain Block, as WithReplacedHeader on a
        // BlockToProduce drops the access list.
        BlockHeader header = block.Header.Clone();
        header.IsPostMerge = poSSwitcher.IsPostMerge(header);
        Block isolated = new(header, block.Body, block.BlockAccessList);
        try
        {
            // NoValidation is required, not an optimisation: the shared validator is the merge plugin's
            // InvalidBlockInterceptor, which records into the process-wide InvalidChainTracker, so a replay that
            // diverges on this node would make the Engine API reject the block and its descendants. The header is
            // compared with the execution outcome below instead.
            (Block processed, TxReceipt[] receipts) = blockProcessor.ProcessOne(isolated,
                ProcessingOptions.ReadOnlyChain | ProcessingOptions.NoValidation, tracer, specProvider.GetSpec(header), token);
            ThrowIfNotReproduced(block, processed, receipts);

            // Trusted inputs serialize the block's access list, which import may have released after storing it.
            block.EncodedBlockAccessList ??= isolated.EncodedBlockAccessList;
        }
        finally
        {
            isolated.DisposeAccountChanges();
        }

        return worldState.GetWitness(parentHeader);
    }

    /// <remarks>The header hash commits to every execution outcome the block validator compares.</remarks>
    private void ThrowIfNotReproduced(Block block, Block processed, TxReceipt[] receipts)
    {
        Hash256 expected = block.Hash ?? block.Header.CalculateHash();
        if (processed.Hash == expected) return;

        string error = BlockValidator.DescribeMismatch(processed, receipts, block, _logger)
            ?? BlockErrorMessages.InvalidHeaderHash(expected, processed.Hash!);
        throw new InvalidBlockException(block, error);
    }
}
