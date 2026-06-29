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
/// <see cref="IBlockProcessor"/> decorator on the main pipeline that, when a witness has been requested
/// for the block being processed, routes that single <see cref="ProcessOne"/> to the dedicated
/// witness-wired processor (<see cref="WitnessCapturingBlockProcessingEnv"/>) instead of the main inner
/// processor, then projects the recorded accesses into a <see cref="Witness"/> and publishes it via
/// <see cref="WitnessRendezvous"/>. Every other block flows straight through to the inner processor.
/// </summary>
/// <remarks>
/// The witness processor shares the main pipeline's writable world state (through a transparent
/// recorder), so the witnessed block is really imported — it is not re-executed. Selection happens once
/// per block at this single point; the witness processor's world state, code repository and block-access
/// -list manager are statically witness-configured, so there are no per-call predicates anywhere below.
/// </remarks>
public sealed class WitnessCapturingBlockProcessor(
    IBlockProcessor inner,
    WitnessCapturingBlockProcessingEnv witness,
    WitnessRendezvous rendezvous,
    IHeaderFinder headerFinder,
    ILogManager? logManager = null) : IBlockProcessor
{
    private readonly ILogger _logger = (logManager ?? LimboLogs.Instance).GetClassLogger<WitnessCapturingBlockProcessor>();

    public event Action? TransactionsExecuted
    {
        add => inner.TransactionsExecuted += value;
        remove => inner.TransactionsExecuted -= value;
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
            && rendezvous.HasPendingRequest(blockHash);

        if (!shouldCapture)
            return inner.ProcessOne(suggestedBlock, options, blockTracer, spec, token);

        long parentBlockNumber = suggestedBlock.Number - 1;
        BlockHeader parent = headerFinder.Get(parentHash, parentBlockNumber)
            ?? throw new ArgumentException($"Unable to find parent for block {parentBlockNumber} with hash {parentHash}");

        witness.ResetForBlock();

        // If ProcessOne throws, the request slot is left pending; the handler's using-registration
        // cancels it, so there is no cleanup to do here.
        (Block Block, TxReceipt[] Receipts) result = witness.Processor.ProcessOne(suggestedBlock, options, blockTracer, spec, token);

        if (!rendezvous.TryClaim(blockHash!, out TaskCompletionSource<Witness?>? tcs))
            return result; // request was declined or cancelled — nothing to publish.

        Witness? capturedWitness = null;
        try
        {
            capturedWitness = witness.GetWitness(parent);
        }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error($"{nameof(WitnessCapturingBlockProcessor)}: witness build failed for block {blockHash}", ex);
        }
        tcs!.SetResult(capturedWitness);
        return result;
    }
}
