// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Memory;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Pbt.Common;
using IResettable = Nethermind.Core.Resettables.IResettable;

namespace Nethermind.State.Pbt.Snapshot;

/// <summary>One immutable-at-seal diff layer of flat values and canonical node groups.</summary>
/// <remarks>
/// Node-group replacements require a single writer per partition, and reads concurrent with them are unsupported.
/// Sealed content supports concurrent readers while its snapshot is leased. Reset requires exclusive ownership.
/// </remarks>
public sealed class PbtSnapshotContent : IDisposable, IResettable
{
    public readonly ConcurrentDictionary<ValueHash256, PbtAccount?> Accounts = new();
    // Whole slot runs keyed by run key (see SlotRun.RunKey); this layer owns the runs. Header slots' runs are keyed
    // in the account zone, the rest in the storage zone. A read probes every layer with the same key; the pre-hashed
    // key pays the key hash once instead of once per layer.
    public readonly ConcurrentDictionary<HashedKey<PbtPath>, PackedSlotRun> HeaderStorages = new();
    public readonly ConcurrentDictionary<HashedKey<PbtStoragePath>, PackedSlotRun> Storages = new();
    public readonly ConcurrentDictionary<ValueHash256, CodeInfo> Codes = new();
    public readonly ConcurrentDictionary<ValueHash256, bool> SelfDestructedStorageAddresses = new();
    // Partitioned like PbtTrieNodeCache: account and code groups key on the narrower PbtNodePath; only storage pays for PbtStorageNodePath.
    public readonly Dictionary<PbtNodePath, RefCountingMemory?> AccountNodeGroups = [];
    public readonly Dictionary<PbtNodePath, RefCountingMemory?> CodeNodeGroups = [];
    public readonly Dictionary<PbtStorageNodePath, RefCountingMemory?> StorageNodeGroups = [];


    /// <summary>The runs of header slots for <see cref="PbtPath"/> run keys, or of the other slots for <see cref="PbtStoragePath"/> ones.</summary>
    private ConcurrentDictionary<HashedKey<TKey>, PackedSlotRun> Runs<TKey>() where TKey : struct, IPbtKey<TKey> =>
        SlotRun.ByZone<TKey, ConcurrentDictionary<HashedKey<TKey>, PackedSlotRun>>(HeaderStorages, Storages);

    /// <summary>Drops this layer's runs of <paramref name="addressHash"/> and marks its storage cleared.</summary>
    public void ClearStorage(in ValueHash256 addressHash)
    {
        ClearRuns(HeaderStorages, addressHash);
        ClearRuns(Storages, addressHash);
        SelfDestructedStorageAddresses.TryAdd(addressHash, true);
    }

    private static void ClearRuns<TKey>(ConcurrentDictionary<HashedKey<TKey>, PackedSlotRun> runs, in ValueHash256 addressHash) where TKey : struct, IPbtKey<TKey>
    {
        foreach ((HashedKey<TKey> key, _) in runs)
            if (Eip8297KeyDerivation.AddressHashOf(key.Key) == addressHash && runs.TryRemove(key, out PackedSlotRun? removed)) SlotRun.Return(removed);
    }

    /// <summary>The key a run key is filed under in a slot filter built by <see cref="AddSlotFilterKeysTo"/>.</summary>
    /// <remarks>The hash the run dictionaries already bucket on, so a probe hashes nothing new.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong SlotFilterKey<TKey>(in HashedKey<TKey> runKey) where TKey : struct, IPbtKey<TKey> => (uint)runKey.GetHashCode();

    /// <summary>The key a cleared address is filed under in a slot filter built by <see cref="AddSlotFilterKeysTo"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong SlotFilterKey(in ValueHash256 addressHash) => (uint)addressHash.GetHashCode();

    /// <summary>How many keys <see cref="AddSlotFilterKeysTo"/> adds.</summary>
    public long SlotFilterKeyCount => HeaderStorages.Count + Storages.Count + SelfDestructedStorageAddresses.Count;

    /// <summary>Adds every run key and cleared address of this layer to <paramref name="filter"/> under <see cref="SlotFilterKey{TKey}"/> and <see cref="SlotFilterKey(in ValueHash256)"/>.</summary>
    /// <remarks>
    /// Both kinds of entry answer a run read in this layer, so a filter that holds neither for a read proves the
    /// layer cannot answer it. Requires sealed content: a run or clear added afterwards would be missing from a filter
    /// already built.
    /// </remarks>
    public void AddSlotFilterKeysTo(BloomFilter filter)
    {
        foreach ((HashedKey<PbtPath> runKey, _) in HeaderStorages) filter.AddUnsynchronized(SlotFilterKey(runKey));
        foreach ((HashedKey<PbtStoragePath> runKey, _) in Storages) filter.AddUnsynchronized(SlotFilterKey(runKey));
        foreach ((ValueHash256 addressHash, _) in SelfDestructedStorageAddresses) filter.AddUnsynchronized(SlotFilterKey(addressHash));
    }

    /// <summary>Whether this layer holds the run of <paramref name="runKey"/>, borrowed; a held run answers for all of its slots.</summary>
    public bool TryGetSlotRun<TKey>(in HashedKey<TKey> runKey, [NotNullWhen(true)] out PackedSlotRun? run) where TKey : struct, IPbtKey<TKey> =>
        Runs<TKey>().TryGetValue(runKey, out run);

    /// <summary>Takes ownership of <paramref name="run"/> and returns the run it replaces to its pool.</summary>
    /// <remarks>Replacements of one run require caller serialization; a run being read must not be replaced.</remarks>
    public void SetRun<TKey>(in HashedKey<TKey> runKey, PackedSlotRun run) where TKey : struct, IPbtKey<TKey>
    {
        ConcurrentDictionary<HashedKey<TKey>, PackedSlotRun> runs = Runs<TKey>();
        runs.TryGetValue(runKey, out PackedSlotRun? previous);
        runs[runKey] = run;
        if (previous is not null) SlotRun.Return(previous);
    }

