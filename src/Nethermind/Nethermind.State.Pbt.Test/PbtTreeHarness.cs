// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

/// <summary>Drives <see cref="TrieUpdater"/> over a persistent variable-length complete-key store.</summary>
internal sealed class PbtTreeHarness : IDisposable
{
    private PbtNodeGroupStore _store = new();

    /// <summary>A fresh processor-count fold quota, so concurrently running fixtures never starve each other.</summary>
    public static ConcurrencyController FoldQuota() => new(Environment.ProcessorCount);

    /// <summary>A fan-out with one worker minimum whatever the subtree size.</summary>
    public static FoldFanOut FanOut(int minOperationsPerWorker) => new(minOperationsPerWorker, long.MaxValue, minOperationsPerWorker);

    public ValueHash256 RootHash { get; private set; }

    public IReadOnlyList<PbtNodeRecord> Nodes => _store.EnumerateRecords();
    public IReadOnlyList<PbtPhysicalPayload> PhysicalPayloads => _store.ExportPhysicalPayloads();

    public ValueHash256 ApplyBatch(IEnumerable<(byte[] Key, byte[]? Value)> writes, TrieUpdaterMetrics? metrics = null)
    {
        using PbtWriteBatchBuilder<PbtStorageTreeKey> batch = new(0);
        foreach ((byte[] key, byte[]? value) in writes)
        {
            PbtStorageTreeKey fullKey = new(key);
            if (value is null) batch.Delete(fullKey);
            else batch.Set(fullKey, new ValueHash256(value));
        }
        RootHash = TrieUpdater.UpdateRoot(_store, RootHash, batch.Build(), metrics);
        return RootHash;
    }

    public bool TryGetNode<TPath>(TPath path, out byte[]? encoding) where TPath : struct, IPbtNodePath<TPath>
    {
        encoding = _store.GetNode(path);
        return encoding is not null;
    }

    public void Reopen()
    {
        IReadOnlyList<PbtPhysicalPayload> payloads = PhysicalPayloads;
        PbtNodeGroupStore reopened = PbtNodeGroupStore.FromPhysicalPayloads(payloads);
        PbtNodeGroupStore prior = _store;
        _store = reopened;
        prior.Dispose();
    }

    public void Dispose() => _store.Dispose();

    public string[] CanonicalRecords()
    {
        IReadOnlyList<PbtNodeRecord> records = Nodes;
        string[] result = new string[records.Count];
        for (int index = 0; index < result.Length; index++)
        {
            PbtNodeRecord record = records[index];
            result[index] = Convert.ToHexString(record.Path.ToEncodedArray()) + Convert.ToHexString(record.Encoding.Span);
        }
        return result;
    }
}

internal static class PbtStoreTestExtensions
{
    internal static int NodeGroupCount(this PbtSnapshotContent content) =>
        content.AccountNodeGroups.Count + content.CodeNodeGroups.Count + content.StorageNodeGroups.Count;

    internal static RefCountingMemory? GetNodeGroup<TPath>(this IPbtStore store, TPath groupKey, in ValueHash256 groupHash)
        where TPath : struct, IPbtNodePath<TPath>
    {
        PbtTraversalPath cursor = PbtTraversalPath.FromPath(stackalloc byte[PbtStorageTreeKey.MaxLength], groupKey);
        return store.GetNodeGroup(cursor, groupHash);
    }

    internal static void SetNodeGroup<TPath>(this IPbtStore store, TPath groupKey, in ValueHash256 groupHash, RefCountingMemory? payload)
        where TPath : struct, IPbtNodePath<TPath>
    {
        PbtTraversalPath cursor = PbtTraversalPath.FromPath(stackalloc byte[PbtStorageTreeKey.MaxLength], groupKey);
        store.SetNodeGroup(cursor, groupHash, payload);
    }

    internal static PbtNodeGroupReader ReadGroup<TPath>(TPath groupKey, ReadOnlySpan<byte> payload)
        where TPath : struct, IPbtNodePath<TPath>
    {
        PbtTraversalPath cursor = PbtTraversalPath.FromPath(stackalloc byte[PbtStorageTreeKey.MaxLength], groupKey);
        return new PbtNodeGroupReader(cursor, payload);
    }

