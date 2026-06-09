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
using Nethermind.Evm.State;
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
/// <para>
/// Two complementary capture layers are active during each witnessed <c>ProcessOne</c> call:
/// </para>
/// <list type="number">
///   <item>
///     <b><see cref="WitnessCapturingWorldStateProxy"/> / <see cref="WitnessGeneratingWorldState"/></b>
///     — records every account/slot/bytecode access via <see cref="IWorldState"/> call hooks.
///     Drives <see cref="WitnessProofCollector"/> which runs a tree visitor over the recorded keys
///     to produce Merkle proofs.
///   </item>
///   <item>
///     <b><see cref="WitnessCapturingTrieStore"/></b> — intercepts raw trie node reads at the
///     storage layer. This catches sibling reads that occur during <c>RecalculateStateRoot()</c>
///     when branch nodes collapse after account deletion or storage clearing. Those reads never
///     surface at the <see cref="IWorldState"/> level, so layer (1) alone would silently omit
///     them, producing a witness that stateless verifiers cannot use to reconstruct the post-state root.
///   </item>
/// </list>
/// <para>
/// All capture state lives on per-call instances — there is no global armed/disarmed flag, no
/// shared mutable dictionaries, and no nested-arming guard beyond the proxy's atomic
/// activate/deactivate. Blocks with no pending request bypass the recorder entirely.
/// </para>
/// </remarks>
public sealed class WitnessCapturingBlockProcessor(
    IBlockProcessor inner,
    WitnessCapturingWorldStateProxy proxy,
    WitnessRendezvous rendezvous,
    IStateReader stateReader,
    IHeaderFinder headerFinder,
    WitnessCapturingTrieStore trieStore,
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

        WitnessGeneratingHeaderFinder perBlockHeaderFinder = new(headerFinder);

        // Reset the shared trie store so only nodes touched during *this* block are captured.
        // The trie store wraps the main pipeline's read-only store, intercepting every node load
        // that RecalculateStateRoot() triggers (e.g. sibling reads during branch collapse on
        // account deletion or storage clearing) — reads that never surface at the IWorldState level.
        trieStore.Reset();
        WitnessGeneratingWorldState recorder = new(proxy.InnerState, stateReader, trieStore, perBlockHeaderFinder);

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
                witness = recorder.GetWitness(parent);
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
