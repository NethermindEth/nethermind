// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Metrics = Nethermind.Blockchain.Metrics;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>Serializes proving and shares verified completed steps between aggregation callers.</summary>
public sealed class ProductionProofCache(ILeanProofVerifier verifier, ILogManager? logManager = null) : ILeanProofVerifier
{
    // This type is reachable from the zkVM guest, which rejects floating-point instructions.
    private static long ElapsedMilliseconds(long start) =>
        (long)((Int128)(Stopwatch.GetTimestamp() - start) * 1000 / Stopwatch.Frequency);

    private readonly ILogger _logger = (logManager ?? NullLogManager.Instance).GetClassLogger<ProductionProofCache>();
    private const int MaxEntries = 64;
    private const long MaxBytes = 32 * 1024 * 1024;
    private readonly Dictionary<ValueHash256, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _recent = [];
    private readonly Lock _cacheLock = new();
    private readonly object _provingLock = new();
    private int _producersWaiting;
    private Task? _scheduled;
    private CancellationTokenSource? _scheduledCancellation;
    private HashSet<FrameDependency>? _scheduledDeps;
    private (ulong Timestamp, ulong Number) _latestProduction;

    /// <summary>How far behind the latest recorded production a pass may start before that record is taken as bogus, such as
    /// a payload with a far-future timestamp, and replaced.</summary>
    private const ulong MaxProductionRegressSeconds = 32 * 12;
    private const int MaxVerified = 256;
    private readonly Dictionary<ValueHash256, LinkedListNode<ValueHash256>> _verified = [];
    private readonly LinkedList<ValueHash256> _verifiedOrder = [];
    private static readonly AsyncLocal<Request?> CurrentRequest = new();
    private long _bytes;

    private sealed record Entry(ValueHash256 Key, byte[] Proof);
    private sealed class Request(CancellationToken cancellation, bool background)
    {
        public CancellationToken Cancellation { get; } = cancellation;
        public bool Background { get; } = background;
        public volatile bool Active = true;
    }
    private sealed class RequestScope(Request request, Request? previous) : IDisposable
    {
        public void Dispose()
        {
            request.Active = false;
            CurrentRequest.Value = previous;
        }
    }

    internal static IDisposable Background(CancellationToken cancellationToken)
    {
        Request? previous = CurrentRequest.Value;
        Request request = new(cancellationToken, true);
        CurrentRequest.Value = request;
        return new RequestScope(request, previous);
    }

    internal static byte[] Prove(ILeanProofVerifier verifier, in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk,
        AggregationInput input, CancellationToken cancellationToken)
    {
        Request? previous = CurrentRequest.Value;
        Request request = new(cancellationToken, previous is { Active: true, Background: true });
        CurrentRequest.Value = request;
        try { return verifier.ProveRecursiveStark(in depsHash, aggregatedVk, input); }
        finally
        {
            request.Active = false;
            CurrentRequest.Value = previous;
        }
    }

