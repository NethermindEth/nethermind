// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using IResettable = Nethermind.Core.Resettables.IResettable;

namespace Nethermind.State.Pbt;

/// <summary>One immutable-at-seal diff layer of flat values, canonical node groups, and code references.</summary>
/// <remarks>
/// Concurrent mutable node-group operations must own disjoint paths; same-path reads and replacements require caller serialization.
/// Sealed content supports concurrent readers while its snapshot is leased. Reset requires exclusive ownership.
/// </remarks>
public sealed class PbtSnapshotContent : IDisposable, IResettable
{
    internal readonly ConcurrentDictionary<ValueHash256, Account?> Accounts = new();
    internal readonly ConcurrentDictionary<PbtStorageFullKey, EvmWord> Storages = new();
    internal readonly ConcurrentDictionary<ValueHash256, CodeInfo> Codes = new();
    internal readonly ConcurrentDictionary<ValueHash256, bool> SelfDestructedStorageAddresses = new();
    internal readonly ConcurrentDictionary<PbtStorageNodePath, RefCountingMemory?> NodeGroups = new();
    internal readonly ConcurrentDictionary<ValueHash256, ulong?> CodeReferences = new();

    internal void ClearStorage(in ValueHash256 addressHash)
    {
        foreach ((PbtStorageFullKey key, _) in Storages)
            if (PbtFlatState.StorageAddress(key) == addressHash) Storages.TryRemove(key, out _);
        SelfDestructedStorageAddresses[addressHash] = true;
    }

    /// <summary>Retains an independent reference to a complete group replacement, or records a null tombstone.</summary>
    internal void SetNodeGroup<TPath>(TPath groupKey, RefCountingMemory? payload) where TPath : struct, IPbtNodePath<TPath>
    {
        if (!PbtFourLevelGroupGeometry.IsGroupDepth(groupKey.BitDepth))
            throw new ArgumentException("A group key depth must be a four-level boundary.", nameof(groupKey));
        if (payload is not null) _ = new PbtNodeGroupReader<TPath>(groupKey, payload.GetSpan());
        PbtStorageNodePath storagePath = groupKey.ToPath<PbtStorageNodePath>();
        NodeGroups.TryGetValue(storagePath, out RefCountingMemory? previous);
        payload?.AcquireLease();
        try
        {
            NodeGroups[storagePath] = payload;
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
        if (!PbtFourLevelGroupGeometry.IsGroupDepth(groupKey.BitDepth))
            throw new ArgumentException("A group key depth must be a four-level boundary.", nameof(groupKey));
        bool found = NodeGroups.TryGetValue(groupKey.ToPath<PbtStorageNodePath>(), out payload);
        payload?.AcquireLease();
        return found;
    }

    internal void SetCodeReference(in ValueHash256 codeHash, ulong? referenceCount) => CodeReferences[codeHash] = referenceCount;

    internal bool TryGetCodeReference(in ValueHash256 codeHash, out ulong? referenceCount) =>
        CodeReferences.TryGetValue(codeHash, out referenceCount);

    public void Reset()
    {
        Accounts.NoLockClear();
        Storages.NoLockClear();
        Codes.NoLockClear();
        SelfDestructedStorageAddresses.NoLockClear();
        foreach ((_, RefCountingMemory? payload) in NodeGroups) ((IDisposable?)payload)?.Dispose();
        NodeGroups.NoLockClear();
        CodeReferences.NoLockClear();
    }

    internal PbtSnapshotPayloadSize GetPayloadSize()
    {
        long leafBytes = Accounts.Count * (ValueHash256.MemorySize + 128L)
            + SelfDestructedStorageAddresses.Count * ValueHash256.MemorySize;
        long nodeBytes = 0;
        foreach ((PbtStorageFullKey key, _) in Storages) leafBytes += key.Length + ValueHash256.MemorySize;
        foreach ((_, CodeInfo code) in Codes) leafBytes += ValueHash256.MemorySize + code.Code.Length;

        foreach ((PbtStorageNodePath path, RefCountingMemory? payload) in NodeGroups)
            nodeBytes += path.EncodedLength + (payload?.Memory.Length ?? 0);

        long codeReferenceBytes = CodeReferences.Count * (ValueHash256.MemorySize + sizeof(ulong));
        return new PbtSnapshotPayloadSize(leafBytes, nodeBytes, codeReferenceBytes);
    }

    public void Dispose() => Reset();
}

internal readonly record struct PbtSnapshotPayloadSize(long Leaf, long Node, long CodeReference);