    /// <summary>Asserts every stored descendant size equals the summed payload lengths of the groups keyed below that boundary slot.</summary>
    internal static void AssertSubtreeBytes(IReadOnlyList<PbtPhysicalPayload> payloads)
    {
        using (Assert.EnterMultipleScope())
        {
            foreach (PbtPhysicalPayload group in payloads)
            {
                int groupDepth = group.Key.BitDepth;
                long[] expected = new long[PbtNodeGroupCodec.DescendantSlots];
                foreach (PbtPhysicalPayload candidate in payloads)
                {
                    if (candidate.Key.BitDepth <= groupDepth || !candidate.Key.MatchesPrefix(group.Key, groupDepth)) continue;
                    int slot = (candidate.Key.GetByte(groupDepth >> 3) >> (4 - (groupDepth & 4))) & 0xF;
                    expected[slot] += candidate.Payload.Length;
                }
                long[] stored = new long[PbtNodeGroupCodec.DescendantSlots];
                PbtNodeGroupCodec.ReadDescendantBytes(group.Payload.Span, stored);
                Assert.That(stored, Is.EqualTo(expected), $"descendant bytes of group {Convert.ToHexString(group.Key.ToEncodedArray())}");
            }
        }
    }

    internal static RefCountingMemory? GetPhysicalNodeGroup<TPath>(this PbtNodeGroupStore store, TPath groupKey)
        where TPath : struct, IPbtNodePath<TPath>
        => store.GetNodeGroup(groupKey, store.GetGroupHash(groupKey));

    internal static ValueHash256 GetGroupHash<TPath>(this PbtNodeGroupStore store, TPath groupKey)
        where TPath : struct, IPbtNodePath<TPath>
    {
        IReadOnlyList<PbtPhysicalPayload> groups = store.ExportPhysicalPayloads();
        PbtStorageNodePath path = new([], 0);
        while (true)
        {
            PbtNodeGroupLocation<PbtStorageNodePath> location = PbtFourLevelGroupGeometry.Locate(path);
            byte[]? encoding = null;
            foreach (PbtPhysicalPayload physical in groups)
                if (physical.Key.Equals(location.GroupKey))
                    encoding = ResolveNode(PbtStoreTestExtensions.ReadGroup(location.GroupKey, physical.Payload.Span), location.GroupKey, location.Position);
            // A root leaf's hash is not derivable from its encoding, and the test store ignores group hashes anyway.
            if (encoding is null || encoding[0] == 0) return default;
            PbtNodeReader node = new(encoding);
            int branchDepth = path.BitDepth + node.Prefix.BitCount;
            for (int bit = path.BitDepth; bit < Math.Min(branchDepth, groupKey.BitDepth); bit++)
                if (TrieUpdater.GetBit(node.Prefix.Bytes, bit - path.BitDepth) != groupKey.GetBit(bit)) return default;
            if (branchDepth >= groupKey.BitDepth)
            {
                int prefixBits = branchDepth - groupKey.BitDepth;
                byte[] prefix = new byte[(prefixBits + 7) / 8];
                for (int bit = 0; bit < prefixBits; bit++)
                    prefix[bit / 8] |= (byte)(TrieUpdater.GetBit(node.Prefix.Bytes, groupKey.BitDepth - path.BitDepth + bit) << (7 - bit % 8));
                return PbtNodeCodec.Hash(new PbtNodeReader(PbtNodeCodec.EncodeBranch(prefix, prefixBits, node.LeftHash, node.RightHash)));
            }
            int direction = groupKey.GetBit(branchDepth);
            // An inline leaf has no group below it.
            if (!(direction == 0 ? node.LeftKey : node.RightKey).IsEmpty) return default;
            path = path.Append(node.Prefix, direction);
        }
    }

    internal static byte[]? GetNode<TPath>(this IPbtStore store, TPath path, in ValueHash256 root)
        where TPath : struct, IPbtNodePath<TPath>
    {
        if (path.BitDepth != 0) throw new ArgumentException("Use canonical traversal for non-root reads.", nameof(path));
        using RefCountingMemory? payload = store.GetNodeGroup(path, root);
        if (payload is null) return null;
        return ResolveNode(PbtStoreTestExtensions.ReadGroup(path, payload.GetSpan()), path, PbtFourLevelGroupGeometry.RootPosition);
    }

