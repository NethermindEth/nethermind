// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Test;

/// <summary>Drives <see cref="TrieUpdater"/> over a persistent variable-length complete-key store.</summary>
internal sealed class PbtTreeHarness : IDisposable
{
    private PbtNodeGroupStore _store = new();

    public ValueHash256 RootHash { get; private set; }

    public IReadOnlyList<PbtNodeRecord> Nodes => _store.EnumerateRecords();
    public IReadOnlyList<PbtPhysicalPayload> PhysicalPayloads => _store.ExportPhysicalPayloads();

    public ValueHash256 ApplyBatch(IEnumerable<(byte[] Key, byte[]? Value)> writes, TrieUpdaterMetrics? metrics = null)
    {
        using PbtWriteBatchBuilder<PbtStorageFullKey> batch = new(0);
        foreach ((byte[] key, byte[]? value) in writes)
        {
            PbtStorageFullKey fullKey = new(key);
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
    internal static byte[] ToPathArray<TPath>(this TPath path) where TPath : struct, IPbtNodePath<TPath>
    {
        Span<byte> encoding = stackalloc byte[path.EncodedLength];
        path.Encode(encoding);
        return encoding[4..].ToArray();
    }

    internal static byte[] ToEncodedArray<TPath>(this TPath path) where TPath : struct, IPbtNodePath<TPath>
    {
        byte[] encoding = new byte[path.EncodedLength];
        path.Encode(encoding);
        return encoding;
    }

    internal static PbtPartitionBatches PreparePartitions(IEnumerable<(byte[] Key, byte[]? Value)> changes)
    {
        using PbtWriteBatchBuilder<PbtFullKey> account = new(2);
        using PbtWriteBatchBuilder<PbtFullKey> code = new(2);
        using PbtWriteBatchBuilder<PbtStorageFullKey> storage = new(2);
        foreach ((byte[] key, byte[]? value) in changes)
        {
            switch (key[0])
            {
                case 0x00: Apply(account, new PbtFullKey(key), value); break;
                case 0x01: Apply(code, new PbtFullKey(key), value); break;
                case 0xFF: Apply(storage, new PbtStorageFullKey(key), value); break;
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

    internal static byte[]? GetNode<TPath>(this IPbtStore store, TPath path) where TPath : struct, IPbtNodePath<TPath>
    {
        PbtStorageNodePath currentPath = new([], 0);
        while (currentPath.BitDepth <= path.BitDepth)
        {
            byte[]? encoding = GetLogicalNode(store, currentPath);
            if (encoding is null || currentPath.Equals(path)) return encoding;
            PbtNodeReader node = new(encoding);
            if (node.IsLeaf || currentPath.BitDepth + node.PrefixBitCount >= path.BitDepth) return null;
            int directionBit = currentPath.BitDepth + node.PrefixBitCount;
            int direction = path.GetBit(directionBit);
            currentPath = IPbtNodePath<PbtStorageNodePath>.Append<PbtStorageNodePath>(currentPath, node.Prefix, node.PrefixBitCount, direction);
            for (int bit = 0; bit < currentPath.BitDepth; bit++)
                if (currentPath.GetBit(bit) != path.GetBit(bit)) return null;
        }
        return null;
    }

    private static byte[]? GetLogicalNode<TPath>(IPbtStore store, TPath path) where TPath : struct, IPbtNodePath<TPath>
    {
        PbtNodeGroupLocation<TPath> location = PbtFourLevelGroupGeometry.Locate(path);
        using RefCountingMemory? payload = store.GetNodeGroup(location.GroupKey);
        if (payload is null) return null;
        PbtNodeGroupReader<TPath> reader = new(location.GroupKey, payload.GetSpan());
        return ResolveNode(reader, location.Position);
    }

    private static byte[]? ResolveNode<TPath>(PbtNodeGroupReader<TPath> reader, int position) where TPath : struct, IPbtNodePath<TPath>
    {
        if (reader.TryGetNode(position, out ReadOnlySpan<byte> encoding)) return encoding.ToArray();
        TPath path = PbtFourLevelGroupGeometry.PathOf(reader.GroupKey, position);
        int relativeDepth = path.BitDepth - reader.GroupKey.BitDepth;
        if (relativeDepth is < 1 or > 3) return null;
        int width = 1 << (4 - relativeDepth);
        byte[]? left = ResolveNode(reader, position - width);
        byte[]? right = ResolveNode(reader, position - 1);
        return left is null || right is null ? null : PbtNodeCodec.EncodeBranch([], 0,
            PbtNodeCodec.Hash(new PbtNodeReader(left)), PbtNodeCodec.Hash(new PbtNodeReader(right)));
    }

    internal static void SetNode<TPath>(this IPbtStore store, TPath path, byte[]? encoding,
        IRefCountingMemoryProvider? memoryProvider = null) where TPath : struct, IPbtNodePath<TPath>
    {
        PbtNodeGroupLocation<TPath> location = PbtFourLevelGroupGeometry.Locate(path);
        using RefCountingMemory? priorPayload = store.GetNodeGroup(location.GroupKey);
        List<PbtNodeRecord> records = [];
        if (priorPayload is not null)
        {
            PbtNodeGroupReader<TPath> reader = new(location.GroupKey, priorPayload.GetSpan());
            PbtNodeGroupReader<TPath>.Enumerator enumerator = reader.EnumerateNodes();
            while (enumerator.MoveNext())
            {
                if (enumerator.CurrentPosition != location.Position)
                    records.Add(new PbtNodeRecord(PbtFourLevelGroupGeometry.PathOf(location.GroupKey, enumerator.CurrentPosition).ToPath<PbtStorageNodePath>(), enumerator.Current));
            }
        }
        if (encoding is not null) records.Add(new PbtNodeRecord(path.ToPath<PbtStorageNodePath>(), encoding));
        if (records.Count == 0)
        {
            store.SetNodeGroup(location.GroupKey, null);
            return;
        }

        BufferWriter writer = new(memoryProvider ?? PooledRefCountingMemoryProvider.Instance);
        try
        {
            PbtNodeGroupCodec.Encode(ref writer, location.GroupKey, records);
            using RefCountingMemory payload = writer.Detach()!;
            store.SetNodeGroup(location.GroupKey, payload);
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
            pending.Push(IPbtNodePath<PbtStorageNodePath>.Append<PbtStorageNodePath>(path, node.Prefix, node.PrefixBitCount, 0));
            pending.Push(IPbtNodePath<PbtStorageNodePath>.Append<PbtStorageNodePath>(path, node.Prefix, node.PrefixBitCount, 1));
        }
        records.Sort(static (left, right) => left.Path.CompareTo(right.Path));
        return records;
    }
}
