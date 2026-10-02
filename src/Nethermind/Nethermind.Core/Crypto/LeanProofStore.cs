// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;

namespace Nethermind.Core.Crypto;

/// <summary>Bounded, thread-safe storage of verified dependency witnesses for block production.</summary>
public sealed class LeanProofStore
{
    // Leave room for Snappy's worst-case expansion within the 16 MiB inbound frame cap.
    public const int MaxWrapperBytes = 10 * 1024 * 1024;
    public const int MaxWrapperTransactions = 4096;
    private const long MaxStoredBytes = 64 * 1024 * 1024;
    private const int MaxStoredWrappers = 1024;
    private readonly object _lock = new();
    private readonly Dictionary<FrameDependency, ProofRecord> _coverage = [];
    private readonly Queue<ProofRecord> _records = [];
    private readonly Dictionary<ValueHash256, ProofRecord> _identities = [];
    private readonly Dictionary<ValueHash256, ProofRecord> _recursiveByDeps = [];
    private readonly AsyncLocal<AdmissionScope?> _admission = new();
    private long _storedBytes;

    private sealed record ProofRecord(FrameDependency[] Dependencies, byte[][]? Witnesses, byte[]? RecursiveProof,
        long Size, ValueHash256 Identity, ValueHash256 DependencyHash, ValueHash256 ProofHash)
    {
        public RecursiveProofInput RecursiveInput { get; } = RecursiveProof is null ? default
            : new(Array.AsReadOnly(Dependencies), RecursiveProof, ProofHash);
    }

    private sealed class AdmissionScope(LeanProofStore store, AdmissionScope? previous, IReadOnlyList<FrameDependency> dependencies) : IDisposable
    {
        private readonly HashSet<FrameDependency> _dependencies = [.. dependencies];
        private int _active = 1;
        public bool Contains(FrameDependency dependency) => Volatile.Read(ref _active) != 0 && _dependencies.Contains(dependency);
        public void Dispose()
        {
            Interlocked.Exchange(ref _active, 0);
            store._admission.Value = previous;
        }
    }

    /// <summary>Temporarily authorizes verified dependencies during synchronous pool admission.</summary>
    public IDisposable BeginAdmission(IReadOnlyList<FrameDependency> dependencies)
    {
        AdmissionScope scope = new(this, _admission.Value, dependencies);
        _admission.Value = scope;
        return scope;
    }

    /// <summary>Stores an already verified wrapper, copying its mutable witness buffers.</summary>
    public void AddVerified(IReadOnlyList<FrameDependency> dependencies, IReadOnlyList<byte[]>? witnesses, byte[]? recursiveProof,
        IReadOnlyList<FrameDependency>? admittedDependencies = null)
    {
        if ((witnesses is null) == (recursiveProof is null) || witnesses is not null && witnesses.Count != dependencies.Count)
            throw new ArgumentException("Exactly one proof form must cover the dependencies.");
        if (dependencies.Count == 0 || admittedDependencies is { Count: 0 }) return;
        long expectedSize = (long)dependencies.Count * Eip8288Constants.DependencyTripleLength + (recursiveProof?.Length ?? 0);
        if (witnesses is not null)
            foreach (byte[] witness in witnesses) expectedSize += witness.Length;
        if (dependencies.Count > Eip8288Constants.MaxProofDependencies || expectedSize > MaxWrapperBytes)
            throw new ArgumentException("Wrapper exceeds the proof storage limit.");
        if (admittedDependencies is not null)
        {
            HashSet<FrameDependency> declared = [.. dependencies];
            foreach (FrameDependency dependency in admittedDependencies)
                if (!declared.Contains(dependency)) throw new ArgumentException("Admitted dependencies must be covered by the proof.");
        }
        IReadOnlyList<FrameDependency> coverage = admittedDependencies ?? dependencies;
        lock (_lock)
        {
            if (admittedDependencies is not null && HasCoverage(coverage)) return;
        }
        byte[] dependencyBytes = Eip8288Dependencies.Serialize(dependencies);
        ValueHash256 dependencyHash = Eip8288Dependencies.ComputeDepsHash(dependencies);
        ValueHash256 proofHash = recursiveProof is null ? default : ValueKeccak.Compute(recursiveProof);
        byte[] identityBytes = new byte[dependencyBytes.Length + 1 + (witnesses?.Count ?? 1) * Hash256.Size];
        dependencyBytes.CopyTo(identityBytes, 0);
        identityBytes[dependencyBytes.Length] = recursiveProof is null ? (byte)0 : (byte)1;
        if (witnesses is not null)
            for (int i = 0; i < witnesses.Count; i++)
            {
                ValueHash256 witnessHash = ValueKeccak.Compute(witnesses[i]);
                witnessHash.Bytes.CopyTo(identityBytes.AsSpan(dependencyBytes.Length + 1 + i * Hash256.Size));
            }
        else proofHash.Bytes.CopyTo(identityBytes.AsSpan(dependencyBytes.Length + 1));
        ValueHash256 identity = ValueKeccak.Compute(identityBytes);
        lock (_lock)
        {
            if (HasCoverage(coverage) && (recursiveProof is null || admittedDependencies is not null || _recursiveByDeps.ContainsKey(dependencyHash))) return;
            if (_identities.TryGetValue(identity, out ProofRecord? existing))
            {
                foreach (FrameDependency dep in coverage) _coverage[dep] = existing;
                return;
            }
        }
        FrameDependency[] deps = new FrameDependency[dependencies.Count];
        byte[][]? copiedWitnesses = witnesses is null ? null : new byte[witnesses.Count][];
        long size = (long)dependencies.Count * Eip8288Constants.DependencyTripleLength + (recursiveProof?.Length ?? 0);
        for (int i = 0; i < deps.Length; i++)
        {
            deps[i] = dependencies[i];
            if (copiedWitnesses is not null)
            {
                size += witnesses![i].Length;
                if (size > MaxWrapperBytes) throw new ArgumentException("Wrapper exceeds the proof storage limit.");
                copiedWitnesses[i] = (byte[])witnesses[i].Clone();
            }
        }
        if (size > MaxWrapperBytes) throw new ArgumentException("Wrapper exceeds the proof storage limit.");
        ProofRecord record = new(deps, copiedWitnesses, recursiveProof is null ? null : (byte[])recursiveProof.Clone(), size,
            identity, dependencyHash, proofHash);
        lock (_lock)
        {
            if (HasCoverage(coverage) && (recursiveProof is null || admittedDependencies is not null || _recursiveByDeps.ContainsKey(dependencyHash))) return;
            if (_identities.TryGetValue(identity, out ProofRecord? existing))
            {
                foreach (FrameDependency dep in coverage) _coverage[dep] = existing;
                return;
            }
            _records.Enqueue(record);
            _identities.Add(identity, record);
            if (recursiveProof is not null) _recursiveByDeps[dependencyHash] = record;
            _storedBytes += size;
            foreach (FrameDependency dep in coverage) _coverage[dep] = record;
            while (_storedBytes > MaxStoredBytes || _records.Count > MaxStoredWrappers)
            {
                ProofRecord oldest = _records.Dequeue();
                _storedBytes -= oldest.Size;
                _identities.Remove(oldest.Identity);
                if (_recursiveByDeps.TryGetValue(oldest.DependencyHash, out ProofRecord? cached) && ReferenceEquals(cached, oldest))
                    _recursiveByDeps.Remove(oldest.DependencyHash);
                foreach (FrameDependency dep in oldest.Dependencies)
                    if (_coverage.TryGetValue(dep, out ProofRecord? currentCoverage) && ReferenceEquals(currentCoverage, oldest)) _coverage.Remove(dep);
            }
        }
    }