    internal static byte[] ToPathArray<TPath>(this TPath path) where TPath : struct, IPbtNodePath<TPath>
    {
        byte[] bytes = new byte[(path.BitDepth + 7) >> 3];
        path.CopyBitsTo(0, bytes, 0, path.BitDepth);
        return bytes;
    }

    /// <summary>The path's capacity-independent identity as bytes: its big-endian depth, then its canonical bytes.</summary>
    internal static byte[] ToEncodedArray<TPath>(this TPath path) where TPath : struct, IPbtNodePath<TPath>
    {
        byte[] encoding = new byte[4 + ((path.BitDepth + 7) >> 3)];
        BinaryPrimitives.WriteInt32BigEndian(encoding, path.BitDepth);
        path.CopyBitsTo(0, encoding.AsSpan(4), 0, path.BitDepth);
        return encoding;
    }

    internal static byte[] ToStorageKey<TPath>(this TPath path, PbtColumns column, PbtNodeGroupKeyLayout layout) where TPath : struct, IPbtNodePath<TPath> =>
        column == PbtColumns.Metadata
            ? PbtRocksDbPersistence.RootNodeGroupKey.ToArray()
            : PbtNodeGroupKey.Encode(layout, column, path, new byte[PbtNodeGroupKey.MaxLength]).ToArray();

    /// <summary>Zero-pads a zone prefix to the fixed <see cref="PbtStoragePath"/> or <see cref="PbtPath"/> length the partition fold requires.</summary>
    internal static byte[] ZoneKey(string hexPrefix)
    {
        byte[] prefix = Bytes.FromHexString(hexPrefix);
        byte[] key = new byte[prefix[0] == Eip8297KeyDerivation.StorageZone ? PbtStoragePath.KeyLength : PbtPath.KeyLength];
        prefix.CopyTo(key, 0);
        return key;
    }

    internal static PbtPartitionBatches PreparePartitions(IEnumerable<(byte[] Key, byte[]? Value)> changes)
    {
        using PbtWriteBatchBuilder<PbtPath> account = new(2);
        using PbtWriteBatchBuilder<PbtPath> code = new(2);
        using PbtWriteBatchBuilder<PbtStoragePath> storage = new(2);
        foreach ((byte[] key, byte[]? value) in changes)
        {
            switch (key[0])
            {
                case 0x00: Apply(account, new PbtPath(key), value); break;
                case 0x01: Apply(code, new PbtPath(key), value); break;
                case 0xFF: Apply(storage, new PbtStoragePath(key), value); break;
                default: throw new ArgumentException("Unsupported partition zone.", nameof(changes));
            }
        }
        return new PbtPartitionBatches
        {
            Account = account.Count == 0 ? null : account.Build(),
            Code = code.Count == 0 ? null : code.Build(),
            Storage = storage.Count == 0 ? null : storage.Build(),
        };
    }

    private static void Apply<TKey>(PbtWriteBatchBuilder<TKey> builder, TKey key, byte[]? value) where TKey : struct, IPbtKey<TKey>
    {
        if (value is null) builder.Delete(key);
        else builder.Set(key, new ValueHash256(value));
    }

    internal static byte[]? GetNode<TPath>(this PbtNodeGroupStore store, TPath path) where TPath : struct, IPbtNodePath<TPath>
    {
        PbtStorageNodePath currentPath = new([], 0);
        while (currentPath.BitDepth <= path.BitDepth)
        {
            byte[]? encoding = GetLogicalNode(store, currentPath);
            if (encoding is null || currentPath.Equals(path)) return encoding;
            PbtNodeReader node = new(encoding);
            if (node.IsLeaf || currentPath.BitDepth + node.Prefix.BitCount >= path.BitDepth) return null;
            int directionBit = currentPath.BitDepth + node.Prefix.BitCount;
            int direction = path.GetBit(directionBit);
            if (!(direction == 0 ? node.LeftKey : node.RightKey).IsEmpty) return null;
            currentPath = currentPath.Append(node.Prefix, direction);
            for (int bit = 0; bit < currentPath.BitDepth; bit++)
                if (currentPath.GetBit(bit) != path.GetBit(bit)) return null;
        }
        return null;
    }