    /// <summary>Takes ownership of <paramref name="run"/> unless the layer already holds a run of <paramref name="runKey"/>.</summary>
    public bool TryAddRun<TKey>(in HashedKey<TKey> runKey, PackedSlotRun run) where TKey : struct, IPbtKey<TKey> => Runs<TKey>().TryAdd(runKey, run);

    /// <summary>Takes ownership of <paramref name="run"/> if the layer still holds <paramref name="expected"/>, which the caller then owns.</summary>
    /// <remarks>
    /// The compare is by reference, so <paramref name="expected"/> must stay out of the pool while any writer may still
    /// compare against it: a re-rented instance stored back under the same key would let a stale replacement through.
    /// </remarks>
    public bool TryReplaceRun<TKey>(in HashedKey<TKey> runKey, PackedSlotRun run, PackedSlotRun expected) where TKey : struct, IPbtKey<TKey> =>
        Runs<TKey>().TryUpdate(runKey, run, expected);

    /// <summary>Retains an independent reference to a complete group replacement, or records a null tombstone.</summary>
    public void SetNodeGroup<TPath>(TPath groupKey, RefCountingMemory? payload) where TPath : struct, IPbtNodePath<TPath>
    {
        if (payload is not null) PbtNodeGroupCodec.DebugValidateNodes(groupKey, payload.GetSpan());
        switch (PbtPartitions.PartitionOfPath(groupKey))
        {
            case PbtPartition.Storage: SetNodeGroup(StorageNodeGroups, groupKey.ToPath<PbtStorageNodePath>(), payload); break;
            case PbtPartition.Code: SetNodeGroup(CodeNodeGroups, groupKey.ToPath<PbtNodePath>(), payload); break;
            default: SetNodeGroup(AccountNodeGroups, groupKey.ToPath<PbtNodePath>(), payload); break;
        }
    }

    private static void SetNodeGroup<TStored>(Dictionary<TStored, RefCountingMemory?> partition, TStored groupKey, RefCountingMemory? payload) where TStored : struct, IPbtNodePath<TStored>
    {
        payload?.AcquireLease();
        ref RefCountingMemory? slot = ref CollectionsMarshal.GetValueRefOrAddDefault(partition, groupKey, out _);
        RefCountingMemory? previous = slot;
        slot = payload;
        ((IDisposable?)previous)?.Dispose();
    }

    /// <summary>Returns a caller-owned group lease or a null tombstone; false means this layer has no entry.</summary>
    public bool TryGetNodeGroup<TPath>(TPath groupKey, out RefCountingMemory? payload) where TPath : struct, IPbtNodePath<TPath>
    {
        bool found = PbtPartitions.PartitionOfPath(groupKey) switch
        {
            PbtPartition.Storage => StorageNodeGroups.TryGetValue(groupKey.ToPath<PbtStorageNodePath>(), out payload),
            PbtPartition.Code => CodeNodeGroups.TryGetValue(groupKey.ToPath<PbtNodePath>(), out payload),
            _ => AccountNodeGroups.TryGetValue(groupKey.ToPath<PbtNodePath>(), out payload),
        };
        payload?.AcquireLease();
        return found;
    }

    public void Reset()
    {
        Accounts.NoLockClear();
        foreach ((_, PackedSlotRun run) in HeaderStorages) SlotRun.Return(run);
        HeaderStorages.NoLockClear();
        foreach ((_, PackedSlotRun run) in Storages) SlotRun.Return(run);
        Storages.NoLockClear();
        Codes.NoLockClear();
        SelfDestructedStorageAddresses.NoLockClear();
        Reset(AccountNodeGroups);
        Reset(CodeNodeGroups);
        Reset(StorageNodeGroups);
    }

    private static void Reset<TStored>(Dictionary<TStored, RefCountingMemory?> partition) where TStored : struct, IPbtNodePath<TStored>
    {
        foreach ((_, RefCountingMemory? payload) in partition) ((IDisposable?)payload)?.Dispose();
        partition.Clear();
    }

    public PbtSnapshotPayloadSize GetPayloadSize()
    {
        long leafBytes = Accounts.Count * (ValueHash256.MemorySize + 128L)
            + SelfDestructedStorageAddresses.Count * ValueHash256.MemorySize;
        foreach ((_, PackedSlotRun run) in HeaderStorages) leafBytes += PbtPath.KeyLength + run.Count * ValueHash256.MemorySize;
        foreach ((_, PackedSlotRun run) in Storages) leafBytes += PbtStoragePath.KeyLength + run.Count * ValueHash256.MemorySize;
        foreach ((_, CodeInfo code) in Codes) leafBytes += ValueHash256.MemorySize + code.Code.Length;

        return new PbtSnapshotPayloadSize(leafBytes, NodeBytes(AccountNodeGroups) + NodeBytes(CodeNodeGroups) + NodeBytes(StorageNodeGroups));
    }

    private static long NodeBytes<TStored>(Dictionary<TStored, RefCountingMemory?> partition) where TStored : struct, IPbtNodePath<TStored>
    {
        long nodeBytes = 0;
        foreach ((TStored path, RefCountingMemory? payload) in partition)
            nodeBytes += ((path.BitDepth + 7) >> 3) + (payload?.Memory.Length ?? 0);
        return nodeBytes;
    }

    public void Dispose() => Reset();
}

public readonly record struct PbtSnapshotPayloadSize(long Leaf, long Node);
