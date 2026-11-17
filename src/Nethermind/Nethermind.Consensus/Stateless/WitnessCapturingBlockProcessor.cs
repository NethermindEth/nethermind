// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Headers;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// <see cref="IBlockProcessor"/> decorator that, when a witness has been requested for the block being
/// processed, routes that single <see cref="ProcessOne"/> to the witness-wired processor
/// (<see cref="WitnessCapturingBlockProcessingEnv"/>), then publishes the resulting <see cref="Witness"/>
/// via <see cref="WitnessRendezvous"/>. Every other block flows straight through to the inner processor.
/// </summary>
/// <remarks>
/// The witness processor shares the main pipeline's writable world state, so the witnessed block is
/// really imported, not re-executed.
/// </remarks>
public sealed class WitnessCapturingBlockProcessor : IBlockProcessor
{
    private readonly IBlockProcessor _inner;
    private readonly WitnessCapturingBlockProcessingEnv _witness;
    private readonly WitnessRendezvous _rendezvous;
    private readonly IHeaderFinder _headerFinder;
    private readonly ILogger _logger;
    private Action<Block>? _transactionsExecuted;

    /// <param name="graphCore">Given to the selector built inside the witness graph, which hands <paramref name="inner"/> to
    /// it and is never run itself; see <see cref="WitnessCapturingBlockProcessingEnv"/>.</param>
    public WitnessCapturingBlockProcessor(
        IBlockProcessor inner,
        WitnessCapturingBlockProcessingEnv witness,
        WitnessRendezvous rendezvous,
        IHeaderFinder headerFinder,
        ILogManager? logManager = null,
        WitnessGraphCore? graphCore = null)
    {
        _inner = inner;
        _witness = witness;
        _rendezvous = rendezvous;
        _headerFinder = headerFinder;
        _logger = (logManager ?? LimboLogs.Instance).GetClassLogger<WitnessCapturingBlockProcessor>();
        if (graphCore is not null) graphCore.Processor = inner;
    }

    /// <remarks>Forwarded to the inner processor, and to the witness processor for the block it runs.</remarks>
    public event Action<Block>? TransactionsExecuted
    {
        add { _inner.TransactionsExecuted += value; _transactionsExecuted += value; }
        remove { _inner.TransactionsExecuted -= value; _transactionsExecuted -= value; }
    }

    public (Block Block, TxReceipt[] Receipts) ProcessOne(
        Block suggestedBlock,
        ProcessingOptions options,
        IBlockTracer blockTracer,
        IReleaseSpec spec,
        CancellationToken token = default)
    {
        Hash256? blockHash = suggestedBlock.Hash;
        Hash256? parentHash = suggestedBlock.ParentHash;

        bool shouldCapture =
            blockHash is not null
            && parentHash is not null
            && !options.ContainsFlag(ProcessingOptions.ReadOnlyChain)
            && _rendezvous.HasPendingRequest(blockHash);

        if (!shouldCapture)
            return _inner.ProcessOne(suggestedBlock, options, blockTracer, spec, token);

        ulong parentBlockNumber = suggestedBlock.Number - 1;
        BlockHeader parent = _headerFinder.Get(parentHash, parentBlockNumber)
            ?? throw new ArgumentException($"Unable to find parent for block {parentBlockNumber} with hash {parentHash}");

        _witness.ResetForBlock();

        // If ProcessOne throws, the handler's using-registration cancels the pending slot — nothing to do here.
        (Block Block, TxReceipt[] Receipts) result = ProcessWithWitness(suggestedBlock, options, blockTracer, spec, token);

        if (!_rendezvous.TryClaim(blockHash!, out TaskCompletionSource<Witness?>? tcs))
            return result; // request was declined or cancelled — nothing to publish.

        // Witness building is best-effort: the block is already processed and validated, so swallow any failure
        // and let the block stand rather than fail an otherwise-valid block. The handler re-executes the block
        // to rebuild a witness it did not receive.
        Witness? capturedWitness = null;
        try
        {
            capturedWitness = _witness.GetWitness(parent);
        }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error($"{nameof(WitnessCapturingBlockProcessor)}: witness build failed for block {blockHash}", ex);
        }
        tcs!.SetResult(capturedWitness);
        return result;
    }

    /// <remarks>The witness processor is built on the first witnessed block, so the handlers reach it here rather than as
    /// they are added.</remarks>
    private (Block Block, TxReceipt[] Receipts) ProcessWithWitness(Block block, ProcessingOptions options, IBlockTracer tracer,
        IReleaseSpec spec, CancellationToken token)
    {
        IBlockProcessor processor = _witness.Processor;
        Action<Block>? handlers = _transactionsExecuted;
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
}
