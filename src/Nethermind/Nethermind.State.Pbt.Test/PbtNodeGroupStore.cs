// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nethermind.Pbt;
using Nethermind.Pbt.NodeBuffer;

namespace Nethermind.State.Pbt.Test;

/// <summary>In-memory store of canonical nodes grouped by two-level ownership boundaries.</summary>
/// <remarks>Groups are kept as managed record bytes, so a borrowed record stays valid after its group is replaced.</remarks>
public sealed class PbtNodeGroupStore : IPbtStore, IDisposable
{
    public IPbtGroupRecordWriteBatch StartWriteBatch(int alignedTotalLength) => new PbtRecordCallbackWriteBatch(SetNodeGroup, alignedTotalLength);

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
            if (!PbtTwoLevelGroupGeometry.IsGroupDepth(groupKey.BitDepth))
                throw new InvalidDataException("A PBT node-group key depth must be a two-level boundary.");
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

    public bool TryLoad<TArg>(scoped in PbtTraversalPath path, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct
    {
        lock (_groupLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_groups.TryGetValue(path.ToPath<PbtStorageNodePath>(), out byte[]? record)) return false;

            action(record, arg);
            return true;
        }
    }

    /// <summary>Returns a caller-owned copy of the group payload at <paramref name="groupKey"/>, or null when the store has none.</summary>
    public byte[]? GetNodeGroup(scoped in PbtTraversalPath groupKey)
    {
        if (!PbtTwoLevelGroupGeometry.IsGroupDepth(groupKey.BitDepth))
            throw new ArgumentException("A group key depth must be a two-level boundary.", nameof(groupKey));
        using PbtLoadedRecordBuffer loaded = new();
        return loaded.TryLoad(this, groupKey, out PbtGroupRecord record) ? record.Payload.ToArray() : null;
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
        SetRecord(record.Path, record.Span.ToArray());
        if (record.Path.BitDepth == 0) RetainTreeGroups(emptyTree: false);
    }

    /// <summary>Replaces the group at <paramref name="groupKey"/> with a copy of <paramref name="payload"/>, or deletes it for a null payload.</summary>
    public void SetNodeGroup(scoped in PbtTraversalPath groupKey, byte[]? payload)
    {
        PbtStorageNodePath storagePath = groupKey.ToPath<PbtStorageNodePath>();
        SetRecord(storagePath, payload is null ? null : Record(storagePath, payload));
    }

    private void SetRecord(PbtStorageNodePath storagePath, byte[]? record)
    {
        lock (_groupLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!PbtTwoLevelGroupGeometry.IsGroupDepth(storagePath.BitDepth))
                throw new ArgumentException("A group key depth must be a two-level boundary.", nameof(storagePath));
            if (record is null)
            {
                _groups.Remove(storagePath);
                return;
            }

            PbtNodeGroupCodec.ValidateNodes(storagePath, new PbtGroupRecord(record, 0).Payload.Span);
            _groups[storagePath] = record;
        }
    }

    /// <summary>Hands a test store's group payload to <paramref name="action"/>.</summary>
    /// <returns>False for a null payload, which stands for a missing group.</returns>
    public static bool TryLoadPayload<TArg>(byte[]? payload, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct
    {
        if (payload is null) return false;

        action(payload, arg);
        return true;
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
