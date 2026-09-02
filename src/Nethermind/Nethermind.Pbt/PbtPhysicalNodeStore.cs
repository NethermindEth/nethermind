// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>An in-memory store whose selectable physical grouping preserves exact canonical records.</summary>
public sealed class PbtPhysicalNodeStore : IPbtStore
{
    private Dictionary<PbtNodePath, byte[]>? _records;
    private Dictionary<byte, Bucket>? _buckets;
    private PbtNodeLayout _layout;

    public PbtPhysicalNodeStore(PbtNodeLayout layout = PbtNodeLayout.Record)
    {
        _layout = Validate(layout);
        if (_layout == PbtNodeLayout.Record) _records = [];
        else _buckets = [];
    }

    /// <summary>Gets the current canonical root.</summary>
    public ValueHash256 RootHash { get; private set; }

    /// <summary>Gets or changes the authoritative physical representation of canonical records.</summary>
    public PbtNodeLayout Layout
    {
        get => _layout;
        set
        {
            PbtNodeLayout validated = Validate(value);
            if (validated == _layout) return;
            if (validated == PbtNodeLayout.HashBucket)
            {
                _buckets = BuildBuckets(_records!);
                _records = null;
            }
            else
            {
                _records = MaterializeRecords(_buckets!);
                _buckets = null;
            }
            _layout = validated;
        }
    }

