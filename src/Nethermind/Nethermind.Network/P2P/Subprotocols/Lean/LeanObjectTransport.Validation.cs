// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Lean;

public sealed partial class LeanObjectTransport
{
    private const int MaxBusyRetries = 50;
    // Each worker has at most one ETH recovery batch outstanding; never exceed the ETH per-peer cap.
    private const int MaxRecoveringValidations = LeanProtocol.MaxRequestsPerPeer;

    /// <summary>The outcome of validating a completed object.</summary>
    /// <remarks><see cref="Refused"/> is a local inability to finish validation, including an unsupported proof or a closed
    /// source. It does not establish invalidity, so it is neither penalized nor tombstoned.
    /// <see cref="Unresolved"/> is soft-tombstoned only while the source's ETH recovery link remains bound.</remarks>
    private enum Verdict { Valid, Invalid, LocalFailure, Refused, Unresolved }

    /// <summary>Hands the owned reassembly buffer to the bounded validation queue without a completion copy.</summary>
    /// <remarks>Without room the assembly stays charged and is retried on the timer until it expires.</remarks>
    private void TryComplete(LeanAssembly assembly, Outbox outbox)
    {
        if (_disposed || !assembly.IsComplete || assembly.Queued || assembly.Dropped
            || _pendingValidations >= LeanLimits.MaxPendingValidations) return;
        long length = assembly.RetrievalBytes;
        _incompleteBytes -= length;
        _completionBytes += length;
        assembly.CompletionSource = assembly.CurrentSource;
        if (assembly.CurrentSource is { } source) assembly.Contributors.Add(source);
        assembly.Queued = true;
        ReleaseChunks(assembly, releaseMetadata: false);
        assembly.Processing = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        CancelRequestsFor(assembly, outbox);
        _pendingValidations++;
        _validation.Enqueue(assembly);
        if (_validationWorkers < MaxRecoveringValidations)
        {
            _validationWorkers++;
            _ = Task.Run(ValidateQueueAsync);
        }
    }

    private async Task ValidateQueueAsync()
    {
        try
        {
            while (true)
            {
                LeanAssembly? assembly;
                lock (_gate)
                {
                    if (_disposed || !_validation.TryDequeue(out assembly)) return;
                    // An assembly that expired while queued already released its body.
                    if (assembly.Dropped) continue;
                    _pendingValidations--;
                    assembly.Started = true;
                }
                await ProcessAndReleaseAsync(assembly).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_gate)
            {
                _validationWorkers--;
                if (_disposed)
                {
                    // Active verifications may outlive Dispose; the last worker owns the gate's teardown.
                    if (_validationWorkers == 0) _verification.Dispose();
                }
                else if (_validation.Count != 0)
                {
                    // Enqueue may have observed this worker just before it exited with an empty queue.
                    _validationWorkers++;
                    _ = Task.Run(ValidateQueueAsync);
                }
            }
        }
    }

