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
    private ValueHash256 _rootHash;
    private bool _disposed;

    /// <summary>Gets the current canonical root.</summary>
    public ValueHash256 RootHash
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _rootHash;
        }
        private set => _rootHash = value;
    }

    /// <summary>Reconstructs a store from canonical node-group payloads.</summary>
    public static PbtNodeGroupStore FromPhysicalPayloads(
        in ValueHash256 rootHash,
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

            store.RootHash = rootHash;
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
    public void Apply(
        in ValueHash256 newRoot,
        IReadOnlyList<PbtLeafMutation> leafMutations,
        IReadOnlyList<PbtNodeMutation> nodeMutations)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(leafMutations);
        ArgumentNullException.ThrowIfNull(nodeMutations);

        Dictionary<PbtNodePath, Dictionary<int, byte[]?>> mutationsByGroup = [];
        foreach (PbtNodeMutation mutation in nodeMutations)
        {
            PbtNodeGroupLocation location = PbtFourLevelGroupGeometry.Locate(mutation.Path);
            if (mutation.Encoding is not null) PbtNodeCodec.ValidateExact(mutation.Encoding);

            if (!mutationsByGroup.TryGetValue(location.GroupKey, out Dictionary<int, byte[]?>? mutations))
            {
                mutations = [];
                mutationsByGroup.Add(location.GroupKey, mutations);
            }
            mutations[location.Position] = mutation.Encoding;
        }

        Dictionary<PbtNodePath, RefCountingMemory?> staged = new(mutationsByGroup.Count);
        bool publishedSuccessfully = false;
        try
        {
            foreach ((PbtNodePath groupKey, Dictionary<int, byte[]?> mutations) in mutationsByGroup)
                staged.Add(groupKey, StageGroup(groupKey, mutations));

            Dictionary<PbtNodePath, RefCountingMemory> published = new(_groups.Count + staged.Count);
            foreach ((PbtNodePath groupKey, RefCountingMemory payload) in _groups)
                if (!staged.ContainsKey(groupKey)) published.Add(groupKey, payload);
            foreach ((PbtNodePath groupKey, RefCountingMemory? payload) in staged)
                if (payload is not null) published.Add(groupKey, payload);

            Dictionary<PbtNodePath, RefCountingMemory> replacedGroups = _groups;
            _groups = published;
            RootHash = newRoot;
            publishedSuccessfully = true;

            foreach (PbtNodePath groupKey in staged.Keys)
                if (replacedGroups.TryGetValue(groupKey, out RefCountingMemory? replacedPayload))
                    ((IDisposable)replacedPayload).Dispose();
        }
        finally
        {
            if (!publishedSuccessfully)
                foreach (RefCountingMemory? payload in staged.Values)
                    ((IDisposable?)payload)?.Dispose();
        }
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
