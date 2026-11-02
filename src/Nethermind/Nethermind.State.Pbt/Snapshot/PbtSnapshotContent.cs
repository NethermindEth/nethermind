// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Common;
using Nethermind.State.Pbt.NodeBuffer;
using IResettable = Nethermind.Core.Resettables.IResettable;

namespace Nethermind.State.Pbt.Snapshot;

/// <summary>One immutable-at-seal diff layer of flat values and the root of its canonical group tree.</summary>
/// <remarks>
/// The layer's groups are the buffer records of its tree written since its generation pin. A tree root is published by one
/// fold at a time, and reads concurrent with that are unsupported. Sealed content supports concurrent readers while its
/// snapshot is leased. Reset requires exclusive ownership.
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
    private IPbtGroupRecordBufferPin? _nodeGroupPin;

    /// <summary>The pin on the generation active at <see cref="BeginNodeGroupWrites"/>, or null before it; released on reset.</summary>
    public IPbtGroupRecordBufferPin? NodeGroupPin => _nodeGroupPin;

    /// <summary>Pins the active generation of <paramref name="buffer"/>, unless already pinned, so later group writes stay readable.</summary>
    public void BeginNodeGroupWrites(IPbtGroupRecordBuffer buffer)
    {
        Debug.Assert(_nodeGroupPin is null || ReferenceEquals(_nodeGroupPin.Buffer, buffer), "A layer writes its groups to one buffer.");
        _nodeGroupPin ??= buffer.PinHead();
    }

    /// <summary>The pointer of the root group of the tree this layer published, or 0 for an empty tree or when it published none.</summary>
    public ulong RootGroupPointer { get; set; }

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

    /// <summary>Takes ownership of <paramref name="run"/> unless the layer already holds a run of <paramref name="runKey"/>.</summary>
    public bool TryAddRun<TKey>(in HashedKey<TKey> runKey, PackedSlotRun run) where TKey : struct, IPbtKey<TKey> => Runs<TKey>().TryAdd(runKey, run);

    /// <summary>Takes ownership of <paramref name="run"/> if the layer still holds <paramref name="expected"/>, which the caller then owns.</summary>
    /// <remarks>
    /// The compare is by reference, so <paramref name="expected"/> must stay out of the pool while any writer may still
    /// compare against it: a re-rented instance stored back under the same key would let a stale replacement through.
    /// </remarks>
    public bool TryReplaceRun<TKey>(in HashedKey<TKey> runKey, PackedSlotRun run, PackedSlotRun expected) where TKey : struct, IPbtKey<TKey> =>
        Runs<TKey>().TryUpdate(runKey, run, expected);

    public void Reset()
    {
        Accounts.NoLockClear();
        foreach ((_, PackedSlotRun run) in HeaderStorages) SlotRun.Return(run);
        HeaderStorages.NoLockClear();
        foreach ((_, PackedSlotRun run) in Storages) SlotRun.Return(run);
        Storages.NoLockClear();
        Codes.NoLockClear();
        SelfDestructedStorageAddresses.NoLockClear();
        RootGroupPointer = 0;
        _nodeGroupPin?.Dispose();
        _nodeGroupPin = null;
    }

    public void Dispose() => Reset();
}

public readonly record struct PbtSnapshotPayloadSize(long Leaf, long Node);
