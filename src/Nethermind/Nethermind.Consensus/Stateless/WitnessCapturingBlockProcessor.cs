// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
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
/// being processed, arms the <see cref="WitnessCaptureSession"/> with fresh per-block recorders for
/// the duration of one <see cref="ProcessOne"/> call, then projects the recorded set into a
/// <see cref="Witness"/> and publishes it via <see cref="WitnessRendezvous"/>.
/// </summary>
/// <remarks>
/// <para>
/// Three complementary capture surfaces are active during each witnessed <c>ProcessOne</c> call,
/// all gated by the session:
/// </para>
/// <list type="number">
///   <item>
///     <b><see cref="WitnessCapturingWorldStateProxy"/> / <see cref="WitnessGeneratingWorldState"/></b>
///     — records every account/slot/bytecode access via <see cref="IWorldState"/> call hooks.
///     Drives <see cref="WitnessGeneratingWorldState.GetWitness"/>, which runs a tree visitor over
///     the recorded keys to produce Merkle proofs.
///   </item>
///   <item>
///     <b><see cref="WitnessCapturingHeaderFinder"/> / <see cref="WitnessHeaderRecorder"/></b>
///     — catches header lookups from the EVM (e.g. BLOCKHASH) and the rest of the processing
///     pipeline so the witness header chain extends back to whatever the block touched.
///   </item>
///   <item>
///     <b><see cref="WitnessCapturingTrieStore"/> / <see cref="WitnessTrieStoreRecorder"/></b>
///     — intercepts raw trie node reads at the storage layer for the case where branch nodes
///     collapse during state-root recomputation and siblings are read that never surface at the
///     <see cref="IWorldState"/> level.
///   </item>
/// </list>
/// <para>
/// All capture state lives on per-call instances installed onto the session — there is no global
/// armed/disarmed flag, no shared mutable dictionaries, and the session's atomic
/// <see cref="WitnessCaptureSession.TryArm"/> rejects nested or concurrent capture attempts.
/// Blocks with no pending request bypass the capture machinery entirely.
/// </para>
/// </remarks>
public sealed class WitnessCapturingBlockProcessor(
    IBlockProcessor inner,
    WitnessCapturingWorldStateProxy proxy,
    WitnessCapturingHeaderFinder headerFinder,
    WitnessCapturingTrieStore trieStore,
    WitnessCaptureSession session,
    WitnessRendezvous rendezvous,
    IStateReader stateReader,
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
        BlockHeader parent = headerFinder.Inner.Get(parentHash, parentBlockNumber)
            ?? throw new ArgumentException($"Unable to find parent for block {parentBlockNumber} with hash {parentHash}");

        WitnessTrieStoreRecorder trieRecorder = new();
        WitnessHeaderRecorder headerRecorder = new();
        WitnessGeneratingWorldState recorder = new(
            proxy.InnerState,
            stateReader,
            trieStore,
            trieRecorder,
            headerRecorder,
            headerFinder.Inner);

        if (!session.TryArm(recorder, headerRecorder, trieRecorder))
        {
            // Another capture is in progress for some other block on this session. Skip capture
            // for this one rather than risking interleaved recording.
            if (_logger.IsWarn) _logger.Warn($"{nameof(WitnessCapturingBlockProcessor)}: session already armed when processing {blockHash}; skipping capture.");
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
            session.Disarm();
        }
    }
}
