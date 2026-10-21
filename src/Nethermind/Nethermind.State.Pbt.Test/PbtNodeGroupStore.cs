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
public sealed class PbtNodeGroupStore(IRefCountingMemoryProvider? memoryProvider = null) : IPbtStore, IDisposable
{
    public IPbtConcurrentWriter CreateWriter() => new PbtRecordCallbackWriter(SetNodeGroup);

    private readonly IRefCountingMemoryProvider _memoryProvider = memoryProvider ?? PooledRefCountingMemoryProvider.Instance;
    private readonly System.Threading.Lock _groupLock = new();
    private Dictionary<PbtStorageNodePath, RefCountingMemory> _groups = [];
    private bool _rootWritten;
    private bool _disposed;

    /// <summary>Reconstructs a store from canonical node-group payloads.</summary>
    public static PbtNodeGroupStore FromPhysicalPayloads(
        IReadOnlyList<PbtPhysicalPayload> payloads)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        PbtNodeGroupStore store = new();
        try
        {
            foreach (PbtPhysicalPayload payload in payloads)
            {
                PbtStorageNodePath groupKey = payload.Key;
                if (!PbtThreeLevelGroupGeometry.IsGroupDepth(groupKey.BitDepth))
                    throw new InvalidDataException("A PBT node-group key depth must be a three-level boundary.");
                if (store._groups.ContainsKey(groupKey)) throw new InvalidDataException("Duplicate PBT node group.");

                ReadOnlySpan<byte> payloadSpan = payload.Payload.Span;
                PbtNodeGroupCodec.ValidateNodes(groupKey, payloadSpan);
                RefCountingMemory ownedPayload = store._memoryProvider.Rent(payloadSpan.Length);
                try
                {
                    payloadSpan.CopyTo(ownedPayload.GetSpan());
                    store._groups.Add(groupKey, ownedPayload);
                }
                catch
                {
                    ((IDisposable)ownedPayload).Dispose();
                    throw;
                }
            }

            return store;
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public RefCountingMemory? GetNodeGroup(scoped in PbtTraversalPath groupKey)
    {
        lock (_groupLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!PbtThreeLevelGroupGeometry.IsGroupDepth(groupKey.BitDepth))
                throw new ArgumentException("A group key depth must be a three-level boundary.", nameof(groupKey));
            if (!_groups.TryGetValue(groupKey.ToPath<PbtStorageNodePath>(), out RefCountingMemory? payload)) return null;

            payload.AcquireLease();
            return payload;
        }
    }

    /// <summary>Drops the groups the completed fold left unreachable, as persistence keeps only the new tree's groups.</summary>
    /// <remarks>A completed fold that wrote no root group emptied the tree.</remarks>
    public void CompleteFold()
    {
        RetainTreeGroups(!_rootWritten);
        _rootWritten = false;
    }

    /// <summary>Drops every group the tree from the root group no longer reaches, or every group of an empty tree.</summary>
    public void RetainTreeGroups(bool emptyTree)
    {
        HashSet<PbtStorageNodePath> reachable = emptyTree ? [] : [.. this.EnumerateRecords().Select(static record => PbtTestPaths.Locate(record.Path).GroupKey)];
        lock (_groupLock)
            foreach (PbtStorageNodePath key in _groups.Keys.ToArray())
                if (!reachable.Contains(key) && _groups.Remove(key, out RefCountingMemory? payload)) ((IDisposable)payload).Dispose();
    }

    /// <summary>Copies a fold's group record into the store.</summary>
    public void SetNodeGroup(PbtGroupRecord record)
    {
        if (record.Path.BitDepth == 0) _rootWritten = true;
        using RefCountingMemory payload = _memoryProvider.Rent(record.Payload.Length);
        record.Payload.Span.CopyTo(payload.GetSpan());
        SetNodeGroup(record.Path, payload);
    }

    /// <summary>Replaces the group at <paramref name="groupKey"/>, or deletes it for a null payload, which the store then leases.</summary>
    public void SetNodeGroup(scoped in PbtTraversalPath groupKey, RefCountingMemory? payload) => SetNodeGroup(groupKey.ToPath<PbtStorageNodePath>(), payload);

    private void SetNodeGroup(PbtStorageNodePath storagePath, RefCountingMemory? payload)
    {
        lock (_groupLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!PbtThreeLevelGroupGeometry.IsGroupDepth(storagePath.BitDepth))
                throw new ArgumentException("A group key depth must be a three-level boundary.", nameof(storagePath));
            if (payload is not null) PbtNodeGroupCodec.ValidateNodes(storagePath, payload.GetSpan());

            _groups.TryGetValue(storagePath, out RefCountingMemory? previous);
            if (payload is null)
            {
                _groups.Remove(storagePath);
            }
            else
            {
                payload.AcquireLease();
                try
                {
                    _groups[storagePath] = payload;
                }
                catch
                {
                    ((IDisposable)payload).Dispose();
                    throw;
                }
            }
            ((IDisposable?)previous)?.Dispose();
        }
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
            payloads[index] = new PbtPhysicalPayload(keys[index], _groups[keys[index]].GetSpan());
        return payloads;
    }

    /// <summary>Releases all node-group payloads owned by this store.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (RefCountingMemory payload in _groups.Values) ((IDisposable)payload).Dispose();
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
