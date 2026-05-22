// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// Cross-thread coordination between the JSON-RPC handler that requests a witness for a block
/// hash and the block-processing thread that produces one. The handler awaits a <see cref="Task{T}"/>;
/// the processor completes it once the matching <c>ProcessOne</c> finishes.
/// </summary>
/// <remarks>
/// No state-recording concerns live here; this type is purely a hash-keyed <see cref="TaskCompletionSource{T}"/>
/// registry. The recorder side ([[witness-capturing-block-processor]]) owns the recording lifecycle and
/// calls <see cref="TryClaim"/> to publish a result.
/// </remarks>
public sealed class WitnessRendezvous(ILogManager? logManager = null)
{
    private readonly ILogger _logger = (logManager ?? LimboLogs.Instance).GetClassLogger<WitnessRendezvous>();
    private readonly ConcurrentDictionary<Hash256AsKey, TaskCompletionSource<Witness?>> _pending = new();

    /// <summary>
    /// Handler-side: register a pending witness request for <paramref name="blockHash"/> and return
    /// a <see cref="Task{T}"/> that completes when the block is processed (or is cancelled).
    /// A duplicate request for the same hash cancels the previous task and replaces the entry.
    /// </summary>
    public Task<Witness?> RequestWitness(Hash256 blockHash)
    {
        // RunContinuationsAsynchronously: completion fires from the block-processing thread; we must
        // not run the handler's continuation inline there.
        TaskCompletionSource<Witness?> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<Witness?> effective = _pending.AddOrUpdate(
            blockHash,
            tcs,
            (_, existing) =>
            {
                if (_logger.IsWarn) _logger.Warn($"{nameof(WitnessRendezvous)}: duplicate RequestWitness for {blockHash}. Replacing previous entry.");
                existing.TrySetCanceled();
                return tcs;
            });
        return effective.Task;
    }

    /// <summary>True iff a witness has been requested for <paramref name="blockHash"/>.</summary>
    public bool HasPendingRequest(Hash256 blockHash) => _pending.ContainsKey(blockHash);

    /// <summary>
    /// Handler-side: cancel a pending request (e.g. on the exception path before the processor drains).
    /// No-op when no entry exists for <paramref name="blockHash"/>.
    /// </summary>
    public void CancelWitnessRequest(Hash256 blockHash)
    {
        if (_pending.TryRemove(blockHash, out TaskCompletionSource<Witness?>? tcs))
        {
            tcs.TrySetCanceled();
            if (_logger.IsTrace) _logger.Trace($"{nameof(WitnessRendezvous)}: capture cancelled for {blockHash}");
        }
    }

    /// <summary>
    /// Recorder-side: atomically remove and return the pending TCS for <paramref name="blockHash"/>.
    /// Returns <c>false</c> when no request is pending or the entry was already claimed/cancelled.
    /// </summary>
    /// <remarks>
    /// Two-step (claim + complete) rather than a single <c>Complete(hash, witness)</c> so the recorder
    /// can avoid building the witness when the request was cancelled while processing.
    /// </remarks>
    public bool TryClaim(Hash256 blockHash, out TaskCompletionSource<Witness?>? tcs)
        => _pending.TryRemove(blockHash, out tcs);
}
