// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>Serializes proving and shares verified completed steps between aggregation callers.</summary>
public sealed class ProductionProofCache(ILeanProofVerifier verifier) : ILeanProofVerifier
{
    private const int MaxEntries = 64;
    private const long MaxBytes = 32 * 1024 * 1024;
    private readonly Dictionary<ValueHash256, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _recent = [];
    private readonly Lock _cacheLock = new();
    private readonly object _provingLock = new();
    private int _producersWaiting;
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
    public bool VerifyLeanStark(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) =>
        verifier.VerifyLeanStark(in dataHash, in verificationKey, witness);
    /// <inheritdoc/>
    public bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof) =>
        verifier.VerifyRecursiveStark(in depsHash, aggregatedVk, proof);

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
        if (TryGet(key, out byte[]? cached)) return cached!;
        bool entered = false;
        if (request?.Background == true)
        {
            if (Volatile.Read(ref _producersWaiting) != 0 || !Monitor.TryEnter(_provingLock))
                throw new InvalidOperationException("Proof production is busy; retry background aggregation on the next cadence.");
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
            if (request?.Background == true && Volatile.Read(ref _producersWaiting) != 0)
                throw new InvalidOperationException("Proof production is busy; retry background aggregation on the next cadence.");
            if (TryGet(key, out cached)) return cached!;
            // Preserve authenticated work before a caller checks its cancellation or proposal deadline.
            byte[] proof = verifier.ProveRecursiveStark(in statement, aggregatedVk, input);
            if (proof.Length is 0 or > Eip8288Constants.MaxProofBytes
                || !verifier.VerifyRecursiveStark(in statement, aggregatedVk, proof))
                throw new InvalidOperationException("Produced EIP-8288 proof failed verification.");
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
