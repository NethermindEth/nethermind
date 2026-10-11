// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Core.Memory;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Pbt.Common;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt.Snapshot;

/// <summary>An immutable canonical state view composed from snapshot diffs over one persistence snapshot.</summary>
/// <param name="slotFilterBitsPerKey">Bits per key of the negative filter <see cref="RentRunFiltered"/> builds over the
/// in-memory layers' slot runs; 0 never builds one.</param>
public sealed class PbtReadOnlySnapshotBundle(
    PbtSnapshotPooledList snapshots,
    IPbtPersistence.IReader reader,
    bool recordDetailedMetrics,
    double slotFilterBitsPerKey) : RefCountingDisposable
{
    private const int SlotFilterNotBuilt = 0;
    private const int SlotFilterBuilding = 1;
    private const int SlotFilterReady = 2;
    private const int SlotFilterSkipped = 3;

    // With one in-memory layer the loop is already a single dictionary probe, which the filter would not beat.
    private const int MinMemoryLayersForSlotFilter = 2;

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

    // Negative filter over the run keys and cleared addresses of all in-memory layers, for RentRunFiltered. Built
    // lazily by the first filtered read, so bundles that only serve block processing never pay for it.
    private BloomFilter? _slotFilter;
    private int _slotFilterState = InitialSlotFilterState(slotFilterBitsPerKey, snapshots.Count);

    public PbtReadOnlySnapshotBundle(PbtSnapshotChain chain, IPbtPersistence.IReader reader, bool recordDetailedMetrics, double slotFilterBitsPerKey)
        : this(new PbtSnapshotPooledList(0), reader, recordDetailedMetrics, slotFilterBitsPerKey)
    {
        _chain = chain;
        _slotFilterState = InitialSlotFilterState(slotFilterBitsPerKey, chain.Layers.Count(static layer => layer.Memory is not null));
    }

    private static int InitialSlotFilterState(double bitsPerKey, int memoryLayers) =>
        bitsPerKey > 0 && memoryLayers >= MinMemoryLayersForSlotFilter ? SlotFilterNotBuilt : SlotFilterSkipped;

    private int LayerCount => _chain?.Layers.Count ?? snapshots.Count;
    private PbtSnapshot? MemoryLayer(int index) => _chain is null ? snapshots[index] : _chain.Layers[index].Memory;
    private PersistedSnapshots.PbtRetainedSnapshot? RetainedLayer(int index) => _chain?.Layers[index].Retained;

    private bool TryAccount(int index, in ValueHash256 address, out PbtAccount? account) =>
        MemoryLayer(index) is { } memory ? memory.Content.Accounts.TryGetValue(address, out account) : RetainedLayer(index)!.TryGetAccount(address, out account);
    private bool TryCode(int index, in ValueHash256 hash, out CodeInfo? code) =>
        MemoryLayer(index) is { } memory ? memory.Content.Codes.TryGetValue(hash, out code) : RetainedLayer(index)!.TryGetCode(hash, out code);
    private bool TryGroup(int index, PbtStorageNodePath path, out RefCountingMemory? payload) =>
        MemoryLayer(index) is { } memory ? memory.Content.TryGetNodeGroup(path, out payload) : RetainedLayer(index)!.TryGetNodeGroup(path, out payload);
    private bool TryReadSlot<TKey>(int layer, in HashedKey<TKey> key, in ValueHash256 address, int index, out UInt256 value) where TKey : struct, IPbtKey<TKey>
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

    /// <summary>Returns a caller-owned group lease or a null tombstone from the visible snapshot layers; false means no snapshot has an entry.</summary>
    public bool TryGetSnapshotNodeGroup(PbtStorageNodePath groupKey, out RefCountingMemory? payload)
    {
        GuardDispose();
        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        for (int index = LayerCount - 1; index >= 0; index--)
        {
            if (TryGroup(index, groupKey, out payload))
            {
                if (recordDetailedMetrics)
                {
                    int partition = (int)PbtPartitions.PartitionOfPath(groupKey);
                    Metrics.PbtReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, (payload is null ? _readNodeGroupSnapshotNullLabels : _readNodeGroupSnapshotLabels)[partition]);
                    if (payload is not null) Metrics.PbtReadOnlySnapshotBundleNodeGroupBytes.AddBy(_readNodeGroupSnapshotSizeKeys[partition], payload.GetSpan().Length);
                }
                return true;
            }
        }
        payload = null;
        return false;
    }

    public RefCountingMemory? GetPersistedNodeGroup(PbtStorageNodePath groupKey)
    {
        GuardDispose();
        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
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

    public PbtAccount? GetAccount(in ValueHash256 addressHash)
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

    /// <summary>Reads storage slot <paramref name="slot"/> of <paramref name="address"/>.</summary>
    public UInt256 GetSlot(Address address, in UInt256 slot)
    {
        ValueHash256 addressHash = PbtStateKey.AddressKeyHash(address);
        return Eip8297KeyDerivation.IsHeaderSlot(slot)
            ? GetSlot(Eip8297KeyDerivation.HeaderStorageKey(addressHash, slot))
            : GetSlot(PbtStateKey.Storage(address, addressHash, slot));
    }

    private UInt256 GetSlot<TKey>(in TKey slotKey) where TKey : struct, IPbtKey<TKey> => GetSlot<TKey>(SlotRun.RunKey(slotKey), SlotRun.IndexOf(slotKey));

    /// <summary>Reads slot <paramref name="index"/> of the run keyed by <paramref name="runKey"/>; the newest layer holding the run answers.</summary>
    private UInt256 GetSlot<TKey>(in HashedKey<TKey> runKey, int index) where TKey : struct, IPbtKey<TKey>
    {
        GuardDispose();
        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        int labelIndex = runKey.Key.Bytes[0] == Eip8297KeyDerivation.AccountZone ? 1 : 0;
        ValueHash256 addressHash = Eip8297KeyDerivation.AddressHashOf(runKey.Key);
        for (int layer = LayerCount - 1; layer >= 0; layer--)
        {
            if (TryReadSlot(layer, runKey, addressHash, index, out UInt256 value))
            {
                if (recordDetailedMetrics) Metrics.PbtReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStorageSnapshotLabels[labelIndex]);
                return value;
            }
        }
        sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        PackedSlotRun persisted = reader.GetSlotRun(runKey.Key);
        UInt256 result = persisted.Get(index);
        SlotRun.Return(persisted);
        if (recordDetailedMetrics) Metrics.PbtReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, (result.IsZero ? _readStoragePersistenceNullLabels : _readStoragePersistenceLabels)[labelIndex]);
        return result;
    }

    /// <summary>A caller-owned copy of the whole run keyed by <paramref name="runKey"/> as this view sees it.</summary>
    public PackedSlotRun RentRun<TKey>(in HashedKey<TKey> runKey, in ValueHash256 addressHash) where TKey : struct, IPbtKey<TKey> =>
        RentRun(runKey, addressHash, skipMemoryLayers: false);

    /// <summary>
    /// Returns exactly what <see cref="RentRun{TKey}(in HashedKey{TKey}, in ValueHash256)"/> returns, but asks a
    /// negative filter over the in-memory layers first, so a run none of them holds or clears - nearly every run an
    /// <c>eth_call</c> reads - costs two filter probes instead of one dictionary probe per in-memory layer.
    /// </summary>
    /// <remarks>
    /// For read-only execution only. The first call builds the filter inline, once per bundle; reads racing that
    /// build take the plain loop instead of waiting. Retained layers keep their own bloom filters and are still
    /// probed on a definite miss.
    /// </remarks>
    public PackedSlotRun RentRunFiltered<TKey>(in HashedKey<TKey> runKey, in ValueHash256 addressHash) where TKey : struct, IPbtKey<TKey>
    {
        GuardDispose();
        BloomFilter? filter = Volatile.Read(ref _slotFilter)
            ?? (Volatile.Read(ref _slotFilterState) == SlotFilterNotBuilt ? TryBuildSlotFilter() : null);
        bool mayBeInMemory = filter is null
            || filter.MightContain(PbtSnapshotContent.SlotFilterKey(runKey))
            || filter.MightContain(PbtSnapshotContent.SlotFilterKey(addressHash));
        return RentRun(runKey, addressHash, skipMemoryLayers: !mayBeInMemory);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private PackedSlotRun RentRun<TKey>(in HashedKey<TKey> runKey, in ValueHash256 addressHash, bool skipMemoryLayers) where TKey : struct, IPbtKey<TKey>
    {
        GuardDispose();
        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        int labelIndex = runKey.Key.Bytes[0] == Eip8297KeyDerivation.AccountZone ? 1 : 0;
        for (int layer = LayerCount - 1; layer >= 0; layer--)
        {
            if ((!skipMemoryLayers || MemoryLayer(layer) is null) && TryRentRun(layer, runKey, addressHash, out PackedSlotRun? run))
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

    public CodeInfo? GetCode(in ValueHash256 codeHash)
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

    // Only the reader that moves the state out of NotBuilt builds. It holds a lease on this bundle, so neither the
    // layers it walks nor the filter it publishes can be cleaned up under it.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private BloomFilter? TryBuildSlotFilter()
    {
        if (Interlocked.CompareExchange(ref _slotFilterState, SlotFilterBuilding, SlotFilterNotBuilt) != SlotFilterNotBuilt) return null;

        long start = Stopwatch.GetTimestamp();
        BloomFilter? filter = null;
        try
        {
            // A key held by several layers is counted once per layer, which only lowers the false-positive rate.
            long capacity = 0;
            for (int layer = 0; layer < LayerCount; layer++) capacity += MemoryLayer(layer)?.Content.SlotFilterKeyCount ?? 0;

            // Layers that hold no run still get a filter: every read then skips them.
            filter = new BloomFilter(Math.Max(capacity, 1), slotFilterBitsPerKey);
            for (int layer = 0; layer < LayerCount; layer++) MemoryLayer(layer)?.Content.AddSlotFilterKeysTo(filter);
        }
        catch (Exception)
        {
            // The plain loop is always right, so a filter that cannot be built is just not used. That deliberately
            // includes OutOfMemoryException: the one large allocation here is the filter's native block, and a read
            // must not fail because an optional filter did not fit.
            filter?.Dispose();
            Volatile.Write(ref _slotFilterState, SlotFilterSkipped);
            Metrics.RecordPbtInMemorySlotFilterBuildFailed();
            return null;
        }

        Volatile.Write(ref _slotFilter, filter);
        Volatile.Write(ref _slotFilterState, SlotFilterReady);
        Metrics.RecordPbtInMemorySlotFilterBuilt(filter.DataBytes, Stopwatch.GetTimestamp() - start);
        return filter;
    }

    /// <summary>
    /// <c>false</c> when <see cref="RentRunFiltered"/> can only fall back to the plain loop: no bits per key, fewer than
    /// two in-memory layers, or a build that failed.
    /// </summary>
    public bool MayFilterSlots => Volatile.Read(ref _slotFilterState) != SlotFilterSkipped;

    private void ReleaseSlotFilter()
    {
        BloomFilter? filter = Interlocked.Exchange(ref _slotFilter, null);
        if (filter is null) return;

        Metrics.RecordPbtInMemorySlotFilterReleased(filter.DataBytes);
        filter.Dispose();
    }

    public bool TryLease() => TryAcquireLease();

    protected override void CleanUp()
    {
        _isDisposed = true;
        try
        {
            // Before the layers, whose keys the filter was built from.
            ReleaseSlotFilter();
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
