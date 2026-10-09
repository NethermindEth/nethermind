// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FastEnumUtility;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Memory;
using Nethermind.Core.Threading;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.Snapshot;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

/// <summary>Drives the partitioned <see cref="TrieUpdater"/> over a persistent node-group store.</summary>
internal sealed class PbtTreeHarness : IDisposable
{
    private PbtNodeGroupStore _store = new();

    /// <summary>A fresh processor-count fold quota, so concurrently running fixtures never starve each other.</summary>
    public static ConcurrencyController FoldQuota() => new(Environment.ProcessorCount);

    /// <summary>A fan-out with one worker minimum whatever the subtree size.</summary>
    public static FoldFanOut FanOut(int minOperationsPerWorker) => new(minOperationsPerWorker, long.MaxValue, minOperationsPerWorker);

    public static readonly FoldFanOut DefaultFanOut = FoldFanOut.Default;

    /// <summary>The code chunks of <paramref name="code"/>, copied out of the pooled memory <see cref="PbtKeyDerivation.ChunkifyCode(ReadOnlySpan{byte})"/> returns.</summary>
    public static byte[] ChunkifyCode(ReadOnlySpan<byte> code)
    {
        using RefCountingMemory chunks = PbtKeyDerivation.ChunkifyCode(code);
        return chunks.GetSpan().ToArray();
    }

    /// <summary>Encodes a single-leaf tree's root leaf.</summary>
    public static byte[] EncodeLeaf<TKey>(TKey key) where TKey : struct, IPbtKey<TKey>
    {
        byte[] encoding = new byte[PbtNodeCodec.LeafLength(key.Length)];
        PbtNodeCodec.EncodeLeaf(encoding, key);
        return encoding;
    }

    /// <summary>The hash of a branch encoding: BLAKE3 over its preimage.</summary>
    public static ValueHash256 HashBranch(ReadOnlySpan<byte> encoding) => Blake3Hash.Hash(PbtBranchReader.FromValidated(encoding).Preimage);

    /// <summary>Hashes a leaf from its complete key and 32-byte value, per EIP-8297.</summary>
    public static ValueHash256 HashLeaf(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        byte[] preimage = new byte[PbtNodeCodec.LeafPreimageLength(key.Length)];
        PbtNodeCodec.WriteLeafPreimage(preimage, key, value);
        return Blake3Hash.Hash(preimage);
    }

    /// <summary>Encodes a branch whose children are both branches.</summary>
    public static byte[] EncodeBranch(ReadOnlySpan<byte> prefix, int bitCount, in ValueHash256 left, in ValueHash256 right) =>
        EncodeBranch(prefix, bitCount, left, right, [], []);

    /// <summary>Encodes a branch; a non-empty key, past <see cref="PbtNodeCodec.InlineKeyOffset"/>, inlines that child as a leaf.</summary>
    public static byte[] EncodeBranch(ReadOnlySpan<byte> prefix, int bitCount, in ValueHash256 left, in ValueHash256 right,
        ReadOnlySpan<byte> leftKey, ReadOnlySpan<byte> rightKey)
    {
        if (prefix.Length != PbtBitPrefix.ByteCount(bitCount)) throw new ArgumentException("Prefix length does not match its bit count.", nameof(prefix));
        byte[] encoding = new byte[PbtNodeCodec.BranchLength(bitCount, leftKey.Length, rightKey.Length)];
        PbtNodeCodec.CreateBranchEncoding(encoding, bitCount, left, right);
        prefix.CopyTo(encoding.AsSpan(3));
        PbtNodeCodec.WriteBranchTrailer(encoding.AsSpan(PbtNodeCodec.BranchPreimageLength(bitCount)), leftKey, rightKey);
        PbtNodeCodec.ThrowIfNotExact(encoding);
        return encoding;
    }

    public ValueHash256 RootHash { get; private set; }

    public IReadOnlyList<PbtNodeRecord> Nodes => _store.EnumerateRecords();
    public IReadOnlyList<PbtPhysicalPayload> PhysicalPayloads => _store.ExportPhysicalPayloads();

    public ValueHash256 ApplyBatch(IEnumerable<(byte[] Key, byte[]? Value)> writes) => RootHash = _store.Fold(RootHash, writes);

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

    public string[] CanonicalRecords() => _store.CanonicalRecords();
}

