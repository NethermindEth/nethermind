// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using IResettable = Nethermind.Core.Resettables.IResettable;

namespace Nethermind.State.Pbt;

/// <summary>One immutable-at-seal diff layer of canonical EIP-8297 leaves, compressed nodes, and code references.</summary>
public sealed class PbtSnapshotContent : IDisposable, IResettable
{
    private readonly Lock _treeLock = new();

    internal readonly ConcurrentDictionary<PbtFullKey, ValueHash256?> Leaves = new();
    internal readonly ConcurrentDictionary<PbtNodePath, RefCountingMemory?> NodeGroups = new();
    internal readonly ConcurrentDictionary<ValueHash256, ulong?> CodeReferences = new();

    internal void SetLeaf(PbtFullKey key, ValueHash256? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_treeLock) Leaves[key] = value is null || value.Value == default ? null : value;
    }

    internal bool TryGetLeaf(PbtFullKey key, out ValueHash256? value)
    {
        lock (_treeLock) return Leaves.TryGetValue(key, out value);
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
            Leaves.NoLockClear();
            foreach ((_, RefCountingMemory? payload) in NodeGroups) ((IDisposable?)payload)?.Dispose();
            NodeGroups.NoLockClear();
        }
        CodeReferences.NoLockClear();
    }

    internal PbtSnapshotPayloadSize GetPayloadSize()
    {
        long leafBytes = 0;
        long nodeBytes = 0;
        foreach ((PbtFullKey key, ValueHash256? value) in Leaves)
        {
            leafBytes += key.Length + (value is null ? 0 : ValueHash256.MemorySize);
        }

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
