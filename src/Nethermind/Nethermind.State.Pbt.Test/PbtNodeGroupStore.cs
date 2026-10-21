// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nethermind.Core.Buffers;
using Nethermind.Core.Memory;
using Nethermind.Pbt;
using Nethermind.Pbt.NodeBuffer;

namespace Nethermind.State.Pbt.Test;

/// <summary>In-memory store of canonical nodes grouped by three-level ownership boundaries.</summary>
/// <remarks>Groups are kept as managed record bytes, so a borrowed record stays valid after its group is replaced.</remarks>
public sealed class PbtNodeGroupStore(IRefCountingMemoryProvider? memoryProvider = null) : IPbtStore, IDisposable
{
    public IPbtGroupRecordWriteBatch StartWriteBatch(int alignedTotalLength) => new PbtRecordCallbackWriteBatch(SetNodeGroup, alignedTotalLength);

    private readonly IRefCountingMemoryProvider _memoryProvider = memoryProvider ?? PooledRefCountingMemoryProvider.Instance;
    private readonly System.Threading.Lock _groupLock = new();
    private Dictionary<PbtStorageNodePath, byte[]> _groups = [];
    private bool _disposed;

    /// <summary>Reconstructs a store from canonical node-group payloads.</summary>
    public static PbtNodeGroupStore FromPhysicalPayloads(
        IReadOnlyList<PbtPhysicalPayload> payloads)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        PbtNodeGroupStore store = new();
        foreach (PbtPhysicalPayload payload in payloads)
        {
            PbtStorageNodePath groupKey = payload.Key;
            if (!PbtThreeLevelGroupGeometry.IsGroupDepth(groupKey.BitDepth))
                throw new InvalidDataException("A PBT node-group key depth must be a three-level boundary.");
            if (store._groups.ContainsKey(groupKey)) throw new InvalidDataException("Duplicate PBT node group.");

            ReadOnlySpan<byte> payloadSpan = payload.Payload.Span;
            PbtNodeGroupCodec.ValidateNodes(groupKey, payloadSpan);
            store._groups.Add(groupKey, Record(groupKey, payloadSpan));
        }