/// <summary>Folds every batch serially and with the fan-out and quota under test, each on its own store, and asserts identical roots and groups matching the reference tree.</summary>
internal sealed class DifferentialTree(FoldFanOut fanOut, ConcurrencyController foldQuota) : IDisposable
{
    private readonly PbtNodeGroupStore _serialStore = new();
    private readonly PbtNodeGroupStore _parallelStore = new();
    private readonly EipReferenceTree _oracle = new();
    private ValueHash256 _parallelRoot;
    private int _round;

    /// <summary>The root of the serially folded store.</summary>
    public ValueHash256 Root { get; private set; }

    /// <summary>The group payloads of the serially folded store.</summary>
    public IReadOnlyList<PbtPhysicalPayload> ExportPhysicalPayloads() => _serialStore.ExportPhysicalPayloads();

    public void Apply(IEnumerable<(byte[] Key, byte[]? Value)> writes)
    {
        string round = $"round {_round++}";
        _oracle.Apply(writes);

        // A quota of one folds every zone and frame on the calling thread.
        Root = _serialStore.Fold(Root, writes, new ConcurrencyController(1), PbtTreeHarness.DefaultFanOut, null);
        _parallelRoot = _parallelStore.Fold(_parallelRoot, writes, foldQuota, fanOut, null);

        IReadOnlyList<PbtPhysicalPayload> expected = _serialStore.ExportPhysicalPayloads();
        IReadOnlyList<PbtPhysicalPayload> actual = _parallelStore.ExportPhysicalPayloads();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_parallelRoot, Is.EqualTo(Root), round);
            Assert.That(Root.Bytes.ToArray(), Is.EqualTo(_oracle.Merkelize()), round);
            Assert.That(actual.Select(Describe), Is.EqualTo(expected.Select(Describe)), round);
        }
        PbtStoreTestExtensions.AssertSubtreeBytes(actual);
    }

    public void Dispose()
    {
        _serialStore.Dispose();
        _parallelStore.Dispose();
    }

    private static string Describe(PbtPhysicalPayload payload) =>
        $"{payload.Key.ToEncodedArray().ToHexString()}:{payload.Payload.Span.ToHexString()}";
}

internal static class PbtStoreTestExtensions
{
    internal static long[] ReadDescendantBytes(ReadOnlySpan<byte> payload)
    {
        ushort descendantMask = PbtNodeGroupCodec.ReadDescendantMask(payload);
        long[] descendantBytes = new long[PbtFourLevelGroupGeometry.BoundarySlots];
        for (int slot = 0; slot < descendantBytes.Length; slot++)
            descendantBytes[slot] = (descendantMask & (1 << slot)) == 0 ? 0 : PbtNodeGroupCodec.ReadDescendantBytes(payload, descendantMask, slot);
        return descendantBytes;
    }

    internal static PbtWriteOperation<TKey>[] ConsumeOperations<TKey>(this PbtWriteBatch<TKey> batch) where TKey : struct, IPbtKey<TKey>
    {
        batch.Consume(out ArrayPoolList<PbtWriteOperation<TKey>> operations, out ArrayPoolList<int> table);
        using ArrayPoolList<PbtWriteOperation<TKey>> operationsLease = operations;
        using ArrayPoolList<int> tableLease = table;
        return operations.AsSpan().ToArray();
    }

    internal static void Write<TPath>(this PbtNodeGroupWriter<TPath> writer, scoped in PbtTraversalPath path, int position, ReadOnlySpan<byte> encoding)
        where TPath : struct, IPbtNodePath<TPath>
    {
        encoding.CopyTo(writer.GetSpan(position, encoding.Length));
        writer.Commit(path);
    }

    internal static int NodeGroupCount(this PbtSnapshotContent content) =>
        content.AccountNodeGroups.Count + content.CodeNodeGroups.Count + content.StorageNodeGroups.Count;

    internal static RefCountingMemory? GetNodeGroup<TPath>(this IPbtStore store, TPath groupKey, in ValueHash256 groupHash)
        where TPath : struct, IPbtNodePath<TPath>
    {
        PbtTraversalPath cursor = PbtTraversalPath.FromPath(stackalloc byte[PbtVariableTreeKey.MaxLength], groupKey);
        return store.GetNodeGroup(cursor, groupHash);
    }

    internal static void SetNodeGroup<TPath>(this IPbtStore store, TPath groupKey, in ValueHash256 groupHash, RefCountingMemory? payload)
        where TPath : struct, IPbtNodePath<TPath>
    {
        PbtTraversalPath cursor = PbtTraversalPath.FromPath(stackalloc byte[PbtVariableTreeKey.MaxLength], groupKey);
        store.SetNodeGroup(cursor, groupHash, payload);
    }

