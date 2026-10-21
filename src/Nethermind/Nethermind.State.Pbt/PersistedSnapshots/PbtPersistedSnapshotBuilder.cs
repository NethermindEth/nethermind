// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Pbt;
using Nethermind.Pbt.NodeBuffer;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Io;
using Nethermind.State.Flat.PersistedSnapshots.Sorted;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.State.Pbt.Common;
using Nethermind.State.Pbt.Snapshot;

namespace Nethermind.State.Pbt.PersistedSnapshots;

public readonly record struct PbtPersistedMetadata(StateId From, StateId To, ValueHash256 TreeRoot);

public static class PbtPersistedSnapshotBuilder
{
    private readonly record struct Entry(byte[] Key, object? Value);

    public static long EstimateSize(PbtSnapshot snapshot)
    {
        PbtSnapshotContent c = snapshot.Content;
        long entries = c.Accounts.Count + c.HeaderStorages.Count + c.Storages.Count + c.Codes.Count
            + c.SelfDestructedStorageAddresses.Count + c.NodeGroupCount;
        // Each inline record has at most 255 value bytes and 72 key bytes. Account for block/restart
        // overhead and ownership/chunk records conservatively rather than under-mapping the arena.
        long bytes = snapshot.PayloadSize.Leaf + snapshot.PayloadSize.Node;
        return checked(16384 + entries * 512 + (bytes / PbtPersistedFormat.ChunkSize + entries) * 512);
    }

    public static long EstimateBlobSize(PbtSnapshot snapshot)
    {
        // Page padding can at most double a short payload; large chunks have no page padding.
        // Include per-entity RLP headers even when payload sizing counted only logical bytes.
        long entries = (long)snapshot.Content.HeaderStorages.Count + snapshot.Content.Storages.Count
            + snapshot.Content.Codes.Count + snapshot.Content.NodeGroupCount;
        return checked(4096 + 2 * (snapshot.PayloadSize.Leaf + snapshot.PayloadSize.Node) + entries * 16);
    }

    public static void Build<TWriter>(PbtSnapshot snapshot, ref TWriter writer, BlobArenaWriter blobs, BloomFilter bloom)
        where TWriter : IByteBufferWriter
    {
        List<Entry> entries = [];
        PbtSnapshotContent c = snapshot.Content;
        foreach ((ValueHash256 key, PbtAccount? value) in c.Accounts) entries.Add(new(PbtPersistedKey.AddressEntity(key, PbtPersistedKey.Account), value));
        foreach ((HashedKey<PbtPath> key, PackedSlotRun value) in c.HeaderStorages) entries.Add(new(PbtPersistedKey.Run(key.Key), value));
        foreach ((HashedKey<PbtStoragePath> key, PackedSlotRun value) in c.Storages) entries.Add(new(PbtPersistedKey.Run(key.Key), value));
        foreach ((ValueHash256 key, CodeInfo value) in c.Codes) entries.Add(new(PbtPersistedKey.CodeEntity(key), value));
        foreach ((ValueHash256 key, bool value) in c.SelfDestructedStorageAddresses) entries.Add(new(PbtPersistedKey.AddressEntity(key, PbtPersistedKey.Clear), value));
        entries.Sort(static (a, b) => a.Key.AsSpan().SequenceCompareTo(b.Key));
        // Group families sort after every other entity, and the tree walk emits each family in key order.
        List<Entry>[] groupFamilies = new List<Entry>[PbtPersistedKey.StorageGroup - PbtPersistedKey.RootGroup + 1];
        for (int family = 0; family < groupFamilies.Length; family++) groupFamilies[family] = [];
        foreach (PbtGroupRecord group in c.EnumerateNodeGroups())
        {
            byte[] key = PbtPersistedKey.Group(group.Path);
            groupFamilies[key[0] - PbtPersistedKey.RootGroup].Add(new(key, group.Payload.ToArray()));
        }
        foreach (List<Entry> family in groupFamilies) entries.AddRange(family);
        SortedTableBuilder<TWriter> table = new(ref writer);
        try
        {
            table.Add(PbtPersistedKey.Owner(blobs.BlobArenaId), [1]);
            foreach (Entry entry in entries)
            {
                bloom.AddUnsynchronized(PbtPersistedKey.BloomHash(entry.Key));
                byte[]? payload = Encode(entry.Value);
                WriteEntity(ref table, entry.Key, payload, blobs);
            }
            PbtPersistedFormat.WriteMetadata(ref table, new(snapshot.From, snapshot.To, snapshot.TreeRoot));
            table.Build();
        }
        finally { table.Dispose(); }
    }

    private static byte[]? Encode(object? value)
    {
        switch (value)
        {
            case null: return null;
            case bool clear: return [clear ? (byte)1 : (byte)0];
            case PbtAccount account:
                byte[] accountBytes = new byte[account.EncodedLength];
                account.Encode(accountBytes);
                return accountBytes;
            case PackedSlotRun run:
                if (run.Count == 0) return [];
                byte[] runBytes = new byte[run.EncodedLength];
                run.Encode(runBytes);
                return runBytes;
            case CodeInfo code: return code.CodeSpan.ToArray();
            case byte[] group: return group;
            default: throw new InvalidOperationException("Unsupported persisted PBT payload.");
        }
    }

    private static void WriteEntity<TWriter>(ref SortedTableBuilder<TWriter> table, ReadOnlySpan<byte> key,
        byte[]? payload, BlobArenaWriter blobs) where TWriter : IByteBufferWriter
    {
        PbtPersistedKey.ValidateDescriptor(key);
        if (payload is null)
        {
            table.Add(key, [PbtPersistedFormat.NullMarker]);
            return;
        }
        if (payload.Length <= PbtPersistedFormat.InlineLimit)
        {
            Span<byte> value = stackalloc byte[PbtPersistedFormat.InlineLimit + 1];
            value[0] = PbtPersistedFormat.InlineMarker;
            payload.CopyTo(value[1..]);
            table.Add(key, value[..(payload.Length + 1)]);
            return;
        }
        if (PbtPersistedKey.IsGroup(key))
        {
            Span<byte> blob = stackalloc byte[PbtPersistedFormat.BlobDescriptorLength];
            PbtPersistedFormat.WriteBlobDescriptor(blob, payload.Length, blobs.WriteRlp(EncodeChunk(payload)));
            table.Add(key, blob);
            return;
        }
        uint count = PbtPersistedFormat.ChunkCount(payload.LongLength);
        Span<byte> descriptor = stackalloc byte[PbtPersistedFormat.ChunkedDescriptorLength];
        PbtPersistedFormat.WriteChunkedDescriptor(descriptor, payload.Length, count);
        table.Add(key, descriptor);
        Span<byte> nodeRefBytes = stackalloc byte[NodeRef.Size];
        for (uint i = 0; i < count; i++)
        {
            int start = checked((int)(i * PbtPersistedFormat.ChunkSize));
            ReadOnlySpan<byte> chunk = payload.AsSpan(start, Math.Min(PbtPersistedFormat.ChunkSize, payload.Length - start));
            NodeRef reference = blobs.WriteRlp(EncodeChunk(chunk));
            NodeRef.Write(nodeRefBytes, reference);
            table.Add(PbtPersistedKey.Chunk(key, i), nodeRefBytes);
        }
    }

    private static byte[] EncodeChunk(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is <= 0 or > PbtPersistedFormat.ChunkSize) throw new InvalidDataException("Invalid persisted PBT chunk length.");
        return Rlp.Encode(payload).Bytes;
    }
}
