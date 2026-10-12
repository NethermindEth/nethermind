// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Consensus.Stateless;

/// <summary>Cross-thread coordination between the JSON-RPC handler requesting a witness for a block hash and the processing thread producing one.</summary>
/// <remarks>Only one witness is ever in flight (block processing is serialized), so this is a single hash-validated slot rather than a registry.</remarks>
public sealed class WitnessRendezvous(ILogManager? logManager = null)
{
    private readonly ILogger _logger = (logManager ?? LimboLogs.Instance).GetClassLogger<WitnessRendezvous>();
    private readonly object _lock = new();
    private Hash256? _requestedHash;
    private TaskCompletionSource<Witness?>? _pending;

    /// <summary>Registers a pending witness request for <paramref name="blockHash"/>, returning a disposable registration whose <see cref="WitnessRequest.Task"/> completes when the block is processed.</summary>
    /// <remarks>If a request is already outstanding, the registration is declined: its task yields <c>null</c>.</remarks>
    public WitnessRequest RequestWitness(Hash256 blockHash)
    {
        lock (_lock)
        {
            if (_pending is not null)
            {
                if (_logger.IsWarn) _logger.Warn($"{nameof(WitnessRendezvous)}: a witness request for {_requestedHash} is already outstanding; declining the request for {blockHash}.");
                return WitnessRequest.Declined;
            }

            // Completion fires on the block-processing thread; don't run the handler's continuation inline there.
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

    /// <summary>Atomically takes the pending TCS for <paramref name="blockHash"/> so the caller can complete it; <c>false</c> when none is pending for that hash.</summary>
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

    /// <summary>Releases the slot held by <paramref name="tcs"/> (cancelling its task) if it still owns the current pending request. Idempotent.</summary>
    internal void Release(Hash256 blockHash, TaskCompletionSource<Witness?> tcs)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_pending, tcs)) return;

            _pending = null;
            _requestedHash = null;
        }

        tcs.TrySetCanceled();
        if (_logger.IsTrace) _logger.Trace($"{nameof(WitnessRendezvous)}: capture released for {blockHash}");
    }
}

/// <summary>A handler-side witness-request registration holding the awaitable <see cref="Task"/>; <see cref="Dispose"/> removes the rendezvous slot and cancels the task (idempotent).</summary>
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
