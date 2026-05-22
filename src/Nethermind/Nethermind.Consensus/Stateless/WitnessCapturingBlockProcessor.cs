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
using Nethermind.State;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// <see cref="IBlockProcessor"/> decorator that, when a witness has been requested for the block
/// being processed, installs a fresh <see cref="WitnessGeneratingWorldState"/> recorder onto the
/// main-pipeline <see cref="WitnessCapturingWorldStateProxy"/> for the duration of a single
/// <see cref="ProcessOne"/> call, then projects the recorded set into a <see cref="Witness"/> and
/// publishes it via <see cref="WitnessRendezvous"/>.
/// </summary>
/// <remarks>
/// All capture state lives on the per-call recorder instance — there is no global armed/disarmed
/// flag, no shared mutable dictionaries, and no nested-arming guard beyond the proxy's atomic
/// activate/deactivate. Blocks with no pending request bypass the recorder entirely.
/// </remarks>
public sealed class WitnessCapturingBlockProcessor(
    IBlockProcessor inner,
    WitnessCapturingWorldStateProxy proxy,
    WitnessRendezvous rendezvous,
    IStateReader stateReader,
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

        // Snapshot the parent state root *before* ProcessOne mutates the inner world state.
        Hash256 parentStateRoot = proxy.InnerState.StateRoot;
        long parentBlockNumber = suggestedBlock.Number - 1;

        WitnessGeneratingHeaderFinder perBlockHeaderFinder = new(headerFinder);
        WitnessGeneratingWorldState recorder = new(proxy.InnerState, stateReader, perBlockHeaderFinder);

        if (!proxy.TryActivate(recorder))
        {
            // Another capture is in progress for some other block on this proxy. Skip capture for
            // this one rather than risking interleaved recording.
            if (_logger.IsWarn) _logger.Warn($"{nameof(WitnessCapturingBlockProcessor)}: proxy already active when processing {blockHash}; skipping capture.");
            return inner.ProcessOne(suggestedBlock, options, blockTracer, spec, token);
        }

        try
        {
            (Block Block, TxReceipt[] Receipts) result = inner.ProcessOne(suggestedBlock, options, blockTracer, spec, token);

            if (!rendezvous.TryClaim(blockHash!, out TaskCompletionSource<Witness?>? tcs))
                return result; // request was cancelled while we were processing — nothing to publish.

            Witness? witness = null;
            try
            {
                // Minimal stub header: WitnessProofCollector only needs StateRoot + Number; the parent
                // hash is supplied separately so the headers section resolves correctly.
                BlockHeader parentView = new(Keccak.Zero, Keccak.Zero, Address.Zero, 0, parentBlockNumber, 0, 0, [])
                {
                    StateRoot = parentStateRoot,
                };
                witness = recorder.GetWitness(parentView, parentHash);
            }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error($"{nameof(WitnessCapturingBlockProcessor)}: witness build failed for block {blockHash}", ex);
            }
            tcs!.SetResult(witness);
            return result;
        }
        catch
        {
            rendezvous.CancelWitnessRequest(blockHash!);
            throw;
        }
        finally
        {
            proxy.Deactivate(recorder);
        }
    }
}