    /// <summary>Processes a completed assembly, then releases its completion copy and its place in the assembly map.</summary>
    private async Task ProcessAndReleaseAsync(LeanAssembly assembly)
    {
        CancellationToken cancellation = assembly.Processing!.Token;
        try
        {
            await ProcessAsync(assembly, cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // The assembly reached a deadline while its body awaited validation or recovery.
            Interlocked.Increment(ref _stats.Expired);
            LeanMetrics.Record(LeanEvent.Expired);
            if (_logger.IsDebug) _logger.Debug($"lean/1 assembly expired during validation: {assembly.Descriptor}");
        }
        catch (Exception exception)
        {
            if (_logger.IsWarn) _logger.Warn($"lean/1 validation of {assembly.Descriptor} failed: {exception.Message}");
        }
        finally
        {
            Outbox outbox = new();
            CancellationTokenSource? processing;
            lock (_gate)
            {
                ReleaseCompletion(assembly);
                processing = assembly.Processing;
                assembly.Processing = null;
                if (_assemblies.TryGetValue(assembly.Descriptor.ObjectId, out LeanAssembly? current) && current == assembly)
                {
                    if (!assembly.RetrySource)
                    {
                        _assemblies.Remove(assembly.Descriptor.ObjectId);
                        ReleaseChunks(assembly);
                    }
                    else
                    {
                        assembly.Queued = false;
                        assembly.Started = false;
                        assembly.Received = 0;
                        assembly.RetrySource = false;
                    }
                }
                Schedule(outbox, _clock.GetTimestamp());
            }
            outbox.Flush();
            processing?.Dispose();
        }
    }

    /// <summary>Releases a completed body's reservation after processing finishes.</summary>
    private void ReleaseCompletion(LeanAssembly assembly)
    {
        if (assembly.Body is null) return;
        long released = assembly.RetrievalBytes + assembly.RecoveryBytes;
        _completionBytes -= released;
        if (assembly.CompletionSource is { } source) { source.ChargedBytes -= released; PruneNode(source.Node); }
        assembly.RecoveryBytes = 0;
        assembly.RetrievalBytes = 0;
        assembly.CompletionSource = null;
        assembly.Body = null;
        if (!assembly.Started)
        {
            // Released while still queued: the queue skips it once dequeued.
            _pendingValidations--;
            assembly.Processing?.Dispose();
            assembly.Processing = null;
        }
    }

    private async Task ProcessAsync(LeanAssembly assembly, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        LeanDescriptor descriptor = assembly.Descriptor;
        byte[] body = assembly.Body!;
        string? integrityError = CheckIntegrity(descriptor, body, out List<ValueHash256> entries, out int verificationBytes);
        if (integrityError is not null)
        {
            Outbox outbox = new();
            lock (_gate)
            {
                if (assembly.CurrentSource is { } source)
                {
                    assembly.Sources.Remove(source);
                    assembly.CurrentSource = null;
                    assembly.RetrySource = assembly.SourceAttempts < LeanLimits.MaxSourcesPerAssembly && assembly.Sources.Count != 0;
                    Violation(source, integrityError, outbox);
                }
            }
            outbox.Flush();
            return;
        }
        assembly.VerificationBytes = verificationBytes;
        LeanMetrics.Record(LeanEvent.Reassembled);
        lock (_gate)
        {
            long unused = assembly.RetrievalBytes - body.Length - verificationBytes;
            if (unused > 0)
            {
                assembly.RetrievalBytes -= unused;
                _completionBytes -= unused;
                if (assembly.CompletionSource is { } source) source.ChargedBytes -= unused;
            }
            Interlocked.Increment(ref _stats.Reassembled);
        }

        (Verdict verdict, string? error) = await ValidateWrapperAsync(assembly, body, entries, cancellation).ConfigureAwait(false);
        switch (verdict)
        {
            case Verdict.Valid:
                Interlocked.Increment(ref _stats.Validated);
                LeanMetrics.Record(LeanEvent.Validated);
                if (_logger.IsDebug) _logger.Debug($"lean/1 validated {descriptor} from {assembly.Contributors.Count} peer(s)");
                break;
            case Verdict.Invalid:
                Reject(assembly, error ?? "invalid proof object");
                break;
            case Verdict.Refused:
                Interlocked.Increment(ref _stats.LocalFailures);
                LeanMetrics.Record(LeanEvent.LocalFailure);
                if (_logger.IsDebug) _logger.Debug($"lean/1 refused {descriptor}: {error}");
                break;
            default:
                lock (_gate)
                {
                    Interlocked.Increment(ref _stats.LocalFailures);
                    if (assembly.CompletionSource is { Closed: false } source
                        && (verdict != Verdict.Unresolved || source.Link.HasTransactionRecovery))
                        _tombstones.Add(descriptor.ObjectId, _clock.GetTimestamp() + (long)(LeanLimits.SoftTombstone.TotalSeconds * _clock.TimestampFrequency));
                }
                LeanMetrics.Record(LeanEvent.LocalFailure);
                if (_logger.IsDebug) _logger.Debug($"lean/1 could not admit {descriptor}: {error}");
                break;
        }
    }

    /// <summary>Checks length, <c>content_hash</c> and the canonical hash-only wrapper encoding.</summary>
    private static string? CheckIntegrity(LeanDescriptor descriptor, byte[] body, out List<ValueHash256> entries, out int verificationBytes)
    {
        entries = [];
        verificationBytes = 0;
        if ((ulong)body.Length != descriptor.ByteLength) return "reconstructed length differs from the descriptor";
        if (ValueKeccak.Compute(body) != descriptor.ContentHash) return "content hash mismatch";
        try
        {
            entries = LeanBodies.ParseWrapper(body, out verificationBytes);
        }
        catch (RlpException exception)
        {
            return $"noncanonical body: {exception.Message}";
        }
        return null;
    }

    /// <summary>Tombstones an invalid object and penalizes every peer that announced or supplied it.</summary>
    private void Reject(LeanAssembly assembly, string reason)
    {
        Outbox outbox = new();
        lock (_gate)
        {
            _tombstones.Add(assembly.Descriptor.ObjectId);
            Interlocked.Increment(ref _stats.Invalid);
            HashSet<LeanPeer> suppliers = assembly.CurrentSource is { } source ? [source] : [];
            foreach (LeanPeer peer in suppliers) Violation(peer, $"supplied invalid {assembly.Descriptor}: {reason}", outbox);
        }
        outbox.Flush();
        LeanMetrics.Record(LeanEvent.Invalid);
        if (_logger.IsInfo) _logger.Info($"lean/1 rejected {assembly.Descriptor}: {reason}");
    }

    private async Task<(Verdict, string?)> ValidateWrapperAsync(LeanAssembly assembly, byte[] body, List<ValueHash256> entries,
        CancellationToken cancellation)
    {
        List<ValueHash256>? missing = null;
        foreach (ValueHash256 hash in entries)
            if (!_txPool.TryGetPendingTransaction(hash, out _)) (missing ??= []).Add(hash);
        Dictionary<ValueHash256, Transaction>? recovered = null;
        if (missing is not null)
        {
            // Verify the witnesses or aggregate proof against the claimed dependencies before fetching anything, so an
            // invalid proof costs no recovery. This does not validate the wrapper: its transactions are checked once resolved.
            (Verdict claims, string? claimsError) = await VerifyWithBudgetAsync(assembly,
                token => _wrappers.VerifyClaimedProofsOwnedAsync(body, token), cancellation).ConfigureAwait(false);
            if (claims != Verdict.Valid) return (claims, claimsError);
            // An unresolved wrapper is retained only within this bounded recovery and is never admitted or announced.
            recovered = await RecoverTransactionsAsync(assembly, missing, cancellation).ConfigureAwait(false);
            lock (_gate)
            {
                if (assembly.CompletionSource is not { Closed: false }) return (Verdict.Refused, "source closed during envelope recovery");
                if (recovered.Count != missing.Count && !assembly.CompletionSource.Link.HasTransactionRecovery)
                    return (Verdict.Refused, "ETH recovery link unavailable");
            }
            if (recovered.Count != missing.Count) return (Verdict.Unresolved, $"{missing.Count - recovered.Count} hash entries unresolved");
        }
        return await VerifyWithBudgetAsync(assembly,
            token => _wrappers.AcceptOwnedDetailedAsync(body, recovered, token), cancellation).ConfigureAwait(false);
    }

    private async Task<(Verdict, string?)> VerifyWithBudgetAsync(LeanAssembly assembly,
        Func<CancellationToken, Task<ProofWrapperAcceptance>> accept, CancellationToken cancellation)
    {
        // Recovery may overlap across bounded workers; proof verification and admission remain serialized.
        await _verification.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (assembly.CompletionSource is not { Closed: false }) return (Verdict.Refused, "source closed before verification");
            if (assembly.VerificationBytes > assembly.RetrievalBytes - (long)assembly.Descriptor.ByteLength)
                return (Verdict.Refused, "no decoder working-space budget");
            return await AcceptWithRetryAsync(accept, cancellation).ConfigureAwait(false);
        }
        finally { _verification.Release(); }
    }