        return store;
    }

    public bool TryLocate(ulong pointer, out PbtGroupRecord record)
    {
        record = default;
        return false;
    }

    public bool TryLoad(scoped in PbtTraversalPath path, out PbtGroupRecord record)
    {
        lock (_groupLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            bool found = _groups.TryGetValue(path.ToPath<PbtStorageNodePath>(), out byte[]? bytes);
            record = found ? new PbtGroupRecord(bytes, 0) : default;
            return found;
        }
    }

    /// <summary>Returns a caller-owned copy of the group payload at <paramref name="groupKey"/>, or null when the store has none.</summary>
    public RefCountingMemory? GetNodeGroup(scoped in PbtTraversalPath groupKey)
    {
        if (!PbtThreeLevelGroupGeometry.IsGroupDepth(groupKey.BitDepth))
            throw new ArgumentException("A group key depth must be a three-level boundary.", nameof(groupKey));
        if (!TryLoad(groupKey, out PbtGroupRecord record)) return null;

        RefCountingMemory payload = _memoryProvider.Rent(record.Payload.Length);
        record.Payload.Span.CopyTo(payload.GetSpan());
        return payload;
    }

    /// <summary>Drops every group the tree from the root group no longer reaches, or every group of an empty tree.</summary>
    public void RetainTreeGroups(bool emptyTree)
    {
        HashSet<PbtStorageNodePath> reachable = emptyTree ? [] : [.. this.EnumerateRecords().Select(static record => PbtTestPaths.Locate(record.Path).GroupKey)];
        lock (_groupLock)
            foreach (PbtStorageNodePath key in _groups.Keys.ToArray())
                if (!reachable.Contains(key)) _groups.Remove(key);
    }

    /// <summary>Copies a fold's group record into the store.</summary>
    /// <remarks>
    /// A fold writes its root group last, so on the root the store drops the groups the new tree no longer reaches, as
    /// persistence keeps only the new tree's groups. A fold that empties the tree writes no root, see <see cref="RetainTreeGroups"/>.
    /// </remarks>
    public void SetNodeGroup(PbtGroupRecord record)
    {
        SetNodeGroup(record.Path, record.Span.ToArray());
        if (record.Path.BitDepth == 0) RetainTreeGroups(emptyTree: false);
    }

    /// <summary>Replaces the group at <paramref name="groupKey"/> with a copy of <paramref name="payload"/>, or deletes it for a null payload.</summary>
    public void SetNodeGroup(scoped in PbtTraversalPath groupKey, RefCountingMemory? payload)
    {
        PbtStorageNodePath storagePath = groupKey.ToPath<PbtStorageNodePath>();
        SetNodeGroup(storagePath, payload is null ? null : Record(storagePath, payload.GetSpan()));
    }

    private void SetNodeGroup(PbtStorageNodePath storagePath, byte[]? record)
    {
        lock (_groupLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!PbtThreeLevelGroupGeometry.IsGroupDepth(storagePath.BitDepth))
                throw new ArgumentException("A group key depth must be a three-level boundary.", nameof(storagePath));
            if (record is null)
            {
                _groups.Remove(storagePath);
                return;
            }

            PbtNodeGroupCodec.ValidateNodes(storagePath, new PbtGroupRecord(record, 0).Payload.Span);
            _groups[storagePath] = record;
        }
    }

    /// <summary>Loads a test store's leased group as a managed record, releasing the lease.</summary>
    /// <returns>False for a null lease, which stands for a missing group.</returns>
    public static bool TryLoadLease(RefCountingMemory? payload, scoped in PbtTraversalPath path, out PbtGroupRecord record)
    {
        using (payload)
        {
            record = payload is null ? default : new PbtGroupRecord(Record(path.ToPath<PbtStorageNodePath>(), payload.GetSpan()), 0);
            return payload is not null;
        }
    }

    private static byte[] Record(in PbtStorageNodePath path, ReadOnlySpan<byte> payload)
    {
        byte[] record = new byte[PbtGroupRecordCodec.Length(payload.Length)];
        payload.CopyTo(PbtGroupRecordCodec.WriteGroup(record, path, default, payload.Length));
        return record;
    }

    /// <summary>Enumerates group keys in canonical order.</summary>
    public IReadOnlyList<PbtStorageNodePath> EnumerateNodeGroupKeys()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PbtStorageNodePath[] keys = [.. _groups.Keys];
        Array.Sort(keys);
        return keys;
    }

    /// <summary>Exports owned copies of canonical node-group payloads sorted by group key.</summary>
    public IReadOnlyList<PbtPhysicalPayload> ExportPhysicalPayloads()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PbtStorageNodePath[] keys = [.. _groups.Keys];
        Array.Sort(keys);
        PbtPhysicalPayload[] payloads = new PbtPhysicalPayload[keys.Length];
        for (int index = 0; index < keys.Length; index++)
            payloads[index] = new PbtPhysicalPayload(keys[index], new PbtGroupRecord(_groups[keys[index]], 0).Payload.Span);
        return payloads;
    }

    /// <summary>Drops all node groups of this store.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _groups.Clear();
    }
}

/// <summary>A node-group key and an owned copy of its opaque payload.</summary>
public sealed class PbtPhysicalPayload
{
    private readonly byte[] _payload;

    public PbtPhysicalPayload(PbtStorageNodePath key, ReadOnlySpan<byte> payload)
    {
        Key = key;
        _payload = payload.ToArray();
    }

    /// <summary>Gets the node-group key.</summary>
    public PbtStorageNodePath Key { get; }

    /// <summary>Gets the opaque physical payload.</summary>
    public ReadOnlyMemory<byte> Payload => _payload;
}
