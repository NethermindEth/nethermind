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
    /// <summary>Full witness bytes a sender may retain through pending transactions.</summary>
    public const long MaxSenderPinnedBytes = 12 * 1024 * 1024;
    private const long MaxStoredBytes = 64 * 1024 * 1024;
    private const int MaxStoredRecords = 1024;
    private readonly object _lock = new();
    private readonly Dictionary<FrameDependency, ProofRecord> _coverage = [];
    private readonly LinkedList<ProofRecord> _records = [];
    private readonly Dictionary<ValueHash256, ProofRecord> _identities = [];
    private readonly Dictionary<ValueHash256, ProofRecord> _recursiveByDeps = [];
    private readonly LinkedList<ProofRecord> _recursiveCache = [];
    private readonly Dictionary<(object? Owner, ValueHash256 Hash), PendingProof> _pending = [];
    private readonly Dictionary<Address, SenderPins> _senders = [];
    private readonly AsyncLocal<AdmissionScope?> _admission = new();
    private long _storedBytes;
    private long _cachedBytes;

    private sealed record PendingProof(Address Sender, ProofRecord[] Records, KeyValuePair<FrameDependency, ProofRecord>[] Coverage);

    private sealed class SenderPins
    {
        public Dictionary<ProofRecord, int> Records { get; } = [];
        public long Bytes { get; set; }
    }

    private sealed class ProofRecord(FrameDependency[] dependencies, byte[][]? witnesses, byte[]? recursiveProof,
        long size, ValueHash256 identity, ValueHash256 dependencyHash, ValueHash256 proofHash)
    {
        public FrameDependency[] Dependencies { get; } = dependencies;
        public byte[][]? Witnesses { get; } = witnesses;
        public byte[]? RecursiveProof { get; } = recursiveProof;
        public long Size { get; } = size;
        public ValueHash256 Identity { get; } = identity;
        public ValueHash256 DependencyHash { get; } = dependencyHash;
        public RecursiveProofInput RecursiveInput { get; } = recursiveProof is null ? default
            : new(Array.AsReadOnly(dependencies), recursiveProof, proofHash);
        public LinkedListNode<ProofRecord>? Node { get; set; }
        public int Pins { get; set; }
        public int CoveredDependencies { get; set; }
    }

    private sealed class RecordLease(LeanProofStore store, ProofRecord[] records, Address? sender = null) : IDisposable
    {
        private int _active = 1;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _active, 0) == 0) return;
            lock (store._lock)
            {
                if (sender is not null) store.ReleaseSender(sender, records);
                foreach (ProofRecord record in records)
                {
                    record.Pins--;
                    if (record.Pins == 0 && record.CoveredDependencies == 0) store.Remove(record);
                }
            }
        }
    }

    private sealed class AdmissionScope(LeanProofStore store, AdmissionScope? previous,
        Dictionary<FrameDependency, ProofRecord> records, IDisposable lease) : IDisposable
    {
        private int _active = 1;
        public bool TryGet(FrameDependency dependency, out ProofRecord? record)
        {
            record = null;
            return Volatile.Read(ref _active) != 0 && records.TryGetValue(dependency, out record);
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _active, 0) == 0) return;
            store._admission.Value = previous;
            lease.Dispose();
        }
    }

    /// <summary>Reserves immutable verified witnesses before transactions enter the pool.</summary>
    public bool TryBeginAdmission(IReadOnlyList<FrameDependency> dependencies, IReadOnlyList<byte[]>? witnesses,
        byte[]? recursiveProof, out IDisposable? admission)
    {
        ValidateShape(dependencies, witnesses, recursiveProof);
        admission = null;
        lock (_lock)
        {
            Dictionary<FrameDependency, ProofRecord> records = [];
            ProofRecord? recursive = null;
            HashSet<ProofRecord> incoming = [];
            for (int i = 0; i < dependencies.Count; i++)
            {
                FrameDependency dependency = dependencies[i];
                if (records.ContainsKey(dependency)) continue;
                if (!_coverage.TryGetValue(dependency, out ProofRecord? record))
                {
                    record = witnesses is null
                        ? recursive ??= CreateRecord(dependencies, null, recursiveProof)
                        : CreateRecord([dependency], [witnesses[i]], null);
                    if (_identities.TryGetValue(record.Identity, out ProofRecord? existing)) record = existing;
                    else incoming.Add(record);
                }
                records[dependency] = record;
            }
            HashSet<ProofRecord> selected = [.. records.Values];
            // Protect existing coverage before finding room for the missing part of this wrapper.
            foreach (ProofRecord record in selected) record.Pins++;
            long incomingBytes = 0;
            foreach (ProofRecord record in incoming) incomingBytes += record.Size;
            if (incoming.Count != 0)
            {
                if (!MakeRoom(incomingBytes, incoming.Count))
                {
                    foreach (ProofRecord record in selected) record.Pins--;
                    return false;
                }
                foreach (ProofRecord record in incoming) Insert(record);
            }
            AdmissionScope scope = new(this, _admission.Value, records, new RecordLease(this, [.. selected]));
            _admission.Value = scope;
            admission = scope;
            return true;
        }
    }

    /// <summary>Protects witnesses throughout transaction filtering and atomic insertion.</summary>
    public bool TryReserveTransaction(IReadOnlyList<FrameDependency> dependencies, out IDisposable? reservation, Address? sender = null)
    {
        reservation = null;
        lock (_lock)
        {
            HashSet<ProofRecord> records = [];
            foreach (FrameDependency dependency in dependencies)
            {
                if (!TryResolve(dependency, out ProofRecord? record)) return false;
                records.Add(record!);
            }
            ProofRecord[] selected = [.. records];
            if (sender is not null && !TryTakeSender(sender, selected)) return false;
            foreach (ProofRecord record in selected) record.Pins++;
            reservation = new RecordLease(this, selected, sender);
            return true;
        }
    }

    /// <summary>Reserves pending witnesses before insertion; publication may be deferred to the insertion event.</summary>
    public bool PinPending(Transaction transaction, object? owner = null, bool publish = true)
    {
        List<FrameDependency> dependencies = Eip8288Dependencies.ForTransaction(transaction);
        if (dependencies.Count == 0) return true;
        if (transaction.Hash is null || transaction.SenderAddress is null) return false;
        lock (_lock)
        {
            (object? Owner, ValueHash256 Hash) key = (owner, transaction.Hash!.ValueHash256);
            if (_pending.ContainsKey(key)) return true;
            HashSet<ProofRecord> records = [];
            Dictionary<FrameDependency, ProofRecord> coverage = [];
            foreach (FrameDependency dependency in dependencies)
            {
                if (!TryResolve(dependency, out ProofRecord? record))
                    return false;
                records.Add(record!);
                coverage.Add(dependency, record!);
            }
            ProofRecord[] selected = [.. records];
            if (!TryTakeSender(transaction.SenderAddress, selected)) return false;
            foreach (ProofRecord record in selected) record.Pins++;
            _pending.Add(key, new(transaction.SenderAddress, selected, [.. coverage]));
            if (publish) PublishPending(key);
            return true;
        }
    }

    /// <summary>Publishes the already reserved dependency set when the pool makes its record visible.</summary>
    public void PublishPending(in ValueHash256 hash, object? owner = null)
    {
        lock (_lock) PublishPending((owner, hash));
    }

    private void PublishPending((object? Owner, ValueHash256 Hash) key)
    {
        if (!_pending.TryGetValue(key, out PendingProof? pending)) return;
        foreach ((FrameDependency dependency, ProofRecord record) in pending.Coverage)
            Publish(dependency, record);
    }

    /// <summary>Called under the owning pool lock for every removal, replacement, and self-eviction.</summary>
    public void UnpinPending(in ValueHash256 hash, object? owner = null)
    {
        lock (_lock)
            if (_pending.Remove((owner, hash), out PendingProof? pending))
            {
                ReleaseSender(pending.Sender, pending.Records);
                foreach (ProofRecord record in pending.Records) record.Pins--;
            }
    }

    /// <summary>Releases all witnesses owned by a pool after its background workers have stopped.</summary>
    public void ReleasePending(object owner)
    {
        lock (_lock)
        {
            List<(object? Owner, ValueHash256 Hash)> owned = [];
            foreach ((object? Owner, ValueHash256 Hash) key in _pending.Keys)
                if (ReferenceEquals(key.Owner, owner)) owned.Add(key);
            foreach ((object? Owner, ValueHash256 Hash) key in owned)
            {
                PendingProof pending = _pending[key];
                _pending.Remove(key);
                ReleaseSender(pending.Sender, pending.Records);
                foreach (ProofRecord record in pending.Records) record.Pins--;
            }
        }
    }

    private bool TryTakeSender(Address sender, ProofRecord[] records)
    {
        if (records.Length == 0) return true;
        _senders.TryGetValue(sender, out SenderPins? pins);
        long bytes = pins?.Bytes ?? 0;
        foreach (ProofRecord record in records)
            if (pins is null || !pins.Records.ContainsKey(record)) bytes += record.Size;
        if (bytes > MaxSenderPinnedBytes) return false;
        if (pins is null) _senders.Add(sender, pins = new());
        foreach (ProofRecord record in records)
        {
            if (pins.Records.TryGetValue(record, out int count)) pins.Records[record] = count + 1;
            else { pins.Records.Add(record, 1); pins.Bytes += record.Size; }
        }
        return true;
    }

    private void ReleaseSender(Address sender, ProofRecord[] records)
    {
        if (records.Length == 0) return;
        SenderPins pins = _senders[sender];
        foreach (ProofRecord record in records)
        {
            int count = pins.Records[record];
            if (count == 1) { pins.Records.Remove(record); pins.Bytes -= record.Size; }
            else pins.Records[record] = count - 1;
        }
        if (pins.Records.Count == 0) _senders.Remove(sender);
    }

    /// <summary>Publishes accepted coverage from the current validated admission snapshot.</summary>
    public void CommitAdmission(IReadOnlyList<FrameDependency> admittedDependencies)
    {
        lock (_lock)
            if (_admission.Value is { } scope)
                foreach (FrameDependency dependency in admittedDependencies)
                    if (scope.TryGet(dependency, out ProofRecord? record)) Publish(dependency, record!);
    }

    /// <summary>Stores verified coverage when capacity permits; pending witnesses are never evicted.</summary>
    public void AddVerified(IReadOnlyList<FrameDependency> dependencies, IReadOnlyList<byte[]>? witnesses, byte[]? recursiveProof,
        IReadOnlyList<FrameDependency>? admittedDependencies = null)
    {
        ValidateShape(dependencies, witnesses, recursiveProof);
        IReadOnlyList<FrameDependency> coverage = admittedDependencies ?? dependencies;
        if (coverage.Count == 0) return;
        HashSet<FrameDependency> declared = [.. dependencies];
        HashSet<FrameDependency> admitted = [.. coverage];
        foreach (FrameDependency dependency in coverage)
            if (!declared.Contains(dependency)) throw new ArgumentException("Admitted dependencies must be covered by the proof.");
        lock (_lock)
        {
            if (HasCoverage(coverage)) return;
            // Active admission already owns immutable snapshots and capacity. Publish only accepted entries.
            if (_admission.Value is { } scope)
            {
                bool reserved = true;
                foreach (FrameDependency dependency in coverage)
                    if (!scope.TryGet(dependency, out _)) { reserved = false; break; }
                if (reserved)
                {
                    foreach (FrameDependency dependency in coverage)
                        if (scope.TryGet(dependency, out ProofRecord? record)) Publish(dependency, record!);
                    return;
                }
            }
            List<ProofRecord> incoming = [];
            Dictionary<FrameDependency, ProofRecord> requested = [];
            ProofRecord? recursive = null;
            for (int i = 0; i < dependencies.Count; i++)
            {
                FrameDependency dependency = dependencies[i];
                if (!admitted.Contains(dependency) || _coverage.ContainsKey(dependency) || requested.ContainsKey(dependency)) continue;
                ProofRecord record = witnesses is null
                    ? recursive ??= CreateRecord(dependencies, null, recursiveProof)
                    : CreateRecord([dependency], [witnesses[i]], null);
                if (_identities.TryGetValue(record.Identity, out ProofRecord? stored)) record = stored;
                else if (!incoming.Contains(record)) incoming.Add(record);
                requested.Add(dependency, record);
            }
            long bytes = 0;
            foreach (ProofRecord record in incoming) bytes += record.Size;
            // Existing requested records must remain available while unpinned cache entries make room.
            foreach (ProofRecord record in requested.Values) record.Pins++;
            bool fits = incoming.Count == 0 || MakeRoom(bytes, incoming.Count);
            if (fits)
            {
                foreach (ProofRecord record in incoming) Insert(record);
                foreach ((FrameDependency dependency, ProofRecord record) in requested) Publish(dependency, record);
            }
            foreach (ProofRecord record in requested.Values) record.Pins--;
        }
    }

    /// <summary>Caches generated recursive proofs separately from required pool witnesses.</summary>
    public void AddCachedRecursive(IReadOnlyList<FrameDependency> dependencies, byte[] proof)
    {
        ValidateShape(dependencies, null, proof);
        ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash(dependencies);
        lock (_lock)
        {
            if (_recursiveByDeps.TryGetValue(hash, out ProofRecord? cached))
            {
                _recursiveCache.Remove(cached.Node!);
                cached.Node = _recursiveCache.AddLast(cached);
                return;
            }
            ProofRecord record = CreateRecord(dependencies, null, proof);
            record.Node = _recursiveCache.AddLast(record);
            _recursiveByDeps.Add(hash, record);
            _cachedBytes += record.Size;
            while (_cachedBytes > MaxStoredBytes || _recursiveCache.Count > MaxStoredRecords)
            {
                ProofRecord oldest = _recursiveCache.First!.Value;
                _recursiveCache.RemoveFirst();
                _recursiveByDeps.Remove(oldest.DependencyHash);
                _cachedBytes -= oldest.Size;
            }
        }
    }

    private bool TryResolve(FrameDependency dependency, out ProofRecord? record) =>
        _coverage.TryGetValue(dependency, out record) || _admission.Value?.TryGet(dependency, out record) == true;

    private void Publish(FrameDependency dependency, ProofRecord record)
    {
        if (_coverage.TryGetValue(dependency, out ProofRecord? previous))
        {
            if (previous.Pins != 0 || ReferenceEquals(previous, record)) return;
            previous.CoveredDependencies--;
        }
        _coverage[dependency] = record;
        record.CoveredDependencies++;
    }

    private void Insert(ProofRecord record)
    {
        record.Node = _records.AddLast(record);
        _identities.Add(record.Identity, record);
        _storedBytes += record.Size;
    }

    private bool MakeRoom(long size, int records = 1)
    {
        for (LinkedListNode<ProofRecord>? node = _records.First;
            _storedBytes + size > MaxStoredBytes || _records.Count + records > MaxStoredRecords;)
        {
            if (node is null) return false;
            LinkedListNode<ProofRecord>? next = node.Next;
            if (node.Value.Pins == 0) Remove(node.Value);
            node = next;
        }
        return true;
    }

    private void Remove(ProofRecord record)
    {
        if (record.Node is null) return;
        _records.Remove(record.Node);
        record.Node = null;
        _identities.Remove(record.Identity);
        _storedBytes -= record.Size;
        foreach (FrameDependency dependency in record.Dependencies)
            if (_coverage.TryGetValue(dependency, out ProofRecord? current) && ReferenceEquals(current, record)) _coverage.Remove(dependency);
    }

    private static void ValidateShape(IReadOnlyList<FrameDependency> dependencies, IReadOnlyList<byte[]>? witnesses, byte[]? recursiveProof)
    {
        if ((witnesses is null) == (recursiveProof is null) || witnesses is not null && witnesses.Count != dependencies.Count)
            throw new ArgumentException("Exactly one proof form must cover the dependencies.");
        long size = (long)dependencies.Count * Eip8288Constants.DependencyTripleLength + (recursiveProof?.Length ?? 0);
        if (witnesses is not null)
            foreach (byte[] witness in witnesses) size += witness.Length;
        if (dependencies.Count > Eip8288Constants.MaxProofDependencies || size > MaxWrapperBytes)
            throw new ArgumentException("Wrapper exceeds the proof storage limit.");
    }

    private static ProofRecord CreateRecord(IReadOnlyList<FrameDependency> dependencies, IReadOnlyList<byte[]>? witnesses, byte[]? recursiveProof)
    {
        FrameDependency[] deps = [.. dependencies];
        byte[][]? copies = witnesses is null ? null : new byte[witnesses.Count][];
        byte[]? recursive = recursiveProof is null ? null : (byte[])recursiveProof.Clone();
        ValueHash256 dependencyHash = Eip8288Dependencies.ComputeDepsHash(deps);
        ValueHash256 proofHash = recursive is null ? default : ValueKeccak.Compute(recursive);
        byte[] dependencyBytes = Eip8288Dependencies.Serialize(deps);
        byte[] identityBytes = new byte[dependencyBytes.Length + 1 + (witnesses?.Count ?? 1) * Hash256.Size];
        dependencyBytes.CopyTo(identityBytes, 0);
        identityBytes[dependencyBytes.Length] = recursive is null ? (byte)0 : (byte)1;
        long size = dependencyBytes.Length + (recursive?.Length ?? 0);
        if (copies is not null)
            for (int i = 0; i < copies.Length; i++)
            {
                copies[i] = (byte[])witnesses![i].Clone();
                size += copies[i].Length;
                ValueHash256 witnessHash = ValueKeccak.Compute(copies[i]);
                witnessHash.Bytes.CopyTo(identityBytes.AsSpan(dependencyBytes.Length + 1 + i * Hash256.Size));
            }
        else proofHash.Bytes.CopyTo(identityBytes.AsSpan(dependencyBytes.Length + 1));
        return new(deps, copies, recursive, size, ValueKeccak.Compute(identityBytes), dependencyHash, proofHash);
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
        lock (_lock)
            foreach (FrameDependency dependency in dependencies)
                if (!TryResolve(dependency, out _)) return false;
        return true;
    }

    /// <summary>Retrieves an already verified recursive proof for the exact canonical dependency set.</summary>
    public bool TryGetRecursiveProof(IReadOnlyList<FrameDependency> dependencies, out byte[]? proof)
    {
        ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash(dependencies);
        lock (_lock)
        {
            if (_recursiveByDeps.TryGetValue(hash, out ProofRecord? cached))
            {
                _recursiveCache.Remove(cached.Node!);
                cached.Node = _recursiveCache.AddLast(cached);
                proof = (byte[])cached.RecursiveProof!.Clone();
                return true;
            }
            foreach (FrameDependency dependency in dependencies)
                if (_coverage.TryGetValue(dependency, out ProofRecord? record) && record.RecursiveProof is not null && record.DependencyHash == hash)
                {
                    proof = (byte[])record.RecursiveProof.Clone();
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
