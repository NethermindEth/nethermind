// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>In-memory store of canonical nodes grouped by four-level ownership boundaries.</summary>
public sealed class PbtNodeGroupStore(IRefCountingMemoryProvider? memoryProvider = null) : IPbtStore, IDisposable
{
    private readonly IRefCountingMemoryProvider _memoryProvider = memoryProvider ?? PooledRefCountingMemoryProvider.Instance;
    private Dictionary<PbtNodePath, RefCountingMemory> _groups = [];
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
                PbtNodePath groupKey = PbtNodePath.Decode(payload.Key.Span);
                if (!PbtFourLevelGroupGeometry.IsGroupDepth(groupKey.BitDepth))
                    throw new InvalidDataException("A PBT node-group key depth must be a four-level boundary.");
                if (store._groups.ContainsKey(groupKey)) throw new InvalidDataException("Duplicate PBT node group.");

                ReadOnlySpan<byte> payloadSpan = payload.Payload.Span;
                _ = new PbtNodeGroupReader(groupKey, payloadSpan);
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

    /// <summary>Enumerates owned copies of canonical path/node records in path order.</summary>
    public IReadOnlyList<PbtNodeRecord> EnumerateRecords()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        List<PbtNodeRecord> records = [];
        foreach ((PbtNodePath groupKey, RefCountingMemory payload) in _groups)
        {
            PbtNodeGroupReader reader = new(groupKey, payload.GetSpan());
            PbtNodeGroupReader.Enumerator enumerator = reader.EnumerateNodes();
            while (enumerator.MoveNext())
            {
                records.Add(new PbtNodeRecord(
                    PbtFourLevelGroupGeometry.PathOf(groupKey, enumerator.CurrentPosition),
                    enumerator.Current));
            }
        }
        records.Sort(static (left, right) => left.Path.CompareTo(right.Path));
        return records;
    }

    /// <summary>Gets an owned copy of the encoded canonical node.</summary>
    public byte[]? GetNode(PbtNodePath path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(path);
        PbtNodeGroupLocation location = PbtFourLevelGroupGeometry.Locate(path);
        if (!_groups.TryGetValue(location.GroupKey, out RefCountingMemory? payload)) return null;

        PbtNodeGroupReader reader = new(location.GroupKey, payload.GetSpan());
        return reader.TryGetNode(location.Position, out ReadOnlySpan<byte> encoding) ? encoding.ToArray() : null;
    }

    /// <inheritdoc/>
    public RefCountingMemory? GetNodeGroup(PbtNodePath groupKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(groupKey);
        if (!PbtFourLevelGroupGeometry.IsGroupDepth(groupKey.BitDepth))
            throw new ArgumentException("A group key depth must be a four-level boundary.", nameof(groupKey));
        if (!_groups.TryGetValue(groupKey, out RefCountingMemory? payload)) return null;

        payload.AcquireLease();
        return payload;
    }

    /// <inheritdoc/>
    public void SetLeaf(PbtFullKey key, ValueHash256? value) { }

    /// <inheritdoc/>
    public void SetNode(PbtNodePath path, byte[]? encoding)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(path);
        PbtNodeGroupLocation location = PbtFourLevelGroupGeometry.Locate(path);
        if (encoding is not null) PbtNodeCodec.ValidateExact(encoding);
        Dictionary<int, byte[]?> mutation = new() { [location.Position] = encoding };
        RefCountingMemory? replacement = StageGroup(location.GroupKey, mutation);
        if (_groups.Remove(location.GroupKey, out RefCountingMemory? replacedPayload))
            ((IDisposable)replacedPayload).Dispose();
        if (replacement is not null) _groups.Add(location.GroupKey, replacement);
    }

    /// <summary>Exports owned copies of canonical node-group payloads sorted by group key.</summary>
    public IReadOnlyList<PbtPhysicalPayload> ExportPhysicalPayloads()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PbtNodePath[] keys = [.. _groups.Keys];
        Array.Sort(keys);
        PbtPhysicalPayload[] payloads = new PbtPhysicalPayload[keys.Length];
        for (int index = 0; index < keys.Length; index++)
        {
            PbtNodePath key = keys[index];
            payloads[index] = new PbtPhysicalPayload(key.Encode(), _groups[key].GetSpan());
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

    private RefCountingMemory? StageGroup(PbtNodePath groupKey, Dictionary<int, byte[]?> mutations)
    {
        PbtNodeRecord?[] recordsByPosition = new PbtNodeRecord[PbtFourLevelGroupGeometry.PositionCount];
        if (_groups.TryGetValue(groupKey, out RefCountingMemory? priorPayload))
        {
            PbtNodeGroupReader reader = new(groupKey, priorPayload.GetSpan());
            PbtNodeGroupReader.Enumerator enumerator = reader.EnumerateNodes();
            while (enumerator.MoveNext())
            {
                int position = enumerator.CurrentPosition;
                recordsByPosition[position] = new PbtNodeRecord(
                    PbtFourLevelGroupGeometry.PathOf(groupKey, position), enumerator.Current);
            }
        }

        foreach ((int position, byte[]? encoding) in mutations)
        {
            recordsByPosition[position] = encoding is null
                ? null
                : new PbtNodeRecord(PbtFourLevelGroupGeometry.PathOf(groupKey, position), encoding);
        }

        List<PbtNodeRecord> records = [];
        foreach (PbtNodeRecord? record in recordsByPosition)
            if (record is not null) records.Add(record);
        if (records.Count == 0) return null;

        BufferWriter writer = new(_memoryProvider);
        try
        {
            PbtNodeGroupCodec.Encode(ref writer, groupKey, records);
            return writer.Detach()!;
        }
        catch
        {
            writer.Dispose();
            throw;
        }
    }
}

/// <summary>An owned canonical path/node record.</summary>
public sealed class PbtNodeRecord
{
    private readonly byte[] _encoding;

    internal PbtNodeRecord(PbtNodePath path, ReadOnlySpan<byte> encoding)
    {
        Path = path;
        _encoding = encoding.ToArray();
    }

    /// <summary>Gets the complete canonical path.</summary>
    public PbtNodePath Path { get; }

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
