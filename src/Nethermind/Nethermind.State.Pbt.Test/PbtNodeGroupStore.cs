// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Test;

/// <summary>In-memory store of canonical nodes grouped by four-level ownership boundaries.</summary>
public sealed class PbtNodeGroupStore(IRefCountingMemoryProvider? memoryProvider = null) : IPbtStore, IPbtNodeGroupSink, IDisposable
{
    public IPbtConcurrentWriter CreateWriter() => new PbtPassThroughWriter(this);

    private readonly IRefCountingMemoryProvider _memoryProvider = memoryProvider ?? PooledRefCountingMemoryProvider.Instance;
    private readonly System.Threading.Lock _groupLock = new();
    private Dictionary<PbtStorageNodePath, RefCountingMemory> _groups = [];
    private bool _disposed;

    /// <summary>Reconstructs a store from canonical node-group payloads.</summary>
    public static PbtNodeGroupStore FromPhysicalPayloads(
        IReadOnlyList<PbtPhysicalPayload> payloads,
        IRefCountingMemoryProvider? memoryProvider = null)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        PbtNodeGroupStore store = new(memoryProvider ?? PooledRefCountingMemoryProvider.Instance);
        try
        {
            Span<byte> pathBuffer = stackalloc byte[PbtStorageTreeKey.MaxLength];
            foreach (PbtPhysicalPayload payload in payloads)
            {
                PbtStorageNodePath groupKey = payload.Key;
                if (!PbtFourLevelGroupGeometry.IsGroupDepth(groupKey.BitDepth))
                    throw new InvalidDataException("A PBT node-group key depth must be a four-level boundary.");
                if (store._groups.ContainsKey(groupKey)) throw new InvalidDataException("Duplicate PBT node group.");

                ReadOnlySpan<byte> payloadSpan = payload.Payload.Span;
                PbtTraversalPath path = PbtTraversalPath.FromPath(pathBuffer, groupKey);
                _ = new PbtNodeGroupReader(path, payloadSpan);
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
    public RefCountingMemory? GetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 groupHash)
    {
        lock (_groupLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!PbtFourLevelGroupGeometry.IsGroupDepth(groupKey.BitDepth))
                throw new ArgumentException("A group key depth must be a four-level boundary.", nameof(groupKey));
            if (!_groups.TryGetValue(groupKey.ToPath<PbtStorageNodePath>(), out RefCountingMemory? payload)) return null;

            payload.AcquireLease();
            return payload;
        }
    }

    /// <inheritdoc/>
    public void SetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 groupHash, RefCountingMemory? payload)
    {
        lock (_groupLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!PbtFourLevelGroupGeometry.IsGroupDepth(groupKey.BitDepth))
                throw new ArgumentException("A group key depth must be a four-level boundary.", nameof(groupKey));
            if (payload is not null) _ = new PbtNodeGroupReader(groupKey, payload.GetSpan());
            PbtStorageNodePath storagePath = groupKey.ToPath<PbtStorageNodePath>();

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
