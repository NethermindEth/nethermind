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
public sealed class PbtSnapshotContent : IDisposable, IResettable
{
    private readonly Lock _treeLock = new();

    internal readonly ConcurrentDictionary<ValueHash256, Account?> Accounts = new();
    internal readonly ConcurrentDictionary<PbtFullKey, EvmWord> Storages = new();
    internal readonly ConcurrentDictionary<ValueHash256, CodeInfo> Codes = new();
    internal readonly ConcurrentDictionary<ValueHash256, bool> SelfDestructedStorageAddresses = new();
    internal readonly ConcurrentDictionary<PbtNodePath, RefCountingMemory?> NodeGroups = new();
    internal readonly ConcurrentDictionary<ValueHash256, ulong?> CodeReferences = new();

    internal void ClearStorage(in ValueHash256 addressHash)
    {
        foreach ((PbtFullKey key, _) in Storages)
            if (PbtFlatState.StorageAddress(key) == addressHash) Storages.TryRemove(key, out _);
        SelfDestructedStorageAddresses[addressHash] = true;
    }

    /// <summary>Retains an independent reference to a complete group replacement, or records a null tombstone.</summary>
    internal void SetNodeGroup(PbtNodePath groupKey, RefCountingMemory? payload)
    {
        ArgumentNullException.ThrowIfNull(groupKey);
        if (!PbtFourLevelGroupGeometry.IsGroupDepth(groupKey.BitDepth))
            throw new ArgumentException("A group key depth must be a four-level boundary.", nameof(groupKey));
        if (payload is not null) _ = new PbtNodeGroupReader(groupKey, payload.GetSpan());
        lock (_treeLock)
        {
            NodeGroups.TryGetValue(groupKey, out RefCountingMemory? previous);
            payload?.AcquireLease();
            try
            {
                NodeGroups[groupKey] = payload;
            }
            catch
            {
                ((IDisposable?)payload)?.Dispose();
                throw;
            }
            ((IDisposable?)previous)?.Dispose();
        }
    }

    /// <summary>Returns a caller-owned group lease or a null tombstone; false means this layer has no entry.</summary>
    internal bool TryGetNodeGroup(PbtNodePath groupKey, out RefCountingMemory? payload)
    {
        ArgumentNullException.ThrowIfNull(groupKey);
        if (!PbtFourLevelGroupGeometry.IsGroupDepth(groupKey.BitDepth))
            throw new ArgumentException("A group key depth must be a four-level boundary.", nameof(groupKey));
        lock (_treeLock)
        {
            bool found = NodeGroups.TryGetValue(groupKey, out payload);
            payload?.AcquireLease();
            return found;
        }
    }

    internal void SetCodeReference(in ValueHash256 codeHash, ulong? referenceCount) => CodeReferences[codeHash] = referenceCount;

    internal bool TryGetCodeReference(in ValueHash256 codeHash, out ulong? referenceCount) =>
        CodeReferences.TryGetValue(codeHash, out referenceCount);

    public void Reset()
    {
        lock (_treeLock)
        {
            Accounts.NoLockClear();
            Storages.NoLockClear();
            Codes.NoLockClear();
            SelfDestructedStorageAddresses.NoLockClear();
            foreach ((_, RefCountingMemory? payload) in NodeGroups) ((IDisposable?)payload)?.Dispose();
            NodeGroups.NoLockClear();
        }
        CodeReferences.NoLockClear();
    }

    internal PbtSnapshotPayloadSize GetPayloadSize()
    {
        long leafBytes = Accounts.Count * (ValueHash256.MemorySize + 128L)
            + SelfDestructedStorageAddresses.Count * ValueHash256.MemorySize;
        long nodeBytes = 0;
        foreach ((PbtFullKey key, _) in Storages) leafBytes += key.Length + ValueHash256.MemorySize;
        foreach ((_, CodeInfo code) in Codes) leafBytes += ValueHash256.MemorySize + code.Code.Length;

        lock (_treeLock)
        {
            foreach ((PbtNodePath path, RefCountingMemory? payload) in NodeGroups)
                nodeBytes += path.Encode().Length + (payload?.Memory.Length ?? 0);
        }

        long codeReferenceBytes = CodeReferences.Count * (ValueHash256.MemorySize + sizeof(ulong));
        return new PbtSnapshotPayloadSize(leafBytes, nodeBytes, codeReferenceBytes);
    }

    public void Dispose() => Reset();
}

internal readonly record struct PbtSnapshotPayloadSize(long Leaf, long Node, long CodeReference);
