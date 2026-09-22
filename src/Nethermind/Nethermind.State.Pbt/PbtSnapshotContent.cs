// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;
using IResettable = Nethermind.Core.Resettables.IResettable;

namespace Nethermind.State.Pbt;

/// <summary>One immutable-at-seal diff layer of flat values and canonical node groups.</summary>
/// <remarks>
/// Concurrent node-group replacements are supported; reads concurrent with same-path replacements require caller serialization.
/// Sealed content supports concurrent readers while its snapshot is leased. Reset requires exclusive ownership.
/// </remarks>
public sealed class PbtSnapshotContent : IDisposable, IResettable
{
    internal readonly ConcurrentDictionary<ValueHash256, Account?> Accounts = new();
    // Whole slot runs keyed by run key (see SlotRun.RunKey); this layer owns the runs. A read probes every
    // layer with the same key; the pre-hashed key pays the 66-byte hash once instead of once per layer.
    internal readonly ConcurrentDictionary<HashedKey<PbtStorageTreeKey>, ISlotRun> Storages = new();
    internal readonly ConcurrentDictionary<ValueHash256, CodeInfo> Codes = new();
    internal readonly ConcurrentDictionary<ValueHash256, bool> SelfDestructedStorageAddresses = new();
    // Partitioned like PbtTrieNodeCache: account and code groups key on the narrower PbtNodePath; only storage pays for PbtStorageNodePath.
    internal readonly ConcurrentDictionary<PbtNodePath, RefCountingMemory?> AccountNodeGroups = new();
    internal readonly ConcurrentDictionary<PbtNodePath, RefCountingMemory?> CodeNodeGroups = new();
    internal readonly ConcurrentDictionary<PbtStorageNodePath, RefCountingMemory?> StorageNodeGroups = new();


    /// <summary>Drops this layer's runs of <paramref name="addressHash"/> and marks its storage cleared.</summary>
    /// <param name="addressHash">The address whose storage is cleared.</param>
    /// <param name="isNewStorage">Whether no layer below this one holds storage for the address, so persistence has nothing to delete. A clear of existing storage is sticky: a later clear of new storage does not lift it.</param>
    internal void ClearStorage(in ValueHash256 addressHash, bool isNewStorage)
    {
        foreach ((HashedKey<PbtStorageTreeKey> key, _) in Storages)
            if (PbtFlatState.StorageAddress(key) == addressHash && Storages.TryRemove(key, out ISlotRun? removed)) SlotRun.Return(removed);
        if (isNewStorage) SelfDestructedStorageAddresses.TryAdd(addressHash, true);
        else SelfDestructedStorageAddresses[addressHash] = false;
    }

    /// <summary>Whether this layer holds the run of <paramref name="runKey"/>, borrowed; a held run answers for all of its slots.</summary>
    internal bool TryGetSlotRun(in HashedKey<PbtStorageTreeKey> runKey, [NotNullWhen(true)] out ISlotRun? run) => Storages.TryGetValue(runKey, out run);

    /// <summary>Takes ownership of <paramref name="run"/> and returns the run it replaces to its pool.</summary>
    /// <remarks>Replacements of one run require caller serialization; a run being read must not be replaced.</remarks>
    internal void SetRun(in HashedKey<PbtStorageTreeKey> runKey, ISlotRun run)
    {
        Storages.TryGetValue(runKey, out ISlotRun? previous);
        SetRun(runKey, run, previous);
    }

    /// <inheritdoc cref="SetRun(in HashedKey{PbtStorageTreeKey}, ISlotRun)"/>
    /// <param name="previous">The run <paramref name="run"/> replaces, as the serialized caller already read it.</param>
    internal void SetRun(in HashedKey<PbtStorageTreeKey> runKey, ISlotRun run, ISlotRun? previous)
    {
        Storages[runKey] = run;
        if (previous is not null) SlotRun.Return(previous);
    }

