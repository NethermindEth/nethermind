// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Core.Crypto;

/// <summary>Bounded, thread-safe storage of verified dependency witnesses for block production.</summary>
public sealed class LeanProofStore
{
    public const int MaxWrapperBytes = 16 * 1024 * 1024;
    public const int MaxWrapperTransactions = 4096;
    private const long MaxStoredBytes = 64 * 1024 * 1024;
    private const int MaxStoredWrappers = 1024;
    private readonly object _lock = new();
    private readonly Dictionary<FrameDependency, ProofRecord> _coverage = [];
    private readonly Queue<ProofRecord> _records = [];
    private long _storedBytes;

    private sealed record ProofRecord(FrameDependency[] Dependencies, byte[][]? Witnesses, byte[]? RecursiveProof, long Size);

    /// <summary>Stores an already verified wrapper, copying its mutable witness buffers.</summary>
    public void AddVerified(IReadOnlyList<FrameDependency> dependencies, IReadOnlyList<byte[]>? witnesses, byte[]? recursiveProof)
    {
        if ((witnesses is null) == (recursiveProof is null) || witnesses is not null && witnesses.Count != dependencies.Count)
            throw new ArgumentException("Exactly one proof form must cover the dependencies.");
        if (dependencies.Count == 0) return;
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
        ProofRecord record = new(deps, copiedWitnesses, recursiveProof is null ? null : (byte[])recursiveProof.Clone(), size);
        lock (_lock)
        {
            _records.Enqueue(record);
            _storedBytes += size;
            foreach (FrameDependency dep in deps) _coverage[dep] = record;
            while (_storedBytes > MaxStoredBytes || _records.Count > MaxStoredWrappers)
            {
                ProofRecord oldest = _records.Dequeue();
                _storedBytes -= oldest.Size;
                foreach (FrameDependency dep in oldest.Dependencies)
                    if (_coverage.TryGetValue(dep, out ProofRecord? existing) && ReferenceEquals(existing, oldest)) _coverage.Remove(dep);
            }
        }
    }

    /// <summary>Checks whether every dependency of a candidate transaction has a verified witness.</summary>
    public bool Covers(Transaction transaction)
    {
        lock (_lock)
            foreach (FrameDependency dep in Eip8288Dependencies.ForTransaction(transaction))
                if (!_coverage.ContainsKey(dep)) return false;
        return true;
    }

    /// <summary>Collects direct and recursive witnesses, discarding dependencies outside the requested set.</summary>
    public bool TryGetInput(IReadOnlyList<FrameDependency> dependencies, out AggregationInput input)
    {
        input = new();
        HashSet<FrameDependency> required = [.. dependencies];
        List<FrameDependency> directDeps = [];
        List<byte[]> witnesses = [];
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
                    witnesses.Add((byte[])record.Witnesses[index].Clone());
                }
                else if (selected.Add(record))
                {
                    recursive.Add(new RecursiveProofInput((FrameDependency[])record.Dependencies.Clone(), (byte[])record.RecursiveProof!.Clone()));
                    foreach (FrameDependency nestedDep in record.Dependencies)
                        if (!required.Contains(nestedDep)) discards.Add(nestedDep);
                }
            }
        }
        input = new() { Deps = directDeps, Witnesses = witnesses, RecursiveProofs = recursive, Discards = [.. discards] };
        return true;
    }
}