    internal static void SetNodeGroup(this IPbtStore store, scoped in PbtTraversalPath groupKey, in ValueHash256 groupHash, RefCountingMemory? payload)
    {
        using IPbtConcurrentWriter writer = store.CreateWriter();
        writer.SetNodeGroup(groupKey, groupHash, payload);
    }

    internal static GroupFrameReader<PbtVariableTreeKey, PbtStorageNodePath> ReadGroup<TPath>(TPath groupKey, ReadOnlySpan<byte> payload)
        where TPath : struct, IPbtNodePath<TPath>
    {
        PbtTraversalPath cursor = PbtTraversalPath.FromPath(stackalloc byte[PbtVariableTreeKey.MaxLength], groupKey);
        PbtNodeGroupCodec.ValidateNodes(cursor, payload);
        return new(RefCountingMemory.Wrapping(payload.ToArray()), groupKey.BitDepth);
    }

    internal static List<(int Position, ReadOnlyMemory<byte> Encoding)> Nodes(this GroupFrameReader<PbtVariableTreeKey, PbtStorageNodePath> reader)
    {
        List<(int Position, ReadOnlyMemory<byte> Encoding)> nodes = [];
        for (uint stored = reader.StoredPositions; stored != 0; stored &= stored - 1)
        {
            int position = BitOperations.TrailingZeroCount(stored);
            nodes.Add((position, reader.GetEncoding(position)));
        }
        return nodes;
    }