    /// <summary>Reconstructs a store from physical payloads, validating their keys and exact canonical node encodings.</summary>
    public static PbtPhysicalNodeStore FromPhysicalPayloads(
        PbtNodeLayout layout,
        in ValueHash256 rootHash,
        IReadOnlyList<PbtPhysicalPayload> payloads)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        PbtPhysicalNodeStore store = new(layout) { RootHash = rootHash };
        if (layout == PbtNodeLayout.Record)
        {
            Dictionary<PbtNodePath, byte[]> records = store._records!;
            foreach (PbtPhysicalPayload payload in payloads)
            {
                PbtNodePath path = PbtNodePath.Decode(payload.Key.Span);
                byte[] encoding = payload.Payload.ToArray();
                ValidateNode(encoding);
                if (!records.TryAdd(path, encoding)) throw new InvalidDataException("Duplicate PBT node path.");
            }
        }
        else
        {
            Dictionary<byte, Bucket> buckets = store._buckets!;
            foreach (PbtPhysicalPayload payload in payloads)
            {
                if (payload.Key.Length != 1) throw new InvalidDataException("A PBT hash-bucket key must be one byte.");
                byte key = payload.Key.Span[0];
                Bucket bucket = Bucket.Parse(key, payload.Payload.Span);
                if (!buckets.TryAdd(key, bucket)) throw new InvalidDataException("Duplicate PBT hash bucket.");
            }
        }
        return store;
    }

    /// <summary>Enumerates owned copies of the canonical path/node records in path order.</summary>
    public IReadOnlyList<PbtNodeRecord> EnumerateRecords()
    {
        Dictionary<PbtNodePath, byte[]> records = _layout == PbtNodeLayout.Record
            ? _records!
            : MaterializeRecords(_buckets!);
        PbtNodePath[] paths = [.. records.Keys];
        Array.Sort(paths);
        PbtNodeRecord[] result = new PbtNodeRecord[paths.Length];
        for (int index = 0; index < result.Length; index++)
        {
            PbtNodePath path = paths[index];
            result[index] = new PbtNodeRecord(path, records[path]);
        }
        return result;
    }

    /// <summary>Gets an owned copy of the encoded canonical node.</summary>
    public byte[]? GetNode(PbtNodePath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (_layout == PbtNodeLayout.Record)
            return _records!.TryGetValue(path, out byte[]? encoding) ? (byte[])encoding.Clone() : null;

        byte bucketKey = BucketKey(path);
        return _buckets!.TryGetValue(bucketKey, out Bucket? bucket) ? bucket.GetNode(path) : null;
    }

    /// <inheritdoc/>
    public void Apply(
        in ValueHash256 newRoot,
        IReadOnlyList<PbtLeafMutation> leafMutations,
        IReadOnlyList<PbtNodeMutation> nodeMutations)
    {
        ArgumentNullException.ThrowIfNull(leafMutations);
        ArgumentNullException.ThrowIfNull(nodeMutations);
        if (_layout == PbtNodeLayout.Record) ApplyRecords(newRoot, nodeMutations);
        else ApplyBuckets(newRoot, nodeMutations);
    }

    /// <summary>Exports owned copies of the authoritative physical payloads.</summary>
    public IReadOnlyList<PbtPhysicalPayload> ExportPhysicalPayloads()
    {
        if (_layout == PbtNodeLayout.Record)
        {
            IReadOnlyList<PbtNodeRecord> records = EnumerateRecords();
            PbtPhysicalPayload[] payloads = new PbtPhysicalPayload[records.Count];
            for (int index = 0; index < payloads.Length; index++)
            {
                PbtNodeRecord record = records[index];
                payloads[index] = new PbtPhysicalPayload(record.Path.Encode(), record.Encoding.Span);
            }
            return payloads;
        }

        byte[] keys = [.. _buckets!.Keys];
        Array.Sort(keys);
        PbtPhysicalPayload[] grouped = new PbtPhysicalPayload[keys.Length];
        for (int index = 0; index < grouped.Length; index++)
        {
            byte key = keys[index];
            grouped[index] = new PbtPhysicalPayload([key], _buckets[key].Payload);
        }
        return grouped;
    }

    private void ApplyRecords(in ValueHash256 newRoot, IReadOnlyList<PbtNodeMutation> nodeMutations)
    {
        Dictionary<PbtNodePath, byte[]> records = new(_records!);
        foreach (PbtNodeMutation mutation in nodeMutations)
        {
            if (mutation.Encoding is null) records.Remove(mutation.Path);
            else
            {
                ValidateNode(mutation.Encoding);
                records[mutation.Path] = (byte[])mutation.Encoding.Clone();
            }
        }
        _records = records;
        RootHash = newRoot;
    }

    private void ApplyBuckets(in ValueHash256 newRoot, IReadOnlyList<PbtNodeMutation> nodeMutations)
    {
        Dictionary<byte, List<PbtNodeMutation>> mutationsByBucket = [];
        foreach (PbtNodeMutation mutation in nodeMutations)
        {
            byte key = BucketKey(mutation.Path);
            if (!mutationsByBucket.TryGetValue(key, out List<PbtNodeMutation>? mutations))
            {
                mutations = [];
                mutationsByBucket.Add(key, mutations);
            }
            mutations.Add(mutation);
        }

        Dictionary<byte, Bucket> buckets = new(_buckets!);
        foreach ((byte key, List<PbtNodeMutation> mutations) in mutationsByBucket)
        {
            Dictionary<PbtNodePath, byte[]> records = buckets.TryGetValue(key, out Bucket? prior)
                ? prior.Materialize()
                : [];
            foreach (PbtNodeMutation mutation in mutations)
            {
                if (mutation.Encoding is null) records.Remove(mutation.Path);
                else
                {
                    ValidateNode(mutation.Encoding);
                    records[mutation.Path] = (byte[])mutation.Encoding.Clone();
                }
            }
            if (records.Count == 0) buckets.Remove(key);
            else buckets[key] = Bucket.Build(key, records);
        }
        _buckets = buckets;
        RootHash = newRoot;
    }

    private static Dictionary<byte, Bucket> BuildBuckets(Dictionary<PbtNodePath, byte[]> records)
    {
        Dictionary<byte, Dictionary<PbtNodePath, byte[]>> grouped = [];
        foreach ((PbtNodePath path, byte[] encoding) in records)
        {
            byte key = BucketKey(path);
            if (!grouped.TryGetValue(key, out Dictionary<PbtNodePath, byte[]>? bucketRecords))
            {
                bucketRecords = [];
                grouped.Add(key, bucketRecords);
            }
            bucketRecords.Add(path, encoding);
        }

        Dictionary<byte, Bucket> buckets = new(grouped.Count);
        foreach ((byte key, Dictionary<PbtNodePath, byte[]> bucketRecords) in grouped)
            buckets.Add(key, Bucket.Build(key, bucketRecords));
        return buckets;
    }

    private static Dictionary<PbtNodePath, byte[]> MaterializeRecords(Dictionary<byte, Bucket> buckets)
    {
        Dictionary<PbtNodePath, byte[]> records = [];
        foreach (Bucket bucket in buckets.Values)
        {
            foreach ((PbtNodePath path, byte[] encoding) in bucket.Materialize()) records.Add(path, encoding);
        }
        return records;
    }

    private static byte BucketKey(PbtNodePath path) => Blake3Hash.Hash(path.Encode()).Bytes[0];

    private static void ValidateNode(ReadOnlySpan<byte> encoding) => _ = PbtNodeCodec.Decode(encoding);

    private static PbtNodeLayout Validate(PbtNodeLayout layout) => layout switch
    {
        PbtNodeLayout.Record or PbtNodeLayout.HashBucket => layout,
        _ => throw new ArgumentOutOfRangeException(nameof(layout)),
    };

    private sealed class Bucket
    {
        private readonly Dictionary<PbtNodePath, Segment> _index;

        private Bucket(byte[] payload, Dictionary<PbtNodePath, Segment> index)
        {
            Payload = payload;
            _index = index;
        }

        internal byte[] Payload { get; }

        internal byte[]? GetNode(PbtNodePath path) =>
            _index.TryGetValue(path, out Segment segment)
                ? Payload.AsSpan(segment.Offset, segment.Length).ToArray()
                : null;

        internal Dictionary<PbtNodePath, byte[]> Materialize()
        {
            Dictionary<PbtNodePath, byte[]> records = new(_index.Count);
            foreach ((PbtNodePath path, Segment segment) in _index)
                records.Add(path, Payload.AsSpan(segment.Offset, segment.Length).ToArray());
            return records;
        }

        internal static Bucket Build(byte bucketKey, Dictionary<PbtNodePath, byte[]> records)
        {
            PbtNodePath[] paths = [.. records.Keys];
            Array.Sort(paths);
            int length = 0;
            foreach (PbtNodePath path in paths)
                length = checked(length + 8 + path.Encode().Length + records[path].Length);
            byte[] payload = GC.AllocateUninitializedArray<byte>(length);
            int offset = 0;
            foreach (PbtNodePath path in paths)
            {
                byte[] pathEncoding = path.Encode();
                byte[] node = records[path];
                BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(offset), (uint)pathEncoding.Length);
                offset += 4;
                pathEncoding.CopyTo(payload, offset);
                offset += pathEncoding.Length;
                BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(offset), (uint)node.Length);
                offset += 4;
                node.CopyTo(payload, offset);
                offset += node.Length;
            }
            return Parse(bucketKey, payload);
        }

        internal static Bucket Parse(byte bucketKey, ReadOnlySpan<byte> source)
        {
            byte[] payload = source.ToArray();
            Dictionary<PbtNodePath, Segment> index = [];
            int offset = 0;
            while (offset < payload.Length)
            {
                int pathLength = ReadLength(payload, ref offset);
                if (pathLength > payload.Length - offset) throw new InvalidDataException("Truncated PBT bucket path.");
                PbtNodePath path = PbtNodePath.Decode(payload.AsSpan(offset, pathLength));
                offset += pathLength;
                if (BucketKey(path) != bucketKey) throw new InvalidDataException("A PBT record is stored in the wrong hash bucket.");
                int nodeLength = ReadLength(payload, ref offset);
                if (nodeLength > payload.Length - offset) throw new InvalidDataException("Truncated PBT bucket node.");
                ValidateNode(payload.AsSpan(offset, nodeLength));
                if (!index.TryAdd(path, new Segment(offset, nodeLength)))
                    throw new InvalidDataException("Duplicate PBT node path in a hash bucket.");
                offset += nodeLength;
            }
            return new Bucket(payload, index);
        }

        private static int ReadLength(byte[] payload, ref int offset)
        {
            if (payload.Length - offset < 4) throw new InvalidDataException("Truncated PBT bucket record length.");
            uint length = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(offset));
            offset += 4;
            if (length > int.MaxValue) throw new InvalidDataException("A PBT bucket record is too large.");
            return (int)length;
        }

        private readonly record struct Segment(int Offset, int Length);
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