    private static byte[]? GetLogicalNode<TPath>(PbtNodeGroupStore store, TPath path) where TPath : struct, IPbtNodePath<TPath>
    {
        PbtNodeGroupLocation<TPath> location = PbtFourLevelGroupGeometry.Locate(path);
        using RefCountingMemory? payload = store.GetPhysicalNodeGroup(location.GroupKey);
        if (payload is null) return null;
        PbtNodeGroupReader reader = PbtStoreTestExtensions.ReadGroup(location.GroupKey, payload.GetSpan());
        return ResolveNode(reader, location.GroupKey, location.Position);
    }

    private static byte[]? ResolveNode<TPath>(PbtNodeGroupReader reader, TPath groupKey, int position) where TPath : struct, IPbtNodePath<TPath>
    {
        if (reader.TryGetNode(position, out ReadOnlySpan<byte> encoding)) return encoding.ToArray();
        TPath path = PbtFourLevelGroupGeometry.PathOf(groupKey, position);
        int relativeDepth = path.BitDepth - groupKey.BitDepth;
        if (relativeDepth is < 1 or > 3) return null;
        int width = 1 << (4 - relativeDepth);
        byte[]? left = ResolveNode(reader, groupKey, position - width);
        byte[]? right = ResolveNode(reader, groupKey, position - 1);
        return left is null || right is null ? null : PbtNodeCodec.EncodeBranch([], 0,
            PbtNodeCodec.Hash(new PbtNodeReader(left)), PbtNodeCodec.Hash(new PbtNodeReader(right)));
    }

    internal static void SetNode<TPath>(this PbtNodeGroupStore store, TPath path, byte[]? encoding,
        IRefCountingMemoryProvider? memoryProvider = null) where TPath : struct, IPbtNodePath<TPath>
    {
        PbtNodeGroupLocation<TPath> location = PbtFourLevelGroupGeometry.Locate(path);
        using RefCountingMemory? priorPayload = store.GetPhysicalNodeGroup(location.GroupKey);
        List<PbtNodeRecord> records = [];
        if (priorPayload is not null)
        {
            PbtNodeGroupReader reader = PbtStoreTestExtensions.ReadGroup(location.GroupKey, priorPayload.GetSpan());
            PbtNodeGroupReader.Enumerator enumerator = reader.EnumerateNodes();
            while (enumerator.MoveNext())
            {
                if (enumerator.CurrentPosition != location.Position)
                    records.Add(new PbtNodeRecord(PbtFourLevelGroupGeometry.PathOf(location.GroupKey, enumerator.CurrentPosition).ToPath<PbtStorageNodePath>(), enumerator.Current));
            }
        }
        if (encoding is not null) records.Add(new PbtNodeRecord(path.ToPath<PbtStorageNodePath>(), encoding));
        if (records.Count == 0)
        {
            store.SetNodeGroup(location.GroupKey, default, null);
            return;
        }

        BufferWriter writer = new(memoryProvider ?? PooledRefCountingMemoryProvider.Instance);
        try
        {
            PbtNodeGroupEncoder.Encode(ref writer, location.GroupKey, records, default);
            using RefCountingMemory payload = writer.Detach()!;
            store.SetNodeGroup(location.GroupKey, encoding is null || encoding[0] == 0 ? default : PbtNodeCodec.Hash(new PbtNodeReader(encoding)), payload);
        }
        finally
        {
            writer.Dispose();
        }
    }

    internal static IReadOnlyList<PbtNodeRecord> EnumerateRecords(this PbtNodeGroupStore store)
    {
        List<PbtNodeRecord> records = [];
        Stack<PbtStorageNodePath> pending = new();
        pending.Push(new PbtStorageNodePath([], 0));
        while (pending.TryPop(out PbtStorageNodePath path))
        {
            byte[]? encoding = GetLogicalNode(store, path);
            if (encoding is null) continue;
            records.Add(new PbtNodeRecord(path, encoding));
            PbtNodeReader node = new(encoding);
            if (node.IsLeaf) continue;
            if (node.LeftKey.IsEmpty) pending.Push(path.Append(node.Prefix, 0));
            if (node.RightKey.IsEmpty) pending.Push(path.Append(node.Prefix, 1));
        }
        records.Sort(static (left, right) => left.Path.CompareTo(right.Path));
        return records;
    }
}
