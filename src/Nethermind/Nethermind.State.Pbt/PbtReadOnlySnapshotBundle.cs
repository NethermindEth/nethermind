// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;
using Nethermind.Int256;
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
    private static readonly StringLabel _readStorageSnapshotLabel = new("storage_snapshot");
    private static readonly StringLabel _readStoragePersistenceLabel = new("storage_persistence");
    private static readonly StringLabel _readStoragePersistenceNullLabel = new("storage_persistence_null");
    private static readonly StringLabel[] _readNodeGroupSnapshotLabels = [new("node_group_account_snapshot"), new("node_group_code_snapshot"), new("node_group_storage_snapshot")];
    private static readonly StringLabel[] _readNodeGroupPersistenceLabels = [new("node_group_account_persistence"), new("node_group_code_persistence"), new("node_group_storage_persistence")];
    private static readonly StringLabel[] _readNodeGroupPersistenceNullLabels = [new("node_group_account_persistence_null"), new("node_group_code_persistence_null"), new("node_group_storage_persistence_null")];
    private static readonly StringLabel _readCodeSnapshotLabel = new("code_snapshot");
    private static readonly StringLabel _readCodePersistenceLabel = new("code_persistence");
    private static readonly StringLabel _readCodePersistenceNullLabel = new("code_persistence_null");
    private static readonly StringLabel _readCodeReferenceSnapshotLabel = new("code_reference_snapshot");
    private static readonly StringLabel _readCodeReferencePersistenceLabel = new("code_reference_persistence");
    private static readonly StringLabel _readCodeReferencePersistenceNullLabel = new("code_reference_persistence_null");

    private bool _isDisposed;

    public ValueHash256 TreeRoot
    {
        get
        {
            GuardDispose();
            return snapshots.Count > 0 ? snapshots[^1].TreeRoot : reader.CurrentRoot;
        }
    }

    internal RefCountingMemory? GetNodeGroup<TPath>(TPath groupKey) where TPath : struct, IPbtNodePath<TPath>
    {
        GuardDispose();
        if (!PbtFourLevelGroupGeometry.IsGroupDepth(groupKey.BitDepth))
            throw new ArgumentException("A group key depth must be a four-level boundary.", nameof(groupKey));
        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        for (int index = snapshots.Count - 1; index >= 0; index--)
        {
            if (snapshots[index].Content.TryGetNodeGroup(groupKey, out RefCountingMemory? payload))
            {
                if (recordDetailedMetrics) Metrics.PbtReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readNodeGroupSnapshotLabels[GetNodeGroupPartition(groupKey)]);
                return payload;
            }
        }
        sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        RefCountingMemory? result = reader.GetNodeGroup(groupKey);
        if (recordDetailedMetrics) Metrics.PbtReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, (result is null ? _readNodeGroupPersistenceNullLabels : _readNodeGroupPersistenceLabels)[GetNodeGroupPartition(groupKey)]);
        return result;
    }

    private static int GetNodeGroupPartition<TPath>(TPath path) where TPath : struct, IPbtNodePath<TPath>
    {
        if (path.BitDepth == 0) return 0;
        if (path.BitDepth == 4 && path.GetByte(0) == 0xF0
            || path.BitDepth >= 8 && path.GetByte(0) == Eip8297KeyDerivation.StorageZone)
            return 2;
        return path.BitDepth >= 8 && path.GetByte(0) == Eip8297KeyDerivation.CodeZone ? 1 : 0;
    }

    internal ulong GetCodeReference(in ValueHash256 codeHash)
    {
        GuardDispose();
        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].Content.TryGetCodeReference(codeHash, out ulong? count))
            {
                if (recordDetailedMetrics) Metrics.PbtReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readCodeReferenceSnapshotLabel);
                return count ?? 0;
            }
        }

        sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        ulong result = reader.GetCodeReference(codeHash);
        if (recordDetailedMetrics) Metrics.PbtReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, result == 0 ? _readCodeReferencePersistenceNullLabel : _readCodeReferencePersistenceLabel);
        return result;
    }

    internal IEnumerable<KeyValuePair<ValueHash256, Account>> EnumerateAccounts()
    {
        GuardDispose();
        Dictionary<ValueHash256, Account?> visible = [];
        foreach ((ValueHash256 hash, Account account) in reader.EnumerateAccounts()) visible[hash] = account;
        foreach (PbtSnapshot snapshot in snapshots)
            foreach ((ValueHash256 hash, Account? account) in snapshot.Content.Accounts) visible[hash] = account;
        foreach ((ValueHash256 hash, Account? account) in visible)
            if (account is not null) yield return new(hash, account);
    }

    internal IEnumerable<KeyValuePair<PbtStorageFullKey, EvmWord>> EnumerateStorage(ValueHash256? addressFilter = null)
    {
        GuardDispose();
        SortedDictionary<PbtStorageFullKey, EvmWord> visible = [];
        if (addressFilter is null)
        {
            foreach ((PbtStorageFullKey key, EvmWord value) in reader.EnumerateStorage()) visible[key] = value;
        }
        else
        {
            byte[] prefix = new byte[1 + ValueHash256.MemorySize];
            addressFilter.Value.Bytes.CopyTo(prefix.AsSpan(1));
            foreach (byte zone in new[] { Eip8297KeyDerivation.AccountZone, Eip8297KeyDerivation.StorageZone })
            {
                prefix[0] = zone;
                foreach ((PbtStorageFullKey key, EvmWord value) in reader.EnumerateStorage(new PbtStorageFullKey(prefix))) visible[key] = value;
            }
        }
        foreach (PbtSnapshot snapshot in snapshots) PbtFlatState.ApplyStorage(visible, snapshot.Content, addressFilter);
        foreach ((PbtStorageFullKey key, EvmWord value) in visible)
            if (!EvmWordSlot.IsZero(value)) yield return new(key, value);
    }

    internal IEnumerable<KeyValuePair<PbtStorageFullKey, ValueHash256>> EnumerateLeaves() =>
        PbtFlatState.EnumerateLeaves(EnumerateAccounts(), EnumerateStorage(), hash => GetCode(hash));

    internal IEnumerable<KeyValuePair<PbtStorageFullKey, ValueHash256>> EnumerateLeaves(PbtStorageFullKey prefix)
    {
        foreach (KeyValuePair<PbtStorageFullKey, ValueHash256> leaf in EnumerateLeaves())
            if (prefix.IsPrefixOf(leaf.Key)) yield return leaf;
    }

    public Account? GetAccount(Address address) => GetAccount(PbtKeyDerivation.AddressKeyHash(address));

    internal Account? GetAccount(in ValueHash256 addressHash)
    {
        GuardDispose();
        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        for (int index = snapshots.Count - 1; index >= 0; index--)
        {
            if (snapshots[index].Content.Accounts.TryGetValue(addressHash, out Account? account))
            {
                if (recordDetailedMetrics) Metrics.PbtReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readAccountSnapshotLabel);
                return account;
            }
        }
        sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        Account? result = reader.GetAccount(addressHash);
        if (recordDetailedMetrics) Metrics.PbtReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, result is null ? _readAccountPersistenceNullLabel : _readAccountPersistenceLabel);
        return result;
    }

    public EvmWord GetSlot(Address address, in UInt256 slot) => GetSlot(PbtStateKey.Storage(address, slot));

    internal EvmWord GetSlot(PbtStorageFullKey key)
    {
        GuardDispose();
        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        ValueHash256 addressHash = PbtFlatState.StorageAddress(key);
        for (int index = snapshots.Count - 1; index >= 0; index--)
        {
            PbtSnapshotContent content = snapshots[index].Content;
            if (content.Storages.TryGetValue(key, out EvmWord value)
                || content.SelfDestructedStorageAddresses.ContainsKey(addressHash))
            {
                if (recordDetailedMetrics) Metrics.PbtReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStorageSnapshotLabel);
                return value;
            }
        }
        sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        EvmWord result = reader.GetSlot(key);
        if (recordDetailedMetrics) Metrics.PbtReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, EvmWordSlot.IsZero(result) ? _readStoragePersistenceNullLabel : _readStoragePersistenceLabel);
        return result;
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
        if (_isDisposed) return;
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