    /// <inheritdoc/>
    public void EnsureAvailable() => verifier.EnsureAvailable();
    /// <inheritdoc/>
    public bool VerifyLeanSphincs(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) =>
        verifier.VerifyLeanSphincs(in dataHash, in verificationKey, witness);
    /// <inheritdoc/>
    /// <remarks>
    /// A recursive proof that verified once is accepted again without the native call: a produced block is otherwise
    /// verified when produced and again on import, and identical proofs arrive repeatedly in wrappers and blocks.
    /// </remarks>
    public bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof)
    {
        ValueHash256 key = VerifiedKey(in depsHash, aggregatedVk, proof);
        lock (_cacheLock)
        {
            if (_verified.TryGetValue(key, out LinkedListNode<ValueHash256>? node))
            {
                _verifiedOrder.Remove(node);
                _verifiedOrder.AddLast(node);
                Interlocked.Increment(ref Metrics.LeanVerificationCacheHits);
                return true;
            }
        }
        if (!verifier.VerifyRecursiveStark(in depsHash, aggregatedVk, proof)) return false;
        RememberVerified(key);
        return true;
    }

    private void RememberVerified(in ValueHash256 key)
    {
        lock (_cacheLock)
        {
            if (_verified.ContainsKey(key)) return;
            if (_verified.Count >= MaxVerified)
            {
                _verified.Remove(_verifiedOrder.First!.Value);
                _verifiedOrder.RemoveFirst();
            }
            _verified.Add(key, _verifiedOrder.AddLast(key));
        }
    }

    private static ValueHash256 VerifiedKey(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(depsHash.Bytes);
        Span<byte> length = stackalloc byte[4];
        BitConverter.TryWriteBytes(length, aggregatedVk.Length);
        hash.AppendData(length);
        hash.AppendData(aggregatedVk);
        hash.AppendData(proof);
        Span<byte> digest = stackalloc byte[32];
        hash.GetHashAndReset(digest);
        return new ValueHash256(digest);
    }

    /// <inheritdoc/>
    public byte[] ProveRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input)
    {
        Request? request = CurrentRequest.Value;
        if (request?.Active != true) request = null;
        CancellationToken cancellationToken = request?.Cancellation ?? default;
        cancellationToken.ThrowIfCancellationRequested();
        ValueHash256 statement = depsHash;
        ArgumentNullException.ThrowIfNull(input);
        if (input.Deps.Count != input.Witnesses.Count || input.Deps.Count > Eip8288Constants.MaxProofDependencies
            || input.Discards.Count > Eip8288Constants.MaxProofDependencies || input.RecursiveProofs.Count > Eip8288Constants.MaxProofDependencies)
            throw new ArgumentException("Invalid aggregation input shape.", nameof(input));
        foreach (RecursiveProofInput parent in input.RecursiveProofs)
            if (parent.InnerDeps is null || parent.InnerDeps.Count > Eip8288Constants.MaxProofDependencies)
                throw new ArgumentException("Invalid recursive aggregation input.", nameof(input));
        if (RecursiveStarkAggregator.InputSize(input) > Eip8288Constants.MaxAggregationInputBytes)
            throw new ArgumentException("Aggregation input exceeds the proving bound.", nameof(input));
        input = Snapshot(input);
        byte[] keyBytes = aggregatedVk.ToArray();
        aggregatedVk = keyBytes;
        ValueHash256 key = Key(in statement, aggregatedVk, input);
        if (TryGet(key, out byte[]? cached))
        {
            Interlocked.Increment(ref Metrics.LeanProofCacheHits);
            return cached!;
        }
        bool background = request?.Background == true;
        if (background || !cancellationToken.CanBeCanceled)
            return ProveExclusive(key, statement, keyBytes, input, background, cancellationToken);
        Task<byte[]> proving = Task.Factory.StartNew(() => ProveExclusive(key, statement, keyBytes, input, false, cancellationToken),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try { return proving.WaitAsync(cancellationToken).GetAwaiter().GetResult(); }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref Metrics.LeanDetachedProofs);
            _ = proving.ContinueWith(static task => task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
    }

    /// <summary>Whether a scheduled production statement is still being proven.</summary>
    internal bool IsScheduled => Volatile.Read(ref _scheduled) is { IsCompleted: false };

    /// <summary>Whether a scheduled statement is still being proven and every dependency it covers is in
    /// <paramref name="needed"/>, so its proof stays useful to a body that needs them.</summary>
    internal bool IsScheduledWithin(IReadOnlySet<FrameDependency> needed)
    {
        lock (_cacheLock) return _scheduled is { IsCompleted: false } && _scheduledDeps!.IsSubsetOf(needed);
    }

    /// <summary>Records a production pass and reports whether no pass for a later slot or a higher block has started.</summary>
    /// <remarks>
    /// Payloads keep improving after the next slot's payload started, and a proposer also prepares the next slot on the
    /// parent of the block it just proposed. A body built on a superseded parent omits the senders whose transactions its
    /// successor included, so only the latest payload's bodies are worth proving.
    /// </remarks>
    internal bool ObserveProduction(ulong timestamp, ulong number)
    {
        lock (_cacheLock)
        {
            bool bogus = _latestProduction.Timestamp > timestamp && _latestProduction.Timestamp - timestamp > MaxProductionRegressSeconds;
            if (!bogus && (timestamp < _latestProduction.Timestamp
                || timestamp == _latestProduction.Timestamp && number < _latestProduction.Number)) return false;
            _latestProduction = (timestamp, number);
        }
        return true;
    }

    /// <summary>Proves a production statement off the deadline-bound path and publishes it for exact reuse.</summary>
    /// <remarks>
    /// At most one statement runs at a time. The statement is proven with producer priority, so background wrapper aggregation
    /// yields to it. A later slot alone does not cancel it: a statement that takes longer than a slot would otherwise never
    /// finish while dependencies keep arriving, whereas a finished one is narrowed or extended by later passes. A newer statement
    /// supersedes it, cancelling it between fold steps; callers schedule one only once the latest body no longer needs every
    /// dependency the running one covers (<see cref="IsScheduledWithin"/>).
    /// </remarks>
    internal void Schedule(IReadOnlyList<FrameDependency> deps, ValueHash256 depsHash, AggregationInput input, Eip8288Verifier forkVerifier,
        LeanProofStore store)
    {
        CancellationTokenSource cancellation = new();
        CancellationTokenSource? superseded;
        lock (_cacheLock)
        {
            superseded = _scheduled is { IsCompleted: false } ? _scheduledCancellation : null;
            _scheduledCancellation = cancellation;
            _scheduledDeps = [.. deps];
            // The caller's proving scope, if any, must not decide this statement's priority or cancellation.
            using (ExecutionContext.SuppressFlow())
                _scheduled = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        // Cancellation callbacks may run inline, so they run outside the lock.
        if (superseded is not null)
        {
            try { superseded.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        if (_logger.IsDebug)
            _logger.Debug($"Scheduled EIP-8288 production proof for {deps.Count} dependencies: {input.Deps.Count} direct, " +
                $"{input.RecursiveProofs.Count} recursive, {input.Discards.Count} discarded{(superseded is null ? "" : ", superseding the running one")}");

        void Run()
        {
            try
            {
                // A started native call still finishes and stays cached when superseded.
                byte[] proof = RecursiveStarkAggregator.Prove(input, this, forkVerifier, in depsHash, cancellation.Token);
                store.AddCachedRecursive(deps, proof);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                if (_logger.IsDebug) _logger.Debug("Scheduled EIP-8288 production proof superseded by a newer statement");
            }
            catch (Exception exception)
            {
                if (_logger.IsWarn) _logger.Warn($"Scheduled EIP-8288 production proof failed: {exception.Message}");
            }
            finally
            {
                lock (_cacheLock)
                    if (_scheduledCancellation == cancellation) _scheduledCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    /// <summary>Runs one gated native proof and caches its verified result.</summary>
    /// <remarks>
    /// A canceled producer returns while its started native call finishes here, so the noninterruptible call
    /// holds only the proving gate, not the caller's block production; the verified proof stays cached for a retry.
    /// </remarks>
    private byte[] ProveExclusive(ValueHash256 key, ValueHash256 statement, byte[] aggregatedVk, AggregationInput input,
        bool background, CancellationToken cancellationToken)
    {
        bool entered = false;
        if (background)
        {
            if (Volatile.Read(ref _producersWaiting) != 0 || !Monitor.TryEnter(_provingLock))
                throw BackgroundBusy();
            entered = true;
        }
        else
        {
            Interlocked.Increment(ref _producersWaiting);
            try
            {
                while (!(entered = Monitor.TryEnter(_provingLock, 50))) cancellationToken.ThrowIfCancellationRequested();
            }
            finally { Interlocked.Decrement(ref _producersWaiting); }
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (background && Volatile.Read(ref _producersWaiting) != 0)
                throw BackgroundBusy();
            if (TryGet(key, out byte[]? cached))
            {
                Interlocked.Increment(ref Metrics.LeanProofCacheHits);
                return cached!;
            }
            // Preserve authenticated work before a caller checks its cancellation or proposal deadline.
            long started = Stopwatch.GetTimestamp();
            byte[] proof = verifier.ProveRecursiveStark(in statement, aggregatedVk, input);
            long elapsed = ElapsedMilliseconds(started);
            Interlocked.Increment(ref Metrics.LeanNativeProofs);
            Interlocked.Add(ref Metrics.LeanNativeProveMilliseconds, elapsed);
            Metrics.LeanNativeProveLastMilliseconds = elapsed;
            started = Stopwatch.GetTimestamp();
            bool valid = proof.Length is > 0 and <= Eip8288Constants.MaxAggregateProofBytes
                && verifier.VerifyRecursiveStark(in statement, aggregatedVk, proof);
            if (_logger.IsInfo)
                _logger.Info($"Lean native {(background ? "background" : "production")} proof: prove {elapsed} ms, verify " +
                    $"{ElapsedMilliseconds(started)} ms, {proof.Length} bytes; {input.Deps.Count} direct, " +
                    $"{input.RecursiveProofs.Count} recursive, {input.Discards.Count} discarded{(cancellationToken.IsCancellationRequested ? ", caller canceled" : "")}");
            if (!valid) throw new InvalidOperationException("Produced EIP-8288 proof failed verification.");
            RememberVerified(VerifiedKey(in statement, aggregatedVk, proof));
            byte[] owned = (byte[])proof.Clone();
            lock (_cacheLock)
            {
                while (_entries.Count >= MaxEntries || _bytes + owned.Length > MaxBytes)
                {
                    LinkedListNode<Entry> oldest = _recent.First!;
                    _bytes -= oldest.Value.Proof.Length;
                    _entries.Remove(oldest.Value.Key);
                    _recent.RemoveFirst();
                }
                _entries.Add(key, _recent.AddLast(new Entry(key, owned)));
                _bytes += owned.Length;
            }
            return proof;
        }
        finally { if (entered) Monitor.Exit(_provingLock); }
    }

    private static InvalidOperationException BackgroundBusy()
    {
        Interlocked.Increment(ref Metrics.LeanBackgroundProofsSkipped);
        return new InvalidOperationException("Proof production is busy; retry background aggregation on the next cadence.");
    }

    private bool TryGet(ValueHash256 key, out byte[]? proof)
    {
        lock (_cacheLock)
        {
            if (_entries.TryGetValue(key, out LinkedListNode<Entry>? cached))
            {
                _recent.Remove(cached);
                _recent.AddLast(cached);
                proof = (byte[])cached.Value.Proof.Clone();
                return true;
            }
        }
        proof = null;
        return false;
    }

    private static AggregationInput Snapshot(AggregationInput input)
    {
        ReadOnlyMemory<byte>[] witnesses = new ReadOnlyMemory<byte>[input.Witnesses.Count];
        for (int i = 0; i < witnesses.Length; i++) witnesses[i] = input.Witnesses[i].ToArray();
        RecursiveProofInput[] parents = new RecursiveProofInput[input.RecursiveProofs.Count];
        for (int i = 0; i < parents.Length; i++)
            parents[i] = new(input.RecursiveProofs[i].InnerDeps, input.RecursiveProofs[i].Proof.ToArray());
        return new() { Deps = [.. input.Deps], Witnesses = witnesses, RecursiveProofs = parents, Discards = [.. input.Discards] };
    }

    private static ValueHash256 Key(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(depsHash.Bytes);
        Blob(aggregatedVk);
        Count(input.Deps.Count);
        foreach (FrameDependency dependency in input.Deps) Dependency(dependency);
        Count(input.Witnesses.Count);
        foreach (ReadOnlyMemory<byte> witness in input.Witnesses) Blob(witness.Span);
        Count(input.RecursiveProofs.Count);
        foreach (RecursiveProofInput child in input.RecursiveProofs)
        {
            Count(child.InnerDeps.Count);
            foreach (FrameDependency dependency in child.InnerDeps) Dependency(dependency);
            Blob(child.Proof.Span);
        }
        Count(input.Discards.Count);
        foreach (FrameDependency dependency in input.Discards) Dependency(dependency);
        Span<byte> digest = stackalloc byte[32];
        hash.GetHashAndReset(digest);
        return new ValueHash256(digest);

        void Count(int count)
        {
            Span<byte> bytes = stackalloc byte[4];
            BitConverter.TryWriteBytes(bytes, count);
            hash.AppendData(bytes);
        }

        void Blob(ReadOnlySpan<byte> bytes)
        {
            Count(bytes.Length);
            hash.AppendData(bytes);
        }

        void Dependency(FrameDependency dependency)
        {
            Span<byte> bytes = stackalloc byte[Eip8288Constants.DependencyTripleLength];
            dependency.WriteTo(bytes);
            hash.AppendData(bytes);
        }
    }
}