    /// <summary>Asserts every stored descendant size equals the summed payload lengths of the groups keyed below that boundary slot.</summary>
    internal static void AssertSubtreeBytes(IReadOnlyList<PbtPhysicalPayload> payloads)
    {
        using (Assert.EnterMultipleScope())
        {
            foreach (PbtPhysicalPayload group in payloads)
            {
                int groupDepth = group.Key.BitDepth;
                long[] expected = new long[PbtFourLevelGroupGeometry.BoundarySlots];
                foreach (PbtPhysicalPayload candidate in payloads)
                {
                    if (candidate.Key.BitDepth <= groupDepth || !candidate.Key.Prefix(groupDepth).Equals(group.Key)) continue;
                    int slot = (candidate.Key.GetByte(groupDepth >> 3) >> (4 - (groupDepth & 4))) & 0xF;
                    expected[slot] += candidate.Payload.Length;
                }
                long[] stored = ReadDescendantBytes(group.Payload.Span);
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
            PbtNodeGroupLocation<PbtStorageNodePath> location = PbtTestPaths.Locate(path);
            byte[]? encoding = null;
            foreach (PbtPhysicalPayload physical in groups)
                if (physical.Key.Equals(location.GroupKey))
                    encoding = ResolveNode(PbtStoreTestExtensions.ReadGroup(location.GroupKey, physical.Payload.Span), location.GroupKey, location.Position);
            // A root leaf's hash is not derivable from its encoding, and the test store ignores group hashes anyway.
            if (encoding is null || encoding[0] == 0) return default;
            PbtBranchReader node = PbtBranchReader.FromValidated(encoding);
            int branchDepth = path.BitDepth + node.Prefix.BitCount;
            for (int bit = path.BitDepth; bit < Math.Min(branchDepth, groupKey.BitDepth); bit++)
                if (TrieUpdater.GetBit(node.Prefix.Bytes, bit - path.BitDepth) != groupKey.GetBit(bit)) return default;
            if (branchDepth >= groupKey.BitDepth)
            {
                int prefixBits = branchDepth - groupKey.BitDepth;
                byte[] prefix = new byte[(prefixBits + 7) / 8];
                for (int bit = 0; bit < prefixBits; bit++)
                    prefix[bit / 8] |= (byte)(TrieUpdater.GetBit(node.Prefix.Bytes, groupKey.BitDepth - path.BitDepth + bit) << (7 - bit % 8));
                return PbtTreeHarness.HashBranch(PbtTreeHarness.EncodeBranch(prefix, prefixBits, node.LeftHash, node.RightHash));
            }
            int direction = groupKey.GetBit(branchDepth);
            // An inline leaf has no group below it.
            if (!(direction == 0 ? node.LeftKeyPostfix : node.RightKeyPostfix).IsEmpty) return default;
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
        PbtNodePathOperations.CopyTo(path, bytes);
        return bytes;
    }

    internal static int GetBit<TPath>(this TPath path, int bitIndex) where TPath : struct, IPbtNodePath<TPath> =>
        TrieUpdater.GetBit(path.ToPathArray(), bitIndex);

    /// <summary>The path's capacity-independent identity as bytes: its big-endian depth, then its canonical bytes.</summary>
    internal static byte[] ToEncodedArray<TPath>(this TPath path) where TPath : struct, IPbtNodePath<TPath>
    {
        byte[] encoding = new byte[4 + ((path.BitDepth + 7) >> 3)];
        BinaryPrimitives.WriteInt32BigEndian(encoding, path.BitDepth);
        PbtNodePathOperations.CopyTo(path, encoding.AsSpan(4));
        return encoding;
    }

    internal static byte[] ToStorageKey<TPath>(this TPath path, PbtColumns column) where TPath : struct, IPbtNodePath<TPath> =>
        column == PbtColumns.Metadata
            ? PbtRocksDbPersistence.RootNodeGroupKey.ToArray()
            : PbtNodeGroupKey.Encode(path, new byte[PbtNodeGroupKey.MaxLength]).ToArray();

    internal static List<PbtStorageNodePath> PersistedNodeGroupKeys(IColumnsDb<PbtColumns> db)
    {
        List<PbtStorageNodePath> groupKeys = [];
        if (db.GetColumnDb(PbtColumns.Metadata).Get(PbtRocksDbPersistence.RootNodeGroupKey) is not null) groupKeys.Add(new PbtStorageNodePath([], 0));
        foreach (PbtColumns column in (PbtColumns[])[PbtColumns.TopNodeGroups, PbtColumns.AccountNodeGroups, PbtColumns.CodeNodeGroups, PbtColumns.StorageNodeGroups])
            groupKeys.AddRange(db.GetColumnDb(column).GetAllKeys().Select(key => PbtNodeGroupKey.Decode(key)));
        return groupKeys;
    }

    /// <summary>Each group as key-then-payload hex, sorted ordinally.</summary>
    internal static string[] CanonicalGroups(IEnumerable<PbtStorageNodePath> groupKeys, Func<PbtStorageNodePath, RefCountingMemory?> getNodeGroup)
    {
        List<string> result = [];
        foreach (PbtStorageNodePath groupKey in groupKeys)
        {
            using RefCountingMemory? payload = getNodeGroup(groupKey);
            result.Add($"{Convert.ToHexString(groupKey.ToEncodedArray())}:{Convert.ToHexString(payload!.GetSpan())}");
        }
        result.Sort(StringComparer.Ordinal);
        return [.. result];
    }

    /// <summary>Asserts the groups persisted in <paramref name="db"/>, read through <paramref name="reader"/>, are exactly those of <paramref name="expected"/>.</summary>
    internal static void AssertSameGroups(IColumnsDb<PbtColumns> db, IPbtPersistence.IReader reader, PbtNodeGroupStore expected, string message) =>
        Assert.That(CanonicalGroups(PersistedNodeGroupKeys(db), reader.GetNodeGroup),
            Is.EqualTo(CanonicalGroups(expected.EnumerateNodeGroupKeys(), expected.GetPhysicalNodeGroup)), message);

    /// <inheritdoc cref="AssertSameGroups(IColumnsDb{PbtColumns}, IPbtPersistence.IReader, PbtNodeGroupStore, string)"/>
    internal static void AssertSameGroups(IColumnsDb<PbtColumns> db, IPbtPersistence.IReader reader, PbtNodeGroupStore expected) =>
        Assert.That(CanonicalGroups(PersistedNodeGroupKeys(db), reader.GetNodeGroup),
            Is.EqualTo(CanonicalGroups(expected.EnumerateNodeGroupKeys(), expected.GetPhysicalNodeGroup)));

    /// <summary>Opens the on-disk PBT RocksDB at <paramref name="path"/> with the default <see cref="DbConfig"/> and the production column options for <paramref name="config"/>.</summary>
    internal static ColumnsDb<PbtColumns> OpenPbtRocksDb(string path, PbtConfig config)
    {
        DbConfig dbConfig = new();
        PbtRocksDbConfigAdjuster adjuster = new(Substitute.For<IRocksDbConfigFactory>(), dbConfig, config, Substitute.For<IDisposableStack>(), LimboLogs.Instance);
        return new ColumnsDb<PbtColumns>(path, new DbSettings(nameof(DbNames.Pbt), DbNames.Pbt), dbConfig, adjuster, LimboLogs.Instance, FastEnum.GetValues<PbtColumns>());
    }

    /// <summary>Zero-pads a zone prefix to the fixed <see cref="PbtStoragePath"/> or <see cref="PbtPath"/> length the partition fold requires.</summary>
    internal static byte[] ZoneKey(string hexPrefix)
    {
        byte[] prefix = Bytes.FromHexString(hexPrefix);
        byte[] key = new byte[prefix[0] == Eip8297KeyDerivation.StorageZone ? PbtStoragePath.KeyLength : PbtPath.KeyLength];
        prefix.CopyTo(key, 0);
        return key;
    }

    /// <summary>A 32-byte leaf value whose last byte is <paramref name="marker"/>.</summary>
    internal static byte[] Value(byte marker)
    {
        byte[] value = new byte[32];
        value[^1] = marker;
        return value;
    }

    /// <summary>Folds zone-key <paramref name="writes"/> into the tree at <paramref name="root"/> through the partitioned driver production folds with.</summary>
    internal static ValueHash256 Fold(this IPbtStore store, in ValueHash256 root, IEnumerable<(byte[] Key, byte[]? Value)> writes) =>
        store.Fold(root, writes, PbtTreeHarness.DefaultFanOut, null);

    /// <inheritdoc cref="Fold(IPbtStore, in ValueHash256, IEnumerable{ValueTuple{byte[], byte[]}})"/>
    internal static ValueHash256 Fold(this IPbtStore store, in ValueHash256 root, IEnumerable<(byte[] Key, byte[]? Value)> writes,
        FoldFanOut fanOut, IRefCountingMemoryProvider? memoryProvider) =>
        store.Fold(root, writes, PbtTreeHarness.FoldQuota(), fanOut, memoryProvider);

    /// <inheritdoc cref="Fold(IPbtStore, in ValueHash256, IEnumerable{ValueTuple{byte[], byte[]}})"/>
    internal static ValueHash256 Fold(this IPbtStore store, in ValueHash256 root, IEnumerable<(byte[] Key, byte[]? Value)> writes,
        ConcurrencyController foldQuota, FoldFanOut fanOut, IRefCountingMemoryProvider? memoryProvider)
    {
        using PbtPartitionBatches changes = PreparePartitions(writes);
        return TrieUpdater.UpdateRoot(store, root, changes, foldQuota, fanOut, null, memoryProvider);
    }

    internal static PbtPartitionBatches PreparePartitions(IEnumerable<(byte[] Key, byte[]? Value)> changes)
    {
        using PbtWriteBatchBuilder<PbtPath> account = new();
        using PbtWriteBatchBuilder<PbtPath> code = new();
        using PbtWriteBatchBuilder<PbtStoragePath> storage = new();
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
        if (value is null) builder.SetLeaf(key, null);
        else builder.Set(key, new ValueHash256(value));
    }

    internal static byte[]? GetNode<TPath>(this PbtNodeGroupStore store, TPath path) where TPath : struct, IPbtNodePath<TPath>
    {
        PbtStorageNodePath currentPath = new([], 0);
        while (currentPath.BitDepth <= path.BitDepth)
        {
            byte[]? encoding = GetLogicalNode(store, currentPath);
            if (encoding is null || PbtNodePathOperations.Equal(currentPath, path)) return encoding;
            if (PbtNodeCodec.IsLeaf(encoding)) return null;
            PbtBranchReader node = PbtBranchReader.FromValidated(encoding);
            if (currentPath.BitDepth + node.Prefix.BitCount >= path.BitDepth) return null;
            int directionBit = currentPath.BitDepth + node.Prefix.BitCount;
            int direction = path.GetBit(directionBit);
            if (!(direction == 0 ? node.LeftKeyPostfix : node.RightKeyPostfix).IsEmpty) return null;
            currentPath = currentPath.Append(node.Prefix, direction);
            for (int bit = 0; bit < currentPath.BitDepth; bit++)
                if (currentPath.GetBit(bit) != path.GetBit(bit)) return null;
        }
        return null;
    }

    private static byte[]? GetLogicalNode<TPath>(PbtNodeGroupStore store, TPath path) where TPath : struct, IPbtNodePath<TPath>
    {
        PbtNodeGroupLocation<TPath> location = PbtTestPaths.Locate(path);
        using RefCountingMemory? payload = store.GetPhysicalNodeGroup(location.GroupKey);
        if (payload is null) return null;
        GroupFrameReader<PbtVariableTreeKey, PbtStorageNodePath> reader = PbtStoreTestExtensions.ReadGroup(location.GroupKey, payload.GetSpan());
        return ResolveNode(reader, location.GroupKey, location.Position);
    }

    private static byte[]? ResolveNode<TPath>(GroupFrameReader<PbtVariableTreeKey, PbtStorageNodePath> reader, TPath groupKey, int position) where TPath : struct, IPbtNodePath<TPath>
    {
        ReadOnlySpan<byte> encoding = reader.GetEncoding(position).Span;
        if (!encoding.IsEmpty) return encoding.ToArray();
        TPath path = PbtTestPaths.PathOf(groupKey, position);
        int relativeDepth = path.BitDepth - groupKey.BitDepth;
        if (relativeDepth is < 1 or > 3) return null;
        int width = 1 << (4 - relativeDepth);
        byte[]? left = ResolveNode(reader, groupKey, position - width);
        byte[]? right = ResolveNode(reader, groupKey, position - 1);
        return left is null || right is null ? null : PbtTreeHarness.EncodeBranch([], 0,
            PbtTreeHarness.HashBranch(left), PbtTreeHarness.HashBranch(right));
    }

    internal static void SetNode<TPath>(this PbtNodeGroupStore store, TPath path, byte[]? encoding,
        IRefCountingMemoryProvider? memoryProvider = null) where TPath : struct, IPbtNodePath<TPath>
    {
        PbtNodeGroupLocation<TPath> location = PbtTestPaths.Locate(path);
        using RefCountingMemory? priorPayload = store.GetPhysicalNodeGroup(location.GroupKey);
        List<PbtNodeRecord> records = [];
        if (priorPayload is not null)
        {
            foreach ((int position, ReadOnlyMemory<byte> node) in PbtStoreTestExtensions.ReadGroup(location.GroupKey, priorPayload.GetSpan()).Nodes())
            {
                if (position != location.Position)
                    records.Add(new PbtNodeRecord(PbtTestPaths.PathOf(location.GroupKey, position).ToPath<PbtStorageNodePath>(), node.Span));
            }
        }
        if (encoding is not null) records.Add(new PbtNodeRecord(path.ToPath<PbtStorageNodePath>(), encoding));
        if (records.Count == 0)
        {
            store.SetNodeGroup(location.GroupKey, default, null);
            return;
        }

        using RefCountingMemory payload = PbtNodeGroupEncoder.EncodeToMemory(location.GroupKey, records, memoryProvider ?? PooledRefCountingMemoryProvider.Instance);
        store.SetNodeGroup(location.GroupKey, encoding is null || encoding[0] == 0 ? default : PbtTreeHarness.HashBranch(encoding), payload);
    }

    /// <summary>The store's canonical records as path-then-encoding hex, in path order.</summary>
    internal static string[] CanonicalRecords(this PbtNodeGroupStore store)
    {
        IReadOnlyList<PbtNodeRecord> records = store.EnumerateRecords();
        string[] result = new string[records.Count];
        for (int index = 0; index < result.Length; index++)
        {
            PbtNodeRecord record = records[index];
            result[index] = Convert.ToHexString(record.Path.ToEncodedArray()) + Convert.ToHexString(record.Encoding.Span);
        }
        return result;
    }

    /// <summary>The physical group payloads as key-then-payload hex, sorted ordinally.</summary>
    internal static string[] PhysicalRecords(this IEnumerable<PbtPhysicalPayload> payloads)
    {
        List<string> records = [];
        foreach (PbtPhysicalPayload payload in payloads)
            records.Add(Convert.ToHexString(payload.Key.ToEncodedArray()) + Convert.ToHexString(payload.Payload.Span));
        records.Sort(StringComparer.Ordinal);
        return [.. records];
    }

    /// <inheritdoc cref="PhysicalRecords(IEnumerable{PbtPhysicalPayload})"/>
    internal static string[] PhysicalRecords(this PbtNodeGroupStore store) => store.ExportPhysicalPayloads().PhysicalRecords();

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
            if (PbtNodeCodec.IsLeaf(encoding)) continue;
            PbtBranchReader node = PbtBranchReader.FromValidated(encoding);
            if (node.LeftKeyPostfix.IsEmpty) pending.Push(path.Append(node.Prefix, 0));
            if (node.RightKeyPostfix.IsEmpty) pending.Push(path.Append(node.Prefix, 1));
        }
        records.Sort(static (left, right) => left.Path.CompareTo(right.Path));
        return records;
    }
}
