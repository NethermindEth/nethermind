// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// Cross-thread coordination between the JSON-RPC handler that requests a witness for a block hash
/// and the block-processing thread that produces one. The handler awaits a <see cref="Task{T}"/>;
/// the processor completes it once the matching <c>ProcessOne</c> finishes.
/// </summary>
/// <remarks>
/// Only one witness is ever in flight: the engine module serializes all <c>newPayload*</c>/
/// <c>forkchoiceUpdated</c> calls under a single semaphore, and the processing queue runs one block
/// at a time on a single thread. So this is a single hash-validated slot — bounded by construction,
/// not an unbounded registry. Handlers register via <see cref="RequestWitness"/> and hold the
/// returned <see cref="WitnessRequest"/> in a <c>using</c>; its <c>Dispose</c> removes the slot and
/// cancels the task on every exit path, so leak-safety is structural rather than caller-disciplined.
/// </remarks>
public sealed class WitnessRendezvous(ILogManager? logManager = null)
{
    private readonly ILogger _logger = (logManager ?? LimboLogs.Instance).GetClassLogger<WitnessRendezvous>();
    private readonly object _lock = new();
    private Hash256? _requestedHash;
    private TaskCompletionSource<Witness?>? _pending;

    /// <summary>
    /// Handler-side: register a pending witness request for <paramref name="blockHash"/> and return a
    /// disposable registration whose <see cref="WitnessRequest.Task"/> completes when the block is
    /// processed (or is cancelled when the registration is disposed).
    /// </summary>
    /// <remarks>
    /// If a request is already outstanding, the returned registration is <em>declined</em>: its task
    /// yields <c>null</c> and disposing it is a no-op. Declining (rather than evicting the in-flight
    /// request) matches the "one witness at a time" reality; the caller treats a null witness as
    /// VALID-with-no-witness, exactly like the other give-up paths.
    /// </remarks>
    public WitnessRequest RequestWitness(Hash256 blockHash)
    {
        lock (_lock)
        {
            if (_pending is not null)
            {
                if (_logger.IsWarn) _logger.Warn($"{nameof(WitnessRendezvous)}: a witness request for {_requestedHash} is already outstanding; declining the request for {blockHash}.");
                return WitnessRequest.Declined;
            }

            // RunContinuationsAsynchronously: completion fires from the block-processing thread; we must
            // not run the handler's continuation inline there.
            _pending = new TaskCompletionSource<Witness?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _requestedHash = blockHash;
            return new WitnessRequest(this, blockHash, _pending);
        }
    }

    /// <summary>True iff a witness has been requested for <paramref name="blockHash"/>.</summary>
    public bool HasPendingRequest(Hash256 blockHash)
    {
        lock (_lock)
        {
            return _pending is not null && _requestedHash == blockHash;
        }
    }

    /// <summary>
    /// Recorder-side: atomically take the pending TCS for <paramref name="blockHash"/> so the caller
    /// can complete it. Returns <c>false</c> when no request is pending for that hash (declined,
    /// cancelled, or already claimed).
    /// </summary>
    /// <remarks>
    /// Two-step (claim + complete) rather than a single <c>Complete(hash, witness)</c> so the recorder
    /// can avoid building the witness when no request is pending.
    /// </remarks>
    public bool TryClaim(Hash256 blockHash, out TaskCompletionSource<Witness?>? tcs)
    {
        lock (_lock)
        {
            if (_pending is not null && _requestedHash == blockHash)
            {
                tcs = _pending;
                _pending = null;
                _requestedHash = null;
                return true;
            }

            tcs = null;
            return false;
        }
    }

    /// <summary>
    /// Releases the slot held by <paramref name="tcs"/> (cancelling its task) if it is still the
    /// current pending request. Idempotent and safe to call after the recorder has claimed it.
    /// </summary>
    internal void Release(Hash256 blockHash, TaskCompletionSource<Witness?> tcs)
    {
        lock (_lock)
        {
            // Identity check: only clear the slot if this exact registration still owns it. A claimed
            // (and thus replaced/cleared) slot leaves _pending != tcs, so this is a no-op there.
            if (!ReferenceEquals(_pending, tcs)) return;

            _pending = null;
            _requestedHash = null;
        }

        tcs.TrySetCanceled();
        if (_logger.IsTrace) _logger.Trace($"{nameof(WitnessRendezvous)}: capture released for {blockHash}");
    }
}

/// <summary>
/// A handler-side registration for a witness request. Holds the awaitable <see cref="Task"/> and,
/// on <see cref="Dispose"/>, removes the rendezvous slot and cancels the task (idempotent). Hold it
/// in a <c>using</c> so cleanup happens on every exit path.
/// </summary>
public sealed class WitnessRequest : IDisposable
{
    private readonly WitnessRendezvous? _rendezvous;
    private readonly Hash256? _blockHash;
    private readonly TaskCompletionSource<Witness?>? _tcs;

    internal WitnessRequest(WitnessRendezvous rendezvous, Hash256 blockHash, TaskCompletionSource<Witness?> tcs)
    {
        _rendezvous = rendezvous;
        _blockHash = blockHash;
        _tcs = tcs;
        Task = tcs.Task;
    }

    private WitnessRequest() => Task = System.Threading.Tasks.Task.FromResult<Witness?>(null);

    /// <summary>A declined registration (one was already outstanding): a completed null task, no slot.</summary>
    internal static WitnessRequest Declined { get; } = new();

    /// <summary>Completes with the captured witness, or <c>null</c> when none is produced or the request is cancelled/declined.</summary>
    public Task<Witness?> Task { get; }

    public void Dispose() => _rendezvous?.Release(_blockHash!, _tcs!);
}
