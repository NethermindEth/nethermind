// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>Retains verified production steps completed after a caller's improvement deadline.</summary>
/// <remarks>Owned by one serialized block processor; only immutable production input snapshots enter this cache.</remarks>
internal sealed class ProductionProofCache(ILeanProofVerifier verifier) : ILeanProofVerifier
{
    private const int MaxEntries = 64;
    private const long MaxBytes = 32 * 1024 * 1024;
    private readonly Dictionary<ValueHash256, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _recent = [];
    private long _bytes;

    private sealed record Entry(ValueHash256 Key, byte[] Proof);

    public void EnsureAvailable() => verifier.EnsureAvailable();
    public bool VerifyLeanSphincs(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) =>
        verifier.VerifyLeanSphincs(in dataHash, in verificationKey, witness);
    public bool VerifyLeanStark(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) =>
        verifier.VerifyLeanStark(in dataHash, in verificationKey, witness);
    public bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof) =>
        verifier.VerifyRecursiveStark(in depsHash, aggregatedVk, proof);

    public byte[] ProveRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input)
    {
        ValueHash256 key = Key(in depsHash, aggregatedVk, input);
        if (_entries.TryGetValue(key, out LinkedListNode<Entry>? cached))
        {
            _recent.Remove(cached);
            _recent.AddLast(cached);
            return (byte[])cached.Value.Proof.Clone();
        }

        // Native proving cannot be interrupted. Preserve useful authenticated work before the caller checks cancellation.
        byte[] proof = verifier.ProveRecursiveStark(in depsHash, aggregatedVk, input);
        if (proof.Length is 0 or > Eip8288Constants.MaxProofBytes
            || !verifier.VerifyRecursiveStark(in depsHash, aggregatedVk, proof))
            throw new InvalidOperationException("Produced EIP-8288 proof failed verification.");
        byte[] owned = (byte[])proof.Clone();
        while (_entries.Count >= MaxEntries || _bytes + owned.Length > MaxBytes)
        {
            LinkedListNode<Entry> oldest = _recent.First!;
            _bytes -= oldest.Value.Proof.Length;
            _entries.Remove(oldest.Value.Key);
            _recent.RemoveFirst();
        }
        _entries.Add(key, _recent.AddLast(new Entry(key, owned)));
        _bytes += owned.Length;
        return proof;
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
