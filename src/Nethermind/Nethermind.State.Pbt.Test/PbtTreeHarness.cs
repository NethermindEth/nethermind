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

    public ValueHash256 ApplyBatch(IEnumerable<(byte[] Key, byte[]? Value)> writes)
    {
        using PbtWriteBatchBuilder<PbtStorageFullKey> batch = new(0);
        foreach ((byte[] key, byte[]? value) in writes)
        {
            PbtStorageFullKey fullKey = new(key);
            if (value is null) batch.Delete(fullKey);
            else batch.Set(fullKey, new ValueHash256(value));
        }
        RootHash = TrieUpdater.UpdateRoot(_store, RootHash, batch.Build());
        return RootHash;
    }

    public bool TryGetNode(IPbtNodePath path, out byte[]? encoding)
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
            result[index] = Convert.ToHexString(record.Path.Encode()) + Convert.ToHexString(record.Encoding.Span);
        }
        return result;
    }
}

internal static class PbtStoreTestExtensions
{
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

    internal static byte[]? GetNode(this IPbtStore store, IPbtNodePath path)
    {
        PbtNodeGroupLocation location = PbtFourLevelGroupGeometry.Locate(path);
        using RefCountingMemory? payload = store.GetNodeGroup(location.GroupKey);
        if (payload is null) return null;
        PbtNodeGroupReader reader = new(location.GroupKey, payload.GetSpan());
        return reader.TryGetNode(location.Position, out ReadOnlySpan<byte> encoding) ? encoding.ToArray() : null;
    }

    internal static void SetNode(this IPbtStore store, IPbtNodePath path, byte[]? encoding,
        IRefCountingMemoryProvider? memoryProvider = null)
    {
        PbtNodeGroupLocation location = PbtFourLevelGroupGeometry.Locate(path);
        using RefCountingMemory? priorPayload = store.GetNodeGroup(location.GroupKey);
        List<PbtNodeRecord> records = [];
        if (priorPayload is not null)
        {
            PbtNodeGroupReader reader = new(location.GroupKey, priorPayload.GetSpan());
            PbtNodeGroupReader.Enumerator enumerator = reader.EnumerateNodes();
            while (enumerator.MoveNext())
            {
                if (enumerator.CurrentPosition != location.Position)
                    records.Add(new PbtNodeRecord(PbtFourLevelGroupGeometry.PathOf(location.GroupKey, enumerator.CurrentPosition), enumerator.Current));
            }
        }
        if (encoding is not null) records.Add(new PbtNodeRecord(path, encoding));
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
        foreach (PbtPhysicalPayload payload in store.ExportPhysicalPayloads())
        {
            IPbtNodePath groupKey = PbtStorageNodePath.Decode(payload.Key.Span);
            PbtNodeGroupReader reader = new(groupKey, payload.Payload.Span);
            PbtNodeGroupReader.Enumerator enumerator = reader.EnumerateNodes();
            while (enumerator.MoveNext())
                records.Add(new PbtNodeRecord(PbtFourLevelGroupGeometry.PathOf(groupKey, enumerator.CurrentPosition), enumerator.Current));
        }
        records.Sort(static (left, right) => left.Path.CompareTo(right.Path));
        return records;
    }

    internal static int CountNodeChanges(this IPbtStore store, IPbtNodePath groupKey, RefCountingMemory? payload)
    {
        using RefCountingMemory? priorPayload = store.GetNodeGroup(groupKey);
        PbtNodeGroupReader priorReader = priorPayload is null ? default : new(groupKey, priorPayload.GetSpan());
        PbtNodeGroupReader replacementReader = payload is null ? default : new(groupKey, payload.GetSpan());
        int changes = 0;
        for (int position = 0; position < PbtFourLevelGroupGeometry.PositionCount; position++)
        {
            if (position == PbtFourLevelGroupGeometry.RootPosition && groupKey.BitDepth != 0) continue;
            ReadOnlySpan<byte> priorEncoding = default;
            ReadOnlySpan<byte> replacementEncoding = default;
            if (priorPayload is not null) priorReader.TryGetNode(position, out priorEncoding);
            if (payload is not null) replacementReader.TryGetNode(position, out replacementEncoding);
            if (!priorEncoding.SequenceEqual(replacementEncoding)) changes++;
        }
        return changes;
    }
}
