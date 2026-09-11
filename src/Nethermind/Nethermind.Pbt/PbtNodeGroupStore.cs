// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>In-memory store of canonical nodes grouped by four-level ownership boundaries.</summary>
public sealed class PbtNodeGroupStore(IRefCountingMemoryProvider? memoryProvider = null) : IPbtStore, IDisposable
{
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
            foreach (PbtPhysicalPayload payload in payloads)
            {
                PbtStorageNodePath groupKey = PbtStorageNodePath.Decode(payload.Key.Span);
                if (!PbtFourLevelGroupGeometry.IsGroupDepth(groupKey.BitDepth))
                    throw new InvalidDataException("A PBT node-group key depth must be a four-level boundary.");
                if (store._groups.ContainsKey(groupKey)) throw new InvalidDataException("Duplicate PBT node group.");

                ReadOnlySpan<byte> payloadSpan = payload.Payload.Span;
                _ = new PbtNodeGroupReader<PbtStorageNodePath>(groupKey, payloadSpan);
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
    public RefCountingMemory? GetNodeGroup<TPath>(TPath groupKey, in ValueHash256 groupHash) where TPath : struct, IPbtNodePath<TPath>
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
    public void SetNodeGroup<TPath>(TPath groupKey, in ValueHash256 groupHash, RefCountingMemory? payload) where TPath : struct, IPbtNodePath<TPath>
    {
        lock (_groupLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!PbtFourLevelGroupGeometry.IsGroupDepth(groupKey.BitDepth))
                throw new ArgumentException("A group key depth must be a four-level boundary.", nameof(groupKey));
            if (payload is not null) _ = new PbtNodeGroupReader<TPath>(groupKey, payload.GetSpan());
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
        Span<byte> encodedKey = stackalloc byte[4 + PbtStorageFullKey.MaxLength];
        for (int index = 0; index < keys.Length; index++)
        {
            PbtStorageNodePath key = keys[index];
            key.Encode(encodedKey);
            payloads[index] = new PbtPhysicalPayload(encodedKey[..key.EncodedLength], _groups[key].GetSpan());
        }
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

/// <summary>An owned canonical path/node record.</summary>
public sealed class PbtNodeRecord
{
    private readonly byte[] _encoding;

    internal PbtNodeRecord(PbtStorageNodePath path, ReadOnlySpan<byte> encoding)
    {
        Path = path;
        _encoding = encoding.ToArray();
    }

    /// <summary>Gets the complete canonical path.</summary>
    public PbtStorageNodePath Path { get; }

    /// <summary>Gets the exact canonical node encoding.</summary>
    public ReadOnlyMemory<byte> Encoding => _encoding;
}

/// <summary>An owned physical key and opaque payload.</summary>
public sealed class PbtPhysicalPayload
{
    private readonly byte[] _key;
    private readonly byte[] _payload;

    public PbtPhysicalPayload(ReadOnlySpan<byte> key, ReadOnlySpan<byte> payload)
    {
        _key = key.ToArray();
        _payload = payload.ToArray();
    }

    /// <summary>Gets the physical key.</summary>
    public ReadOnlyMemory<byte> Key => _key;

    /// <summary>Gets the opaque physical payload.</summary>
    public ReadOnlyMemory<byte> Payload => _payload;
}