    /// <summary>Retains an independent reference to a complete group replacement, or records a null tombstone.</summary>
    internal void SetNodeGroup<TPath>(TPath groupKey, RefCountingMemory? payload) where TPath : struct, IPbtNodePath<TPath>
    {
        if (payload is not null) PbtNodeGroupCodec.DebugValidateNodes(groupKey, payload.GetSpan());
        switch (PbtRocksDbPersistence.PartitionColumn(groupKey))
        {
            case PbtColumns.StorageNodeGroups: SetNodeGroup(StorageNodeGroups, groupKey.ToPath<PbtStorageNodePath>(), payload); break;
            case PbtColumns.CodeNodeGroups: SetNodeGroup(CodeNodeGroups, groupKey.ToPath<PbtNodePath>(), payload); break;
            default: SetNodeGroup(AccountNodeGroups, groupKey.ToPath<PbtNodePath>(), payload); break;
        }
    }

    private static void SetNodeGroup<TStored>(ConcurrentDictionary<TStored, RefCountingMemory?> partition, TStored groupKey, RefCountingMemory? payload) where TStored : struct, IPbtNodePath<TStored>
    {
        payload?.AcquireLease();
        RefCountingMemory? previous;
        try
        {
            while (true)
            {
                if (partition.TryGetValue(groupKey, out previous))
                {
                    if (partition.TryUpdate(groupKey, payload, previous)) break;
                }
                else if (partition.TryAdd(groupKey, payload)) break;
            }
        }
        catch
        {
            ((IDisposable?)payload)?.Dispose();
            throw;
        }
        ((IDisposable?)previous)?.Dispose();
    }

    /// <summary>Returns a caller-owned group lease or a null tombstone; false means this layer has no entry.</summary>
    internal bool TryGetNodeGroup<TPath>(TPath groupKey, out RefCountingMemory? payload) where TPath : struct, IPbtNodePath<TPath>
    {
        bool found = PbtRocksDbPersistence.PartitionColumn(groupKey) switch
        {
            PbtColumns.StorageNodeGroups => StorageNodeGroups.TryGetValue(groupKey.ToPath<PbtStorageNodePath>(), out payload),
            PbtColumns.CodeNodeGroups => CodeNodeGroups.TryGetValue(groupKey.ToPath<PbtNodePath>(), out payload),
            _ => AccountNodeGroups.TryGetValue(groupKey.ToPath<PbtNodePath>(), out payload),
        };
        payload?.AcquireLease();
        return found;
    }

    public void Reset()
    {
        Accounts.NoLockClear();
        foreach ((_, ISlotRun run) in Storages) SlotRun.Return(run);
        Storages.NoLockClear();
        Codes.NoLockClear();
        SelfDestructedStorageAddresses.NoLockClear();
        Reset(AccountNodeGroups);
        Reset(CodeNodeGroups);
        Reset(StorageNodeGroups);
    }

    private static void Reset<TStored>(ConcurrentDictionary<TStored, RefCountingMemory?> partition) where TStored : struct, IPbtNodePath<TStored>
    {
        foreach ((_, RefCountingMemory? payload) in partition) ((IDisposable?)payload)?.Dispose();
        partition.NoLockClear();
    }

    internal PbtSnapshotPayloadSize GetPayloadSize()
    {
        long leafBytes = Accounts.Count * (ValueHash256.MemorySize + 128L)
            + SelfDestructedStorageAddresses.Count * ValueHash256.MemorySize;
        foreach ((HashedKey<PbtStorageTreeKey> key, ISlotRun run) in Storages) leafBytes += key.Key.Length + run.Count * ValueHash256.MemorySize;
        foreach ((_, CodeInfo code) in Codes) leafBytes += ValueHash256.MemorySize + code.Code.Length;

        return new PbtSnapshotPayloadSize(leafBytes, NodeBytes(AccountNodeGroups) + NodeBytes(CodeNodeGroups) + NodeBytes(StorageNodeGroups));
    }

    private static long NodeBytes<TStored>(ConcurrentDictionary<TStored, RefCountingMemory?> partition) where TStored : struct, IPbtNodePath<TStored>
    {
        long nodeBytes = 0;
        foreach ((TStored path, RefCountingMemory? payload) in partition)
            nodeBytes += ((path.BitDepth + 7) >> 3) + (payload?.Memory.Length ?? 0);
        return nodeBytes;
    }

    public void Dispose() => Reset();
}

internal readonly record struct PbtSnapshotPayloadSize(long Leaf, long Node);
