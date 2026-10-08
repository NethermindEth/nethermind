// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Linq;
using Nethermind.Core.Memory;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt;

/// <summary>An immutable canonical state view composed from snapshot diffs over one persistence snapshot.</summary>
public sealed class PbtReadOnlySnapshotBundle(
    PbtSnapshotPooledList snapshots,
    IPbtPersistence.IReader reader,
    bool recordDetailedMetrics) : RefCountingDisposable
{
    private static readonly StringLabel _readAccountSnapshotLabel = new("account_snapshot");
    private static readonly StringLabel _readAccountPersistenceLabel = new("account_persistence");
    private static readonly StringLabel _readAccountPersistenceNullLabel = new("account_persistence_null");
    private static readonly StringLabel[] _readStorageSnapshotLabels = [new("storage_snapshot"), new("storage_header_snapshot")];
    private static readonly StringLabel[] _readStoragePersistenceLabels = [new("storage_persistence"), new("storage_header_persistence")];
    private static readonly StringLabel[] _readStoragePersistenceNullLabels = [new("storage_persistence_null"), new("storage_header_persistence_null")];
    private static readonly StringLabel[] _readRunSnapshotLabels = [new("storage_run_snapshot"), new("storage_run_header_snapshot")];
    private static readonly StringLabel[] _readRunPersistenceLabels = [new("storage_run_persistence"), new("storage_run_header_persistence")];
    private static readonly StringLabel[] _readRunPersistenceNullLabels = [new("storage_run_persistence_null"), new("storage_run_header_persistence_null")];
    private static readonly StringLabel[] _readNodeGroupSnapshotLabels = [new("node_group_account_snapshot"), new("node_group_code_snapshot"), new("node_group_storage_snapshot")];
    private static readonly StringLabel[] _readNodeGroupSnapshotNullLabels = [new("node_group_account_snapshot_null"), new("node_group_code_snapshot_null"), new("node_group_storage_snapshot_null")];
    private static readonly StringLabel[] _readNodeGroupPersistenceLabels = [new("node_group_account_persistence"), new("node_group_code_persistence"), new("node_group_storage_persistence")];
    private static readonly StringLabel[] _readNodeGroupPersistenceNullLabels = [new("node_group_account_persistence_null"), new("node_group_code_persistence_null"), new("node_group_storage_persistence_null")];
    // The size metric keys on the same label text, so a tier's bytes and its histogram count line up.
    private static readonly string[] _readNodeGroupSnapshotSizeKeys = [.. _readNodeGroupSnapshotLabels.Select(static label => label.Labels[0])];
    private static readonly string[] _readNodeGroupPersistenceSizeKeys = [.. _readNodeGroupPersistenceLabels.Select(static label => label.Labels[0])];
    private static readonly StringLabel _readCodeSnapshotLabel = new("code_snapshot");
    private static readonly StringLabel _readCodePersistenceLabel = new("code_persistence");
    private static readonly StringLabel _readCodePersistenceNullLabel = new("code_persistence_null");

    private bool _isDisposed;
    private readonly PbtSnapshotChain? _chain;
    internal PbtReadOnlySnapshotBundle(PbtSnapshotChain chain, IPbtPersistence.IReader reader, bool recordDetailedMetrics)
        : this(new PbtSnapshotPooledList(0), reader, recordDetailedMetrics) => _chain = chain;
    private int LayerCount => _chain?.Layers.Count ?? snapshots.Count;
    private PbtSnapshot? MemoryLayer(int index) => _chain is null ? snapshots[index] : _chain.Layers[index].Memory;
    private PersistedSnapshots.PbtRetainedSnapshot? RetainedLayer(int index) => _chain?.Layers[index].Retained;

    private bool TryAccount(int index, in ValueHash256 address, out PbtAccount? account) =>
        MemoryLayer(index) is { } memory ? memory.Content.Accounts.TryGetValue(address, out account) : RetainedLayer(index)!.TryGetAccount(address, out account);
    private bool TryCode(int index, in ValueHash256 hash, out CodeInfo? code) =>
        MemoryLayer(index) is { } memory ? memory.Content.Codes.TryGetValue(hash, out code) : RetainedLayer(index)!.TryGetCode(hash, out code);
    private bool TryGroup(int index, PbtStorageNodePath path, out RefCountingMemory? payload) =>
        MemoryLayer(index) is { } memory ? memory.Content.TryGetNodeGroup(path, out payload) : RetainedLayer(index)!.TryGetNodeGroup(path, out payload);
    private bool TryReadSlot<TKey>(int layer, in HashedKey<TKey> key, in ValueHash256 address, int index, out EvmWord value) where TKey : struct, IPbtKey<TKey>
    {
        if (MemoryLayer(layer) is { } memory)
        {
            if (memory.Content.TryGetSlotRun(key, out PackedSlotRun? run)) { value = run!.Get(index); return true; }
            if (memory.Content.SelfDestructedStorageAddresses.ContainsKey(address)) { value = default; return true; }
        }
        else
        {
            PersistedSnapshots.PbtRetainedSnapshot retained = RetainedLayer(layer)!;
            if (retained.TryGetSlotRun(key.Key, out PackedSlotRun? run))
            {
                value = run!.Get(index);
                SlotRun.Return(run);
                return true;
            }
            if (retained.TryGetStorageClear(address, out _)) { value = default; return true; }
        }
        value = default;
        return false;
    }

    private bool TryRentRun<TKey>(int index, in HashedKey<TKey> key, in ValueHash256 address, out PackedSlotRun? run) where TKey : struct, IPbtKey<TKey>
    {
        if (MemoryLayer(index) is { } memory)
        {
            if (memory.Content.TryGetSlotRun(key, out PackedSlotRun? borrowed)) { run = borrowed!.Clone(); return true; }
            if (memory.Content.SelfDestructedStorageAddresses.ContainsKey(address)) { run = SlotRun.Empty; return true; }
        }
        else
        {
            PersistedSnapshots.PbtRetainedSnapshot retained = RetainedLayer(index)!;
            if (retained.TryGetSlotRun(key.Key, out run)) return true;
            if (retained.TryGetStorageClear(address, out _)) { run = SlotRun.Empty; return true; }
        }
        run = null;
        return false;
    }


    public ValueHash256 TreeRoot
    {
        get
        {
            GuardDispose();
            return LayerCount > 0 ? _chain is null ? snapshots[^1].TreeRoot : _chain.Layers[^1].TreeRoot : reader.CurrentRoot;
        }
    }

    /// <summary>Returns a caller-owned group lease, or null when no layer has an entry.</summary>
    internal RefCountingMemory? GetNodeGroup(PbtStorageNodePath groupKey)
    {
        GuardDispose();
        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        for (int index = LayerCount - 1; index >= 0; index--)
        {
            if (TryGroup(index, groupKey, out RefCountingMemory? payload))
            {
                if (recordDetailedMetrics)
                {
                    int partition = (int)PbtPartitions.PartitionOfPath(groupKey);
                    Metrics.PbtReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, (payload is null ? _readNodeGroupSnapshotNullLabels : _readNodeGroupSnapshotLabels)[partition]);
                    if (payload is not null) Metrics.PbtReadOnlySnapshotBundleNodeGroupBytes.AddBy(_readNodeGroupSnapshotSizeKeys[partition], payload.GetSpan().Length);
                }
                return payload;
            }
        }
        sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        RefCountingMemory? result = reader.GetNodeGroup(groupKey);
        if (recordDetailedMetrics)
        {
            int partition = (int)PbtPartitions.PartitionOfPath(groupKey);
            Metrics.PbtReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, (result is null ? _readNodeGroupPersistenceNullLabels : _readNodeGroupPersistenceLabels)[partition]);
            if (result is not null) Metrics.PbtReadOnlySnapshotBundleNodeGroupBytes.AddBy(_readNodeGroupPersistenceSizeKeys[partition], result.GetSpan().Length);
        }
        return result;
    }

    public PbtAccount? GetAccount(Address address) => GetAccount(PbtStateKey.AddressKeyHash(address));

    internal PbtAccount? GetAccount(in ValueHash256 addressHash)
    {
        GuardDispose();
        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        for (int index = LayerCount - 1; index >= 0; index--)
        {
            if (TryAccount(index, addressHash, out PbtAccount? account))
            {
                if (recordDetailedMetrics) Metrics.PbtReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readAccountSnapshotLabel);
                return account;
            }
        }
        sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        PbtAccount? result = reader.GetAccount(addressHash);
        if (recordDetailedMetrics) Metrics.PbtReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, result is null ? _readAccountPersistenceNullLabel : _readAccountPersistenceLabel);
        return result;
    }

    /// <summary>Reads slot <paramref name="index"/> of the run keyed by <paramref name="runKey"/>; the newest layer holding the run answers.</summary>
    internal EvmWord GetSlot<TKey>(in HashedKey<TKey> runKey, int index) where TKey : struct, IPbtKey<TKey>
    {
        GuardDispose();
        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        int labelIndex = runKey.Key.Bytes[0] == Eip8297KeyDerivation.AccountZone ? 1 : 0;
        ValueHash256 addressHash = PbtStateKey.StorageAddress(runKey.Key);
        for (int layer = LayerCount - 1; layer >= 0; layer--)
        {
            if (TryReadSlot(layer, runKey, addressHash, index, out EvmWord value))
            {
                if (recordDetailedMetrics) Metrics.PbtReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStorageSnapshotLabels[labelIndex]);
                return value;
            }
        }
        sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        PackedSlotRun persisted = reader.GetSlotRun(runKey.Key);
        EvmWord result = persisted.Get(index);
        SlotRun.Return(persisted);
        if (recordDetailedMetrics) Metrics.PbtReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, (EvmWordSlot.IsZero(result) ? _readStoragePersistenceNullLabels : _readStoragePersistenceLabels)[labelIndex]);
        return result;
    }

    /// <summary>A caller-owned copy of the whole run keyed by <paramref name="runKey"/> as this view sees it.</summary>
    internal PackedSlotRun RentRun<TKey>(in HashedKey<TKey> runKey, in ValueHash256 addressHash) where TKey : struct, IPbtKey<TKey>
    {
        GuardDispose();
        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        int labelIndex = runKey.Key.Bytes[0] == Eip8297KeyDerivation.AccountZone ? 1 : 0;
        for (int layer = LayerCount - 1; layer >= 0; layer--)
        {
            if (TryRentRun(layer, runKey, addressHash, out PackedSlotRun? run))
            {
                if (recordDetailedMetrics) Metrics.PbtReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readRunSnapshotLabels[labelIndex]);
                return run!;
            }
        }
        sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        PackedSlotRun persisted = reader.GetSlotRun(runKey.Key);
        if (recordDetailedMetrics) Metrics.PbtReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, (persisted.Count == 0 ? _readRunPersistenceNullLabels : _readRunPersistenceLabels)[labelIndex]);
        return persisted;
    }

    internal CodeInfo? GetCode(in ValueHash256 codeHash)
    {
        GuardDispose();
        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        for (int index = LayerCount - 1; index >= 0; index--)
        {
            if (TryCode(index, codeHash, out CodeInfo? code))
            {
                if (recordDetailedMetrics) Metrics.PbtReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readCodeSnapshotLabel);
                return code;
            }
        }
        sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        CodeInfo? result = reader.GetCode(codeHash);
        if (recordDetailedMetrics) Metrics.PbtReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, result is null ? _readCodePersistenceNullLabel : _readCodePersistenceLabel);
        return result;
    }

    public bool TryLease() => TryAcquireLease();

    protected override void CleanUp()
    {
        _isDisposed = true;
        try
        {
            _chain?.Dispose();
            snapshots.Dispose();
        }
        finally
        {
            reader.Dispose();
        }
    }

    private void GuardDispose() => ObjectDisposedException.ThrowIf(_isDisposed, this);
}