    private static async Task<(Verdict, string?)> AcceptWithRetryAsync(Func<CancellationToken, Task<ProofWrapperAcceptance>> accept,
        CancellationToken cancellation)
    {
        for (int attempt = 0; ; attempt++)
        {
            ProofWrapperAcceptance result = await accept(cancellation).ConfigureAwait(false);
            switch (result.Status)
            {
                case ProofWrapperAcceptanceStatus.Accepted or ProofWrapperAcceptanceStatus.PoolRejected:
                    // Pool policy is not a protocol matter: a valid proof is retained even if its transactions were refused.
                    return (Verdict.Valid, null);
                case ProofWrapperAcceptanceStatus.Invalid:
                    return (Verdict.Invalid, result.Result.Error);
                case ProofWrapperAcceptanceStatus.Refused:
                    return (Verdict.Refused, result.Result.Error);
                case ProofWrapperAcceptanceStatus.Busy when attempt < MaxBusyRetries:
                    await Task.Delay(LeanLimits.BusyRetry, cancellation).ConfigureAwait(false);
                    continue;
                default:
                    return (Verdict.LocalFailure, result.Result.Error);
            }
        }
    }

    private async Task<Dictionary<ValueHash256, Transaction>> RecoverTransactionsAsync(LeanAssembly assembly, List<ValueHash256> missing,
        CancellationToken cancellation)
    {
        Dictionary<ValueHash256, Transaction> recovered = [];
        LeanPeer? source;
        lock (_gate)
            source = assembly.CurrentSource is { Closed: false } current && current.Link.HasTransactionRecovery ? current : null;
        if (source is null) return recovered;
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(LeanLimits.TransactionRecovery);
        const int batchSize = 16;
        for (int start = 0; start < missing.Count && !timeout.IsCancellationRequested; start += batchSize)
        {
            lock (_gate)
                if (source.Closed || !source.Link.HasTransactionRecovery) break;
            ValueHash256[] batch = missing.GetRange(start, Math.Min(batchSize, missing.Count - start)).ToArray();
            Transaction[] transactions;
            try
            {
                transactions = await source.Link.RecoverTransactionsAsync(batch,
                    bytes => ReserveRecovery(assembly, source, bytes), timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellation.IsCancellationRequested)
            {
                break;
            }
            foreach (Transaction transaction in transactions)
            {
                ValueHash256 hash = (transaction.Hash ?? transaction.CalculateHash()).ValueHash256;
                if (Array.IndexOf(batch, hash) < 0)
                {
                    Outbox outbox = new();
                    lock (_gate) Violation(source, "ETH recovery returned an unrequested envelope", outbox);
                    outbox.Flush();
                    return recovered;
                }
                if (recovered.TryAdd(hash, transaction))
                    lock (_gate) if (!assembly.Dropped) assembly.Updated = _clock.GetTimestamp();
            }
        }
        cancellation.ThrowIfCancellationRequested();
        return recovered;
    }

    private bool ReserveRecovery(LeanAssembly assembly, LeanPeer source, int bytes)
    {
        lock (_gate)
        {
            if (bytes < 0 || assembly.Dropped || source.Closed || !source.Link.HasTransactionRecovery || assembly.CompletionSource != source
                || !CanReserveObjectBytes(bytes)
                || bytes > LeanLimits.MaxPeerBytes - source.ChargedBytes - 4 * LeanProtocol.MaxMessageBytes) return false;
            _completionBytes += bytes;
            source.ChargedBytes += bytes;
            assembly.RecoveryBytes += bytes;
            return true;
        }
    }
}
