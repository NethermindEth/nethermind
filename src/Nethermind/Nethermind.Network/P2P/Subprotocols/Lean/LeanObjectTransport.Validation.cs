// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    /// <summary>The outcome of validating a completed object.</summary>
    /// <remarks><see cref="Refused"/> is an object within EIP-8288's limits that the local verifier cannot check: EIP-8437
    /// requires refusing it locally without treating it as invalid, so it is neither penalized nor tombstoned.</remarks>
    private enum Verdict { Valid, Invalid, LocalFailure, Refused }

    /// <summary>Moves a fully received assembly into the bounded validation queue once its completion copy can be reserved.</summary>
    /// <remarks>Without room the assembly stays charged and is retried on the timer until it expires.</remarks>
    private void TryComplete(LeanAssembly assembly, Outbox outbox)
    {
        if (_disposed || !assembly.IsComplete || assembly.Queued || assembly.Dropped) return;
        if (!assembly.Validate && assembly.Waiters.Count == 0)
        {
            _assemblies.Remove(assembly.Descriptor.ObjectId);
            Drop(assembly);
            return;
        }
        long length = (long)assembly.Descriptor.ByteLength;
        if ((assembly.Validate && _pendingValidations >= LeanLimits.MaxPendingValidations) || length > LeanLimits.MaxCompletionBytes - _completionBytes) return;
        _completionBytes += length;
        byte[] body = new byte[length];
        int offset = 0;
        for (int index = 0; index < assembly.Chunks.Length; index++)
        {
            byte[] chunk = assembly.Chunks[index]!;
            chunk.CopyTo(body, offset);
            offset += chunk.Length;
            if (assembly.ChargedTo[index] is { } contributor && !assembly.Contributors.Contains(contributor)) assembly.Contributors.Add(contributor);
        }
        Enqueue(assembly, body, outbox);
    }

    /// <summary>Hands a complete body, whose copy is already reserved, to processing and stops retrieving its chunks.</summary>
    private void Enqueue(LeanAssembly assembly, byte[] body, Outbox outbox)
    {
        ReleaseChunks(assembly);
        assembly.Body = body;
        assembly.Queued = true;
        assembly.Processing = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        CancelRequestsFor(assembly, outbox);
        if (!assembly.Validate)
        {
            // Bodies fetched for a waiter (sidecars, transaction recovery) never wait behind the validation they may serve.
            assembly.Started = true;
            _ = Task.Run(() => ProcessAndReleaseAsync(assembly));
            return;
        }
        _pendingValidations++;
        _validation.Enqueue(assembly);
        if (!_validating)
        {
            _validating = true;
            _ = Task.Run(ValidateQueueAsync);
        }
    }

    private async Task ValidateQueueAsync()
    {
        while (true)
        {
            LeanAssembly? assembly;
            lock (_gate)
            {
                if (_disposed || !_validation.TryDequeue(out assembly))
                {
                    _validating = false;
                    return;
                }
                // An assembly that expired while queued already released its body.
                if (assembly.Dropped) continue;
                _pendingValidations--;
                assembly.Started = true;
            }
            await ProcessAndReleaseAsync(assembly).ConfigureAwait(false);
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
            LeanMetrics.Record(assembly.Descriptor.Kind, LeanEvent.Expired);
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
                    _assemblies.Remove(assembly.Descriptor.ObjectId);
                foreach (TaskCompletionSource<byte[]?> waiter in assembly.Waiters) waiter.TrySetResult(null);
                assembly.Waiters.Clear();
                Schedule(outbox, _clock.GetTimestamp());
            }
            outbox.Flush();
            processing?.Dispose();
        }
    }

    /// <summary>Releases a completed body's reservation, once.</summary>
    private void ReleaseCompletion(LeanAssembly assembly)
    {
        if (assembly.Body is null) return;
        _completionBytes -= (long)assembly.Descriptor.ByteLength;
        assembly.Body = null;
        if (!assembly.Started && assembly.Validate)
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
        List<LeanBodies.WrapperEntry>? entries = null;
        string? integrityError = CheckIntegrity(descriptor, body, ref entries);
        if (integrityError is not null)
        {
            Reject(assembly, integrityError);
            return;
        }
        LeanMetrics.Record(descriptor.Kind, LeanEvent.Reassembled);
        TaskCompletionSource<byte[]?>[] waiters;
        bool validate;
        lock (_gate)
        {
            Interlocked.Increment(ref _stats.Reassembled);
            waiters = [.. assembly.Waiters];
            assembly.Waiters.Clear();
            validate = assembly.Validate;
        }
        foreach (TaskCompletionSource<byte[]?> waiter in waiters) waiter.TrySetResult(body);
        if (!validate) return;

        (Verdict verdict, string? error) = descriptor.Kind switch
        {
            LeanProtocol.KindWrapper => await ValidateWrapperAsync(assembly, body, entries!, cancellation).ConfigureAwait(false),
            LeanProtocol.KindInclusionList => await AcceptWithRetryAsync(
                token => _wrappers.AcceptInclusionListDetailedAsync(body, token), cancellation).ConfigureAwait(false),
            _ => (Verdict.LocalFailure, "kind is fetched only on demand")
        };
        switch (verdict)
        {
            case Verdict.Valid:
                Interlocked.Increment(ref _stats.Validated);
                LeanMetrics.Record(descriptor.Kind, LeanEvent.Validated);
                if (assembly.BroadcastStarted is { } broadcastStarted)
                    LeanMetrics.Transferred(descriptor.Kind, broadcast: true, Stopwatch.GetElapsedTime(broadcastStarted));
                else LeanMetrics.Transferred(descriptor.Kind, broadcast: false, _clock.GetElapsedTime(assembly.Created));
                if (_logger.IsDebug) _logger.Debug($"lean/1 validated {descriptor} from {assembly.Contributors.Count} peer(s)");
                break;
            case Verdict.Invalid:
                Reject(assembly, error ?? "invalid proof object");
                break;
            case Verdict.Refused:
                Interlocked.Increment(ref _stats.LocalFailures);
                LeanMetrics.Record(descriptor.Kind, LeanEvent.LocalFailure);
                if (_logger.IsDebug) _logger.Debug($"lean/1 refused {descriptor} beyond local verifier capacity: {error}");
                break;
            default:
                lock (_gate)
                {
                    Interlocked.Increment(ref _stats.LocalFailures);
                    _tombstones.Add(descriptor.ObjectId, _clock.GetTimestamp() + (long)(LeanLimits.SoftTombstone.TotalSeconds * _clock.TimestampFrequency));
                }
                LeanMetrics.Record(descriptor.Kind, LeanEvent.LocalFailure);
                if (_logger.IsDebug) _logger.Debug($"lean/1 could not admit {descriptor}: {error}");
                break;
        }
    }

    /// <summary>Checks length, <c>content_hash</c>, the canonical <c>chunk_root</c> and canonical body encoding.</summary>
    private static string? CheckIntegrity(LeanDescriptor descriptor, byte[] body, ref List<LeanBodies.WrapperEntry>? entries)
    {
        if ((ulong)body.Length != descriptor.ByteLength) return "reconstructed length differs from the descriptor";
        if (ValueKeccak.Compute(body) != descriptor.ContentHash) return "content hash mismatch";
        if (new LeanChunkTree(descriptor.Seed, body).ChunkRoot != descriptor.ChunkRoot) return "noncanonical chunk root";
        try
        {
            if (descriptor.Kind == LeanProtocol.KindWrapper) entries = LeanBodies.ParseWrapper(body);
            else LeanBodies.Check(descriptor.Kind, body);
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
            HashSet<LeanPeer> suppliers = [.. assembly.Contributors, .. assembly.Sources];
            foreach (LeanPeer peer in suppliers) Violation(peer, $"supplied invalid {assembly.Descriptor}: {reason}", outbox);
        }
        outbox.Flush();
        LeanMetrics.Record(assembly.Descriptor.Kind, LeanEvent.Invalid);
        if (_logger.IsInfo) _logger.Info($"lean/1 rejected {assembly.Descriptor}: {reason}");
    }

    private async Task<(Verdict, string?)> ValidateWrapperAsync(LeanAssembly assembly, byte[] body, List<LeanBodies.WrapperEntry> entries,
        CancellationToken cancellation)
    {
        List<ValueHash256>? missing = null;
        foreach (LeanBodies.WrapperEntry entry in entries)
            if (!entry.IsFull && !_txPool.TryGetPendingTransaction(entry.Hash, out _)) (missing ??= []).Add(entry.Hash);
        Dictionary<ValueHash256, Transaction>? recovered = null;
        if (missing is not null)
        {
            // Verify the witnesses or aggregate proof against the claimed dependencies before fetching anything, so an
            // invalid proof costs no recovery. This does not validate the wrapper: its transactions are checked once resolved.
            (Verdict claims, string? claimsError) = await AcceptWithRetryAsync(
                token => _wrappers.VerifyClaimedProofsAsync(body, token), cancellation).ConfigureAwait(false);
            if (claims != Verdict.Valid) return (claims, claimsError);
            // An unresolved wrapper is retained only within this bounded recovery and is never admitted or announced.
            recovered = await RecoverTransactionsAsync(assembly, missing, cancellation).ConfigureAwait(false);
            if (recovered.Count != missing.Count) return (Verdict.LocalFailure, $"{missing.Count - recovered.Count} hash entries unresolved");
        }
        return await AcceptWithRetryAsync(token => _wrappers.AcceptDetailedAsync(body, recovered, token), cancellation).ConfigureAwait(false);
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

    /// <summary>Resolves hash entries through GetTransactions and, for envelopes too large for it, a transaction-hash lookup.</summary>
    private async Task<Dictionary<ValueHash256, Transaction>> RecoverTransactionsAsync(LeanAssembly assembly, List<ValueHash256> missing,
        CancellationToken cancellation)
    {
        Dictionary<ValueHash256, Transaction> recovered = [];
        LeanPeer[] sources;
        lock (_gate)
        {
            HashSet<LeanPeer> candidates = [.. assembly.Contributors, .. assembly.Sources];
            candidates.RemoveWhere(static peer => peer.Closed);
            sources = [.. candidates];
        }
        if (sources.Length == 0) return recovered;
        // The assembly's own deadlines cancel recovery too; this is a further local bound.
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(LeanLimits.TransactionRecovery);
        missing.Sort(static (a, b) => a.Bytes.SequenceCompareTo(b.Bytes));
        List<ValueHash256> tooLarge = [];
        for (int start = 0; start < missing.Count && !timeout.IsCancellationRequested; start += LeanProtocol.MaxTxsPerRequest)
        {
            ValueHash256[] batch = missing.GetRange(start, Math.Min(LeanProtocol.MaxTxsPerRequest, missing.Count - start)).ToArray();
            foreach (LeanPeer source in sources)
            {
                (LeanResultStatus Status, byte[] Envelope)[]? results = await RequestTransactionsAsync(source, batch, timeout.Token).ConfigureAwait(false);
                if (results is null) continue;
                bool resolvedNew = false;
                for (int i = 0; i < batch.Length; i++)
                {
                    if (results[i].Status == LeanResultStatus.Ok && !recovered.ContainsKey(batch[i]) && Decode(results[i].Envelope, batch[i]) is { } transaction)
                    {
                        recovered[batch[i]] = transaction;
                        resolvedNew = true;
                    }
                    else if (results[i].Status == LeanResultStatus.TooLarge && !tooLarge.Contains(batch[i])) tooLarge.Add(batch[i]);
                }
                if (resolvedNew) Touch(assembly);
                if (Array.TrueForAll(batch, recovered.ContainsKey)) break;
            }
        }
        for (int i = 0; i < tooLarge.Count && i < MaxTransactionRecoveries && !timeout.IsCancellationRequested; i++)
            if (!recovered.ContainsKey(tooLarge[i]) && await RecoverByLookupAsync(sources, tooLarge[i], assembly, timeout.Token).ConfigureAwait(false) is { } transaction)
            {
                recovered[tooLarge[i]] = transaction;
                Touch(assembly);
            }
        return recovered;
    }

    /// <summary>Records retrieval progress of an assembly awaiting recovery: a newly resolved missing transaction.</summary>
    private void Touch(LeanAssembly assembly)
    {
        lock (_gate) if (!assembly.Dropped) assembly.Updated = _clock.GetTimestamp();
    }

    /// <summary>Extracts a full entry from a peer's kind-1 object that contains it; the object is neither validated nor relayed.</summary>
    /// <remarks>Only full entries are taken, so recoveries are never chained.</remarks>
    private async Task<Transaction?> RecoverByLookupAsync(LeanPeer[] sources, ValueHash256 hash, LeanAssembly waiting, CancellationToken cancellationToken)
    {
        foreach (LeanPeer source in sources)
        {
            LeanObjectResult[]? results = await RequestObjectsAsync(source,
                [new LeanSelector(LeanProtocol.KindWrapper, LocalProfile, LeanProtocol.LookupTransaction, hash)], cancellationToken).ConfigureAwait(false);
            if (results?[0] is not { Status: LeanResultStatus.Ok, Descriptor: { } descriptor }) continue;
            byte[]? body = await FetchBodyAsync(descriptor, null, source, cancellationToken, waiting).ConfigureAwait(false);
            if (body is null) continue;
            foreach (LeanBodies.WrapperEntry entry in LeanBodies.ParseWrapper(body))
                if (entry.IsFull && entry.Hash == hash)
                    return Decode(body.AsSpan(entry.EnvelopeOffset, entry.EnvelopeLength).ToArray(), hash);
        }
        return null;
    }

    private static Transaction? Decode(byte[] envelope, in ValueHash256 hash)
    {
        try
        {
            Transaction transaction = TxDecoder.Instance.DecodeCompleteNotNull(envelope, RlpBehaviors.InMempoolForm | RlpBehaviors.SkipTypedWrapping);
            return (transaction.Hash ?? transaction.CalculateHash()).ValueHash256 == hash ? transaction : null;
        }
        catch (RlpException)
        {
            return null;
        }
    }

    /// <summary>Retrieves an object's integrity-checked body from <paramref name="source"/>, merging with any existing assembly.</summary>
    /// <param name="descriptor">The checked descriptor of the object.</param>
    /// <param name="skeleton">The checked header skeleton of a kind-2 object.</param>
    /// <param name="source">The peer that offered the object.</param>
    /// <param name="cancellationToken">Stops waiting; an assembly nobody else waits for is then dropped.</param>
    /// <param name="dependent">A wrapper recovering a transaction from this object, whose idle timer its new chunks advance.</param>
    internal async Task<byte[]?> FetchBodyAsync(LeanDescriptor descriptor, LeanHeaderSkeleton? skeleton, LeanPeer source, CancellationToken cancellationToken,
        LeanAssembly? dependent = null)
    {
        TaskCompletionSource<byte[]?> waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
        LeanAssembly? assembly;
        Outbox outbox = new();
        lock (_gate)
        {
            long now = _clock.GetTimestamp();
            if (_store.TryGet(descriptor.ObjectId, out LeanObjectStore.Entry entry)) return entry.Body;
            if (_tombstones.Contains(descriptor.ObjectId, now)) return null;
            if (!_assemblies.TryGetValue(descriptor.ObjectId, out assembly))
                assembly = TryCreateAssembly(descriptor, skeleton, source.Closed ? null : source, now);
            if (assembly is null || assembly.Dropped) return null;
            if (!source.Closed) assembly.AddSource(source);
            if (dependent is not null && !assembly.Dependents.Contains(dependent)) assembly.Dependents.Add(dependent);
            assembly.Waiters.Add(waiter);
            Schedule(outbox, now);
        }
        outbox.Flush();
        await using CancellationTokenRegistration registration = cancellationToken.Register(static state =>
            ((TaskCompletionSource<byte[]?>)state!).TrySetResult(null), waiter);
        byte[]? body = await waiter.Task.ConfigureAwait(false);
        if (body is null) ReleaseWaiter(assembly, waiter, dependent);
        return body;
    }

    private void ReleaseWaiter(LeanAssembly assembly, TaskCompletionSource<byte[]?> waiter, LeanAssembly? dependent)
    {
        Outbox outbox = new();
        lock (_gate)
        {
            assembly.Waiters.Remove(waiter);
            if (dependent is not null) assembly.Dependents.Remove(dependent);
            if (assembly.Waiters.Count == 0 && !assembly.Validate && !assembly.Queued && !assembly.Dropped
                && _assemblies.Remove(assembly.Descriptor.ObjectId))
            {
                CancelRequestsFor(assembly, outbox);
                Drop(assembly);
            }
        }
        outbox.Flush();
    }
}
