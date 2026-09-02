// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>In-memory store of canonical nodes grouped by four-level ownership boundaries.</summary>
public sealed class PbtNodeGroupStore : IPbtStore
{
    private Dictionary<PbtNodePath, PbtNodeGroup> _groups = [];

    /// <summary>Gets the current canonical root.</summary>
    public ValueHash256 RootHash { get; private set; }

    /// <summary>Reconstructs a store from canonical node-group payloads.</summary>
    public static PbtNodeGroupStore FromPhysicalPayloads(
        in ValueHash256 rootHash,
        IReadOnlyList<PbtPhysicalPayload> payloads)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        Dictionary<PbtNodePath, PbtNodeGroup> groups = new(payloads.Count);
        foreach (PbtPhysicalPayload payload in payloads)
        {
            PbtNodePath groupKey = PbtNodePath.Decode(payload.Key.Span);
            if (!PbtFourLevelGroupGeometry.IsGroupDepth(groupKey.BitDepth))
                throw new InvalidDataException("A PBT node-group key depth must be a four-level boundary.");
            PbtNodeGroup group = PbtNodeGroupCodec.Decode(groupKey, payload.Payload.Span);
            if (!groups.TryAdd(groupKey, group)) throw new InvalidDataException("Duplicate PBT node group.");
        }

        return new PbtNodeGroupStore { _groups = groups, RootHash = rootHash };
    }

    /// <summary>Enumerates owned copies of canonical path/node records in path order.</summary>
    public IReadOnlyList<PbtNodeRecord> EnumerateRecords()
    {
        List<PbtNodeRecord> records = [];
        foreach (PbtNodeGroup group in _groups.Values)
            records.AddRange(group.EnumerateNodes());
        records.Sort(static (left, right) => left.Path.CompareTo(right.Path));
        return records;
    }

    /// <summary>Gets an owned copy of the encoded canonical node.</summary>
    public byte[]? GetNode(PbtNodePath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        PbtNodeGroupLocation location = PbtFourLevelGroupGeometry.Locate(path);
        return _groups.TryGetValue(location.GroupKey, out PbtNodeGroup? group)
            ? group.GetNode(location.Position)?.ToArray()
            : null;
    }

    /// <inheritdoc/>
    public void Apply(
        in ValueHash256 newRoot,
        IReadOnlyList<PbtLeafMutation> leafMutations,
        IReadOnlyList<PbtNodeMutation> nodeMutations)
    {
        ArgumentNullException.ThrowIfNull(leafMutations);
        ArgumentNullException.ThrowIfNull(nodeMutations);

        Dictionary<PbtNodePath, Dictionary<int, byte[]?>> staged = [];
        foreach (PbtNodeMutation mutation in nodeMutations)
        {
            PbtNodeGroupLocation location = PbtFourLevelGroupGeometry.Locate(mutation.Path);
            if (mutation.Encoding is not null)
                _ = PbtNodeCodec.Decode(mutation.Encoding);

            if (!staged.TryGetValue(location.GroupKey, out Dictionary<int, byte[]?>? mutations))
            {
                mutations = [];
                staged.Add(location.GroupKey, mutations);
            }
            mutations[location.Position] = mutation.Encoding;
        }

        Dictionary<PbtNodePath, PbtNodeGroup> groups = new(_groups);
        foreach ((PbtNodePath groupKey, Dictionary<int, byte[]?> mutations) in staged)
        {
            Dictionary<int, byte[]> nodes = [];
            if (groups.TryGetValue(groupKey, out PbtNodeGroup? prior))
            {
                foreach (PbtNodeRecord record in prior.EnumerateNodes())
                    nodes[PbtFourLevelGroupGeometry.PositionOf(record.Path)] = record.Encoding.ToArray();
            }

            foreach ((int position, byte[]? encoding) in mutations)
            {
                if (encoding is null) nodes.Remove(position);
                else
                {
                    _ = PbtNodeCodec.Decode(encoding);
                    nodes[position] = (byte[])encoding.Clone();
                }
            }

            List<PbtNodeRecord> records = new(nodes.Count);
            foreach ((int position, byte[] encoding) in nodes)
                records.Add(new PbtNodeRecord(PbtFourLevelGroupGeometry.PathOf(groupKey, position), encoding));
            if (records.Count == 0) groups.Remove(groupKey);
            else groups[groupKey] = PbtNodeGroupCodec.Decode(groupKey, PbtNodeGroupCodec.Encode(groupKey, records));
        }

        _groups = groups;
        RootHash = newRoot;
    }

    /// <summary>Exports owned copies of canonical node-group payloads sorted by group key.</summary>
    public IReadOnlyList<PbtPhysicalPayload> ExportPhysicalPayloads()
    {
        PbtNodePath[] keys = [.. _groups.Keys];
        Array.Sort(keys);
        PbtPhysicalPayload[] payloads = new PbtPhysicalPayload[keys.Length];
        for (int index = 0; index < keys.Length; index++)
        {
            PbtNodePath key = keys[index];
            PbtNodeGroup group = _groups[key];
            List<PbtNodeRecord> records = [.. group.EnumerateNodes()];
            payloads[index] = new PbtPhysicalPayload(key.Encode(), PbtNodeGroupCodec.Encode(key, records));
        }
        return payloads;
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
