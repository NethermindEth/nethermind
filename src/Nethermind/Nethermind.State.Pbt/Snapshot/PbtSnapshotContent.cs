// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Memory;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using Nethermind.Pbt.NodeBuffer;
using Nethermind.State.Pbt.Common;
using Nethermind.State.Pbt.NodeBuffer;
using Nethermind.State.Pbt.Persistence;
using IResettable = Nethermind.Core.Resettables.IResettable;

namespace Nethermind.State.Pbt.Snapshot;

/// <summary>One immutable-at-seal diff layer of flat values and the root of its canonical group tree.</summary>
/// <remarks>
/// The layer's groups are the buffer records of its tree written since its generation pin. A tree root is published by one
/// fold at a time, and reads concurrent with that are unsupported. Sealed content supports concurrent readers while its
/// snapshot is leased. Reset requires exclusive ownership.
/// </remarks>
/// <param name="nodeGroupMemory">The group payload allocator; the pooled managed one when null.</param>
public sealed class PbtSnapshotContent(IRefCountingMemoryProvider? nodeGroupMemory = null) : IDisposable, IResettable
{
    public readonly ConcurrentDictionary<ValueHash256, PbtAccount?> Accounts = new();
    // Whole slot runs keyed by run key (see SlotRun.RunKey); this layer owns the runs. Header slots' runs are keyed
    // in the account zone, the rest in the storage zone. A read probes every layer with the same key; the pre-hashed
    // key pays the key hash once instead of once per layer.
    public readonly ConcurrentDictionary<HashedKey<PbtPath>, PackedSlotRun> HeaderStorages = new();
    public readonly ConcurrentDictionary<HashedKey<PbtStoragePath>, PackedSlotRun> Storages = new();
    public readonly ConcurrentDictionary<ValueHash256, CodeInfo> Codes = new();
    public readonly ConcurrentDictionary<ValueHash256, bool> SelfDestructedStorageAddresses = new();
    private readonly IRefCountingMemoryProvider _nodeGroupMemory = nodeGroupMemory ?? PooledRefCountingMemoryProvider.Instance;
    private IPbtGroupRecordBufferPin? _nodeGroupPin;
    private long _nodeGroupBytes;

    /// <summary>The group record buffer whose records this content owns, or null before <see cref="BeginNodeGroupWrites"/>.</summary>
    public IPbtGroupRecordBuffer? GroupRecordBuffer => _nodeGroupPin?.Buffer;

    /// <summary>Pins the active generation of <paramref name="buffer"/>, unless already pinned, so later group writes stay readable.</summary>
    public void BeginNodeGroupWrites(IPbtGroupRecordBuffer buffer)
    {
        Debug.Assert(_nodeGroupPin is null || ReferenceEquals(_nodeGroupPin.Buffer, buffer), "A layer writes its groups to one buffer.");
        _nodeGroupPin ??= buffer.PinHead();
    }

    /// <summary>The pointer of the root group of the tree this layer published, or 0 for an empty tree or when it published none.</summary>
    public ulong RootGroupPointer { get; set; }

    /// <summary>An upper bound of the groups this layer wrote, counting groups later replaced within the layer.</summary>
    public int NodeGroupCount { get; private set; }

    /// <summary>Accounts for groups a fold wrote into this layer's tree.</summary>
    /// <param name="count">The number of groups.</param>
    /// <param name="bytes">Their record bytes.</param>
    public void AddNodeGroups(int count, long bytes)
    {
        NodeGroupCount += count;
        _nodeGroupBytes += bytes;
    }

    /// <summary>Enumerates the groups of this layer's tree that were written since its generation pin, in <see cref="PbtNodeGroupKey.InKeyOrder"/> order.</summary>
    /// <remarks>
    /// Groups the tree inherits from earlier layers, and groups replaced within this layer, are not enumerated. The layer's
    /// generation pin keeps the records valid while the layer is not reset.
    /// </remarks>
    public IEnumerable<PbtGroupRecord> EnumerateNodeGroups() =>
        _nodeGroupPin is null ? [] : PbtNodeGroupKey.InKeyOrder(_nodeGroupPin.Buffer, RootGroupPointer, _nodeGroupPin.WriteBoundary);

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

    /// <summary>Copies the group at <paramref name="groupKey"/> in the tree whose root group is at <paramref name="rootPointer"/> into an independent caller-owned payload.</summary>
    /// <returns>False when the walk misses, which the persisted view must then resolve.</returns>
    /// <remarks>The caller holds a <see cref="IPbtGroupRecordBuffer.PinAll"/> of the buffer, as a read-only bundle does.</remarks>
    public bool TryFindNodeGroup(ulong rootPointer, PbtStorageNodePath groupKey, [NotNullWhen(true)] out RefCountingMemory? payload)
    {
        payload = null;
        if (_nodeGroupPin is null || !_nodeGroupPin.Buffer.TryFindGroup(rootPointer, groupKey, out PbtGroupRecord record)) return false;
        payload = _nodeGroupMemory.Rent(record.Payload.Length);
        record.Payload.Span.CopyTo(payload.GetSpan());
        return true;
    }

    /// <summary>Takes over the generation range of <paramref name="source"/>, whose groups a merged layer covers.</summary>
    public void CopyNodeGroupsFrom(PbtSnapshotContent source)
    {
        Debug.Assert(_nodeGroupPin is null || source._nodeGroupPin is null || ReferenceEquals(_nodeGroupPin.Buffer, source._nodeGroupPin.Buffer));
        if (source._nodeGroupPin is { } pin && (_nodeGroupPin is null || pin.WriteBoundary < _nodeGroupPin.WriteBoundary))
        {
            IPbtGroupRecordBufferPin copy = pin.Copy();
            _nodeGroupPin?.Dispose();
            _nodeGroupPin = copy;
        }
        AddNodeGroups(source.NodeGroupCount, source._nodeGroupBytes);
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
        RootGroupPointer = 0;
        NodeGroupCount = 0;
        _nodeGroupBytes = 0;
        _nodeGroupPin?.Dispose();
        _nodeGroupPin = null;
    }

    public PbtSnapshotPayloadSize GetPayloadSize()
    {
        long leafBytes = Accounts.Count * (ValueHash256.MemorySize + 128L)
            + SelfDestructedStorageAddresses.Count * ValueHash256.MemorySize;
        foreach ((_, PackedSlotRun run) in HeaderStorages) leafBytes += PbtPath.KeyLength + run.Count * ValueHash256.MemorySize;
        foreach ((_, PackedSlotRun run) in Storages) leafBytes += PbtStoragePath.KeyLength + run.Count * ValueHash256.MemorySize;
        foreach ((_, CodeInfo code) in Codes) leafBytes += ValueHash256.MemorySize + code.Code.Length;

        return new PbtSnapshotPayloadSize(leafBytes, _nodeGroupBytes);
    }

    public void Dispose() => Reset();
}

public readonly record struct PbtSnapshotPayloadSize(long Leaf, long Node);
