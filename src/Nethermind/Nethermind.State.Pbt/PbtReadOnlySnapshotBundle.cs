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
    bool recordDetailedMetrics = false) : RefCountingDisposable
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

    public ValueHash256 TreeRoot
    {
        get
        {
            GuardDispose();
            return snapshots.Count > 0 ? snapshots[^1].TreeRoot : reader.CurrentRoot;
        }
    }

    /// <summary>Returns a caller-owned group lease, or null when no layer has an entry.</summary>
    internal RefCountingMemory? GetNodeGroup(PbtStorageNodePath groupKey)
    {
        GuardDispose();
        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        for (int index = snapshots.Count - 1; index >= 0; index--)
        {
            if (snapshots[index].Content.TryGetNodeGroup(groupKey, out RefCountingMemory? payload))
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
        for (int index = snapshots.Count - 1; index >= 0; index--)
        {
            if (snapshots[index].Content.Accounts.TryGetValue(addressHash, out PbtAccount? account))
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
        for (int layer = snapshots.Count - 1; layer >= 0; layer--)
        {
            PbtSnapshotContent content = snapshots[layer].Content;
            if (content.TryGetSlotRun(runKey, out PackedSlotRun? run) || content.SelfDestructedStorageAddresses.ContainsKey(addressHash))
            {
                if (recordDetailedMetrics) Metrics.PbtReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStorageSnapshotLabels[labelIndex]);
                return run?.Get(index) ?? default;
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
        for (int layer = snapshots.Count - 1; layer >= 0; layer--)
        {
            PbtSnapshotContent content = snapshots[layer].Content;
            if (content.TryGetSlotRun(runKey, out PackedSlotRun? run) || content.SelfDestructedStorageAddresses.ContainsKey(addressHash))
            {
                if (recordDetailedMetrics) Metrics.PbtReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readRunSnapshotLabels[labelIndex]);
                return run?.Clone() ?? SlotRun.Empty;
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
        for (int index = snapshots.Count - 1; index >= 0; index--)
        {
            if (snapshots[index].Content.Codes.TryGetValue(codeHash, out CodeInfo? code))
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
            snapshots.Dispose();
        }
        finally
        {
            reader.Dispose();
        }
    }

    private void GuardDispose() => ObjectDisposedException.ThrowIf(_isDisposed, this);
}
