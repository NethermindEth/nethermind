// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Stateless.Execution;

[assembly: InternalsVisibleTo("Nethermind.TrustedInput.Test")]

namespace Nethermind.TrustedInput;

/// <summary>
/// Requests a witness from the main processing pipeline and stores it as a trusted input in the background.
/// </summary>
/// <remarks>
/// Read-only processing, such as tracing and simulation, passes straight through. Capturing is best-effort: a block whose
/// input fails to build still stands, and <c>trustedinput_getInput</c> rebuilds it on request.
/// At most one capture runs in the background; blocks imported while it is busy or another consumer owns the witness
/// request are regenerated on demand.
/// While a block is processed here, its witness request holds the single slot of <see cref="WitnessRendezvous"/>, so an
/// <c>engine_newPayloadWithWitness</c> request for another block is declined and its handler executes that block again
/// to build the witness. The block is processed under the lock <see cref="DisposeAsync"/> takes, so disposal can wait
/// for one block.
/// </remarks>
public sealed class TrustedInputCapturingBlockProcessor(
    IBlockProcessor inner,
    WitnessRendezvous rendezvous,
    TrustedInputStore store,
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
            || options.ContainsFlag(ProcessingOptions.ReadOnlyChain)
            || rendezvous.HasPendingRequest(suggestedBlock.Hash))
        {
            return Process(inner, suggestedBlock, options, blockTracer, spec, token);
        }

        (Block Block, TxReceipt[] Receipts) result = default;
        bool scheduled = TryScheduleCapture(() =>
        {
            using WitnessRequest request = rendezvous.RequestWitness(suggestedBlock.Hash);
            StatelessExecutor.EcRecoverInputCollector collector = new();
            CompositeBlockTracer tracer = new();
            tracer.Add(blockTracer);
            tracer.Add(collector);
            try
            {
                result = Process(inner, suggestedBlock, options, tracer, spec, token);
            }
            catch
            {
                if (request.Task.IsCompletedSuccessfully) request.Task.Result?.Dispose();
                throw;
            }
            Witness? captured = request.Task.IsCompletedSuccessfully ? request.Task.Result : null;
            if (captured is null) return null;

            // The import frees the block's access list (BlockAccessListStore) while the capture still reads it.
            Block block = suggestedBlock.WithReplacedBody(suggestedBlock.Body);
            block.EncodedBlockAccessList = suggestedBlock.EncodedBlockAccessList;
            return () => Capture(block, captured, collector.Inputs);
        });

        return scheduled ? result : Process(inner, suggestedBlock, options, blockTracer, spec, token);
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

    internal bool TryScheduleCapture(Func<Action?> createCapture)
    {
        lock (_captureLock)
        {
            if (_stopping || !_capture.IsCompleted) return false;
            Action? capture = createCapture();
            if (capture is not null) _capture = Task.Run(capture);
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

    private void Capture(Block block, Witness blockWitness, IReadOnlyList<byte[]> precompileInputs)
    {
        using (blockWitness)
        {
            try
            {
                byte[] input = Stateless.Execution.IO.TrustedInput.Write(specProvider.ChainId, block, blockWitness, precompileInputs);
                store.Add(block.Number, block.Hash!, input);
            }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error($"Could not capture the trusted input of block {block.ToString(Block.Format.Short)}", ex);
            }
        }
    }
}