    private bool HasCoverage(IReadOnlyList<FrameDependency> dependencies)
    {
        foreach (FrameDependency dependency in dependencies)
            if (!_coverage.ContainsKey(dependency)) return false;
        return true;
    }

    /// <summary>Checks whether every dependency of a candidate transaction has a verified witness.</summary>
    public bool Covers(Transaction transaction)
    {
        List<FrameDependency> dependencies = Eip8288Dependencies.ForTransaction(transaction);
        AdmissionScope? admission = _admission.Value;
        lock (_lock)
            foreach (FrameDependency dep in dependencies)
                if (admission?.Contains(dep) != true && !_coverage.ContainsKey(dep)) return false;
        return true;
    }

    /// <summary>Retrieves an already verified recursive proof for the exact canonical dependency set.</summary>
    public bool TryGetRecursiveProof(IReadOnlyList<FrameDependency> dependencies, out byte[]? proof)
    {
        ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash(dependencies);
        lock (_lock)
        {
            if (_recursiveByDeps.TryGetValue(hash, out ProofRecord? record))
            {
                proof = (byte[])record.RecursiveProof!.Clone();
                return true;
            }
        }
        proof = null;
        return false;
    }

    /// <summary>Collects direct and recursive witnesses, discarding dependencies outside the requested set.</summary>
    public bool TryGetInput(IReadOnlyList<FrameDependency> dependencies, out AggregationInput input)
    {
        input = new();
        HashSet<FrameDependency> required = [.. dependencies];
        List<FrameDependency> directDeps = [];
        List<ReadOnlyMemory<byte>> witnesses = [];
        List<RecursiveProofInput> recursive = [];
        HashSet<ProofRecord> selected = [];
        HashSet<FrameDependency> discards = [];
        lock (_lock)
        {
            foreach (FrameDependency dep in required)
            {
                if (!_coverage.TryGetValue(dep, out ProofRecord? record)) return false;
                if (record.Witnesses is not null)
                {
                    int index = Array.IndexOf(record.Dependencies, dep);
                    directDeps.Add(dep);
                    witnesses.Add(record.Witnesses[index]);
                }
                else if (selected.Add(record))
                {
                    recursive.Add(record.RecursiveInput);
                    foreach (FrameDependency nestedDep in record.Dependencies)
                        if (!required.Contains(nestedDep)) discards.Add(nestedDep);
                }
            }
        }
        input = new() { Deps = directDeps, Witnesses = witnesses, RecursiveProofs = recursive, Discards = [.. discards] };
        return true;
    }
}
