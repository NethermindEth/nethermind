// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>An in-memory store whose selectable physical grouping preserves exact canonical records.</summary>
public sealed class PbtPhysicalNodeStore : IPbtStore
{
    private Dictionary<PbtNodeLocator, byte[]>? _records;
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
            Dictionary<PbtNodeLocator, byte[]> records = store._records!;
            foreach (PbtPhysicalPayload payload in payloads)
            {
                PbtNodeLocator locator = PbtNodeLocator.Decode(payload.Key.Span);
                byte[] encoding = payload.Payload.ToArray();
                ValidateNode(encoding);
                if (!records.TryAdd(locator, encoding)) throw new InvalidDataException("Duplicate PBT node locator.");
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

    /// <summary>Enumerates owned copies of the canonical locator/node records in locator order.</summary>
    public IReadOnlyList<PbtNodeRecord> EnumerateRecords()
    {
        Dictionary<PbtNodeLocator, byte[]> records = _layout == PbtNodeLayout.Record
            ? _records!
            : MaterializeRecords(_buckets!);
        PbtNodeLocator[] locators = [.. records.Keys];
        Array.Sort(locators);
        PbtNodeRecord[] result = new PbtNodeRecord[locators.Length];
        for (int index = 0; index < result.Length; index++)
        {
            PbtNodeLocator locator = locators[index];
            result[index] = new PbtNodeRecord(locator, records[locator]);
        }
        return result;
    }

    /// <summary>Gets an owned copy of the encoded canonical node.</summary>
    public byte[]? GetNode(PbtNodeLocator locator)
    {
        ArgumentNullException.ThrowIfNull(locator);
        if (_layout == PbtNodeLayout.Record)
            return _records!.TryGetValue(locator, out byte[]? encoding) ? (byte[])encoding.Clone() : null;

        byte bucketKey = BucketKey(locator);
        return _buckets!.TryGetValue(bucketKey, out Bucket? bucket) ? bucket.GetNode(locator) : null;
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
                payloads[index] = new PbtPhysicalPayload(record.Locator.Encode(), record.Encoding.Span);
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
        Dictionary<PbtNodeLocator, byte[]> records = new(_records!);
        foreach (PbtNodeMutation mutation in nodeMutations)
        {
            if (mutation.Encoding is null) records.Remove(mutation.Locator);
            else
            {
                ValidateNode(mutation.Encoding);
                records[mutation.Locator] = (byte[])mutation.Encoding.Clone();
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
            byte key = BucketKey(mutation.Locator);
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
            Dictionary<PbtNodeLocator, byte[]> records = buckets.TryGetValue(key, out Bucket? prior)
                ? prior.Materialize()
                : [];
            foreach (PbtNodeMutation mutation in mutations)
            {
                if (mutation.Encoding is null) records.Remove(mutation.Locator);
                else
                {
                    ValidateNode(mutation.Encoding);
                    records[mutation.Locator] = (byte[])mutation.Encoding.Clone();
                }
            }
            if (records.Count == 0) buckets.Remove(key);
            else buckets[key] = Bucket.Build(key, records);
        }
        _buckets = buckets;
        RootHash = newRoot;
    }

    private static Dictionary<byte, Bucket> BuildBuckets(Dictionary<PbtNodeLocator, byte[]> records)
    {
        Dictionary<byte, Dictionary<PbtNodeLocator, byte[]>> grouped = [];
        foreach ((PbtNodeLocator locator, byte[] encoding) in records)
        {
            byte key = BucketKey(locator);
            if (!grouped.TryGetValue(key, out Dictionary<PbtNodeLocator, byte[]>? bucketRecords))
            {
                bucketRecords = [];
                grouped.Add(key, bucketRecords);
            }
            bucketRecords.Add(locator, encoding);
        }

        Dictionary<byte, Bucket> buckets = new(grouped.Count);
        foreach ((byte key, Dictionary<PbtNodeLocator, byte[]> bucketRecords) in grouped)
            buckets.Add(key, Bucket.Build(key, bucketRecords));
        return buckets;
    }

    private static Dictionary<PbtNodeLocator, byte[]> MaterializeRecords(Dictionary<byte, Bucket> buckets)
    {
        Dictionary<PbtNodeLocator, byte[]> records = [];
        foreach (Bucket bucket in buckets.Values)
        {
            foreach ((PbtNodeLocator locator, byte[] encoding) in bucket.Materialize()) records.Add(locator, encoding);
        }
        return records;
    }

    private static byte BucketKey(PbtNodeLocator locator) => Blake3Hash.Hash(locator.Encode()).Bytes[0];

    private static void ValidateNode(ReadOnlySpan<byte> encoding) => _ = PbtNodeCodec.Decode(encoding);

    private static PbtNodeLayout Validate(PbtNodeLayout layout) => layout switch
    {
        PbtNodeLayout.Record or PbtNodeLayout.HashBucket => layout,
        _ => throw new ArgumentOutOfRangeException(nameof(layout)),
    };

    private sealed class Bucket
    {
        private readonly Dictionary<PbtNodeLocator, Segment> _index;

        private Bucket(byte[] payload, Dictionary<PbtNodeLocator, Segment> index)
        {
            Payload = payload;
            _index = index;
        }

        internal byte[] Payload { get; }

        internal byte[]? GetNode(PbtNodeLocator locator) =>
            _index.TryGetValue(locator, out Segment segment)
                ? Payload.AsSpan(segment.Offset, segment.Length).ToArray()
                : null;

        internal Dictionary<PbtNodeLocator, byte[]> Materialize()
        {
            Dictionary<PbtNodeLocator, byte[]> records = new(_index.Count);
            foreach ((PbtNodeLocator locator, Segment segment) in _index)
                records.Add(locator, Payload.AsSpan(segment.Offset, segment.Length).ToArray());
            return records;
        }

        internal static Bucket Build(byte bucketKey, Dictionary<PbtNodeLocator, byte[]> records)
        {
            PbtNodeLocator[] locators = [.. records.Keys];
            Array.Sort(locators);
            int length = 0;
            foreach (PbtNodeLocator locator in locators)
                length = checked(length + 8 + locator.Encode().Length + records[locator].Length);
            byte[] payload = GC.AllocateUninitializedArray<byte>(length);
            int offset = 0;
            foreach (PbtNodeLocator locator in locators)
            {
                byte[] locatorEncoding = locator.Encode();
                byte[] node = records[locator];
                BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(offset), (uint)locatorEncoding.Length);
                offset += 4;
                locatorEncoding.CopyTo(payload, offset);
                offset += locatorEncoding.Length;
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
            Dictionary<PbtNodeLocator, Segment> index = [];
            int offset = 0;
            while (offset < payload.Length)
            {
                int locatorLength = ReadLength(payload, ref offset);
                if (locatorLength > payload.Length - offset) throw new InvalidDataException("Truncated PBT bucket locator.");
                PbtNodeLocator locator = PbtNodeLocator.Decode(payload.AsSpan(offset, locatorLength));
                offset += locatorLength;
                if (BucketKey(locator) != bucketKey) throw new InvalidDataException("A PBT record is stored in the wrong hash bucket.");
                int nodeLength = ReadLength(payload, ref offset);
                if (nodeLength > payload.Length - offset) throw new InvalidDataException("Truncated PBT bucket node.");
                ValidateNode(payload.AsSpan(offset, nodeLength));
                if (!index.TryAdd(locator, new Segment(offset, nodeLength)))
                    throw new InvalidDataException("Duplicate PBT node locator in a hash bucket.");
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

/// <summary>An owned canonical locator/node record.</summary>
public sealed class PbtNodeRecord
{
    private readonly byte[] _encoding;

    internal PbtNodeRecord(PbtNodeLocator locator, ReadOnlySpan<byte> encoding)
    {
        Locator = locator;
        _encoding = encoding.ToArray();
    }

    /// <summary>Gets the complete canonical locator.</summary>
    public PbtNodeLocator Locator { get; }

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
