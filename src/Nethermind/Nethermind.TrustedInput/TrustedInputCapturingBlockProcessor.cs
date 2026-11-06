// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Headers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;

[assembly: InternalsVisibleTo("Nethermind.TrustedInput.Test")]

namespace Nethermind.TrustedInput;

/// <summary>
/// <see cref="IBlockProcessor"/> decorator that imports every block the main pipeline processes through the
/// witness-recording processor of <see cref="WitnessCapturingBlockProcessingEnv"/>, then stores the block and its witness
/// as a trusted input.
/// </summary>
/// <remarks>
/// The witness processor shares the main pipeline's writable world state, so a block is imported once, not re-executed.
/// Read-only processing, such as tracing and simulation, passes straight through. Capturing is best-effort: a block whose
/// input fails to build still stands, and <c>trustedinput_getInput</c> rebuilds it on request.
/// At most one capture runs in the background; blocks imported while it is busy are regenerated on demand.
/// </remarks>
public sealed class TrustedInputCapturingBlockProcessor(
    IBlockProcessor inner,
    WitnessCapturingBlockProcessingEnv witness,
    TrustedInputStore store,
    IHeaderFinder headerFinder,
    ISpecProvider specProvider,
    ILogManager logManager) : IBlockProcessor, IAsyncDisposable
{
    private readonly ILogger _logger = logManager.GetClassLogger<TrustedInputCapturingBlockProcessor>();
    private readonly Lock _captureLock = new();
    private Task _capture = Task.CompletedTask;
    private bool _stopping;

    /// <inheritdoc/>
    public event Action<Block>? TransactionsExecuted;

    /// <inheritdoc/>
    public (Block Block, TxReceipt[] Receipts) ProcessOne(
        Block suggestedBlock,
        ProcessingOptions options,
        IBlockTracer blockTracer,
        IReleaseSpec spec,
        CancellationToken token = default)
    {
        if (suggestedBlock.Hash is null || suggestedBlock.ParentHash is null || suggestedBlock.Number == 0
            || options.ContainsFlag(ProcessingOptions.ReadOnlyChain))
        {
            return Process(inner, suggestedBlock, options, blockTracer, spec, token);
        }

        BlockHeader parent = headerFinder.Get(suggestedBlock.ParentHash, suggestedBlock.Number - 1)
            ?? throw new ArgumentException($"Unable to find the parent of block {suggestedBlock.Number}");

        witness.ResetForBlock();
        (Block Block, TxReceipt[] Receipts) result = Process(witness.Processor, suggestedBlock, options, blockTracer, spec, token);

        try
        {
            Witness blockWitness = witness.GetWitness(parent);
            if (!TryScheduleCapture(() => Capture(suggestedBlock, blockWitness))) blockWitness.Dispose();
        }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error($"Could not capture the trusted input of block {suggestedBlock.ToString(Block.Format.Short)}", ex);
        }

        return result;
    }

    private (Block Block, TxReceipt[] Receipts) Process(IBlockProcessor processor, Block block, ProcessingOptions options,
        IBlockTracer tracer, IReleaseSpec spec, CancellationToken token)
    {
        Action<Block>? handlers = TransactionsExecuted;
        processor.TransactionsExecuted += handlers;
        try
        {
            return processor.ProcessOne(block, options, tracer, spec, token);
        }
        finally
        {
            processor.TransactionsExecuted -= handlers;
        }
    }

    internal bool TryScheduleCapture(Action capture)
    {
        lock (_captureLock)
        {
            if (_stopping || !_capture.IsCompleted) return false;
            _capture = Task.Run(capture);
            return true;
        }
    }

    /// <summary>Stops accepting captures and waits for the running capture to release its witness.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_captureLock)
        {
            _stopping = true;
            return new ValueTask(_capture);
        }
    }

    private void Capture(Block block, Witness blockWitness)
    {
        using (blockWitness)
        {
            try
            {
                byte[] input = Stateless.Execution.IO.TrustedInput.Write(specProvider.ChainId, block, blockWitness);
                store.Add(block.Number, block.Hash!, input);
            }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error($"Could not capture the trusted input of block {block.ToString(Block.Format.Short)}", ex);
            }
        }
    }
}
