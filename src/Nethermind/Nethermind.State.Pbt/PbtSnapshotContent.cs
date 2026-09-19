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
    internal readonly ConcurrentDictionary<PbtStorageNodePath, RefCountingMemory?> NodeGroups = new();

    internal void ClearStorage(in ValueHash256 addressHash)
    {
        foreach ((HashedKey<PbtStorageTreeKey> key, _) in Storages)
            if (PbtFlatState.StorageAddress(key) == addressHash && Storages.TryRemove(key, out ISlotRun? removed)) SlotRun.Return(removed);
        SelfDestructedStorageAddresses[addressHash] = true;
    }

    /// <summary>Whether this layer holds the run of <paramref name="runKey"/>, borrowed; a held run answers for all of its slots.</summary>
    internal bool TryGetSlotRun(in HashedKey<PbtStorageTreeKey> runKey, [NotNullWhen(true)] out ISlotRun? run) => Storages.TryGetValue(runKey, out run);

    /// <summary>Takes ownership of <paramref name="run"/> and returns the run it replaces to its pool.</summary>
    /// <remarks>Replacements of one run require caller serialization; a run being read must not be replaced.</remarks>
    internal void SetRun(in HashedKey<PbtStorageTreeKey> runKey, ISlotRun run)
    {
        Storages.TryGetValue(runKey, out ISlotRun? previous);
        Storages[runKey] = run;
        if (previous is not null) SlotRun.Return(previous);
    }

    /// <summary>Retains an independent reference to a complete group replacement, or records a null tombstone.</summary>
    internal void SetNodeGroup<TPath>(TPath groupKey, RefCountingMemory? payload) where TPath : struct, IPbtNodePath<TPath>
    {
        if (!PbtFourLevelGroupGeometry.IsGroupDepth(groupKey.BitDepth))
            throw new ArgumentException("A group key depth must be a four-level boundary.", nameof(groupKey));
        if (payload is not null) PbtNodeGroupCodec.ValidateFraming(groupKey.BitDepth, payload.GetSpan());
        PbtStorageNodePath storagePath = groupKey.ToPath<PbtStorageNodePath>();
        payload?.AcquireLease();
        RefCountingMemory? previous;
        try
        {
            while (true)
            {
                if (NodeGroups.TryGetValue(storagePath, out previous))
                {
                    if (NodeGroups.TryUpdate(storagePath, payload, previous)) break;
                }
                else if (NodeGroups.TryAdd(storagePath, payload)) break;
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
        bool found = NodeGroups.TryGetValue(groupKey.ToPath<PbtStorageNodePath>(), out payload);
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
        foreach ((_, RefCountingMemory? payload) in NodeGroups) ((IDisposable?)payload)?.Dispose();
        NodeGroups.NoLockClear();
    }

    internal PbtSnapshotPayloadSize GetPayloadSize()
    {
        long leafBytes = Accounts.Count * (ValueHash256.MemorySize + 128L)
            + SelfDestructedStorageAddresses.Count * ValueHash256.MemorySize;
        long nodeBytes = 0;
        foreach ((HashedKey<PbtStorageTreeKey> key, ISlotRun run) in Storages) leafBytes += key.Key.Length + run.Count * ValueHash256.MemorySize;
        foreach ((_, CodeInfo code) in Codes) leafBytes += ValueHash256.MemorySize + code.Code.Length;

        foreach ((PbtStorageNodePath path, RefCountingMemory? payload) in NodeGroups)
            nodeBytes += ((path.BitDepth + 7) >> 3) + (payload?.Memory.Length ?? 0);

        return new PbtSnapshotPayloadSize(leafBytes, nodeBytes);
    }

    public void Dispose() => Reset();
}

internal readonly record struct PbtSnapshotPayloadSize(long Leaf, long Node);
