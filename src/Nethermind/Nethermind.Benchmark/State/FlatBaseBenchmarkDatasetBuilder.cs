// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading.Tasks;
using Nethermind.Benchmarks.State.FlatBase;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat;

namespace Nethermind.Benchmarks.State;

/// <summary>
/// Parameters and on-disk layout of the flat-base point-read dataset. Resolved from
/// <c>NETH_FLATBENCH_SCALE</c> (<c>smoke</c> — default, 100k accounts / 500k slots — or <c>full</c>,
/// 300M accounts / 1.2B slots) and <c>NETH_FLATBENCH_DIR</c> (dataset root, defaults to a temp dir).
/// </summary>
public sealed class FlatBaseDatasetSpec
{
    public const string SmokeScale = "smoke";
    public const string FullScale = "full";
    private const int FormatVersion = 1;

    public string Scale { get; private init; }
    public long AccountCount { get; private init; }
    public int SlotsPerAccount { get; private init; }
    public int ShardCount { get; private init; }
    public string Root { get; private init; }

    public long SlotCount => AccountCount * SlotsPerAccount;
    public string DatasetDir => Path.Combine(Root, Scale);
    public string RocksDbDir => Path.Combine(DatasetDir, "rocksdb");
    public string ArenaDir => Path.Combine(DatasetDir, "arena");
    public string LmdbDir => Path.Combine(DatasetDir, "lmdb");
    public string MarkerPath => Path.Combine(DatasetDir, "dataset.marker");

    public string MarkerText =>
        $"flatbase v{FormatVersion} scale={Scale} accounts={AccountCount} slotsPerAccount={SlotsPerAccount} shards={ShardCount}";

    /// <summary>Upper bound for the LMDB memory map. Virtual (sparse) on Linux; on Windows LMDB
    /// materializes the data file at this size, so it scales with the dataset instead of a fixed cap.</summary>
    public long LmdbMapSize => Math.Max(1L << 30, 2 * (AccountCount * 160 + SlotCount * 128));

    public static FlatBaseDatasetSpec FromEnvironment()
    {
        string scale = Environment.GetEnvironmentVariable("NETH_FLATBENCH_SCALE") ?? SmokeScale;
        string root = Environment.GetEnvironmentVariable("NETH_FLATBENCH_DIR")
                      ?? Path.Combine(Path.GetTempPath(), "neth-flatbench");

        return scale switch
        {
            SmokeScale => new FlatBaseDatasetSpec
            {
                Scale = scale,
                AccountCount = 100_000,
                SlotsPerAccount = 5,
                ShardCount = 256,
                Root = root,
            },
            FullScale => new FlatBaseDatasetSpec
            {
                Scale = scale,
                AccountCount = 300_000_000,
                SlotsPerAccount = 4,
                ShardCount = 256,
                Root = root,
            },
            _ => throw new NotSupportedException($"Unknown NETH_FLATBENCH_SCALE '{scale}' (expected '{SmokeScale}' or '{FullScale}')"),
        };
    }
}

/// <summary>
/// Deterministic synthetic dataset generator for the flat-base point-read benchmark: N accounts
/// (keccak(counter)-derived 20-byte keys, ~70-byte slim-RLP values) and N×slotsPerAccount storage
/// slots (52-byte production split keys, 32-byte values), written byte-identically to all three
/// backends (RocksDB flat columns, sharded sorted-table arena, LMDB). Built once per
/// scale under the dataset root and reused across runs via a parameter marker file.
/// </summary>
/// <remarks>
/// Key layouts replicate <c>BaseFlatPersistence</c>: accounts are keyed by the truncated 20-byte
/// address hash; slots by <c>[4B addrHash | 32B slotHash | 16B addrHash]</c>. Values derive from the
/// generation counter via SplitMix64, so a validation read can recompute the expected bytes.
/// Generation runs in three phases: (1) spill (counter, key) records per key-prefix shard,
/// (2) per shard: sort and bulk-load all three backends in globally ascending key order,
/// (3) byte-for-byte validation of sampled keys on every backend.
/// </remarks>
public static class FlatBaseBenchmarkDatasetBuilder
{
    public const int AccountKeyLength = 20;
    public const int StorageKeyLength = 52;
    public const int SlotValueLength = 32;
    private const int StoragePrefixLength = 4;
    private const int SpillRecordLength = sizeof(long) + AccountKeyLength;

    private const ulong NonceSalt = 0x5FD3_91C7_0A2B_11EFUL;
    private const ulong BalanceSalt = 0x9A64_2E85_77D0_43B1UL;
    private const ulong StorageRootSalt = 0x1B3C_58F2_D944_6E07UL;
    private const ulong CodeHashSalt = 0xC0DE_C0DE_1234_5678UL;
    private const ulong SlotHashSalt = 0x5107_4A5B_88ED_2C93UL;
    private const ulong SlotValueSalt = 0x5107_7A1E_0F66_B4D5UL;

    /// <summary>Build the dataset for the environment-selected spec unless a marker with matching
    /// parameters already exists; returns the spec either way.</summary>
    public static FlatBaseDatasetSpec EnsureBuilt()
    {
        FlatBaseDatasetSpec spec = FlatBaseDatasetSpec.FromEnvironment();
        if (File.Exists(spec.MarkerPath) && File.ReadAllText(spec.MarkerPath) == spec.MarkerText)
            return spec;

        if (Directory.Exists(spec.DatasetDir))
            Directory.Delete(spec.DatasetDir, recursive: true);
        Directory.CreateDirectory(spec.DatasetDir);

        Console.WriteLine($"[FlatBase] Building dataset: {spec.MarkerText} under {spec.DatasetDir}");
        Build(spec);
        File.WriteAllText(spec.MarkerPath, spec.MarkerText);
        Console.WriteLine("[FlatBase] Dataset build complete");
        return spec;
    }

    private static void Build(FlatBaseDatasetSpec spec)
    {
        string spillDir = Path.Combine(spec.DatasetDir, "spill");
        SpillAccountKeys(spec, spillDir);

        Directory.CreateDirectory(spec.ArenaDir);
        using (ShardedArenaWriter accountArena = new(
                   Path.Combine(spec.ArenaDir, ShardedSortedTableArena.AccountArenaFile),
                   Path.Combine(spec.ArenaDir, ShardedSortedTableArena.AccountDirFile),
                   spec.ShardCount))
        using (ShardedArenaWriter storageArena = new(
                   Path.Combine(spec.ArenaDir, ShardedSortedTableArena.StorageArenaFile),
                   Path.Combine(spec.ArenaDir, ShardedSortedTableArena.StorageDirFile),
                   spec.ShardCount))
        using (LmdbFlatBackend lmdb = LmdbFlatBackend.OpenWrite(spec.LmdbDir, spec.LmdbMapSize))
        using (RocksDbFlatBackend rocks = new(spec.RocksDbDir))
        {
            for (int shard = 0; shard < spec.ShardCount; shard++)
            {
                LoadShard(spillDir, shard, out byte[][] accountKeys, out long[] counters);
                int accountCount = accountKeys.Length;
                Array.Sort(accountKeys, counters, Bytes.Comparer);

                byte[][] accountValues = new byte[accountCount][];
                Parallel.For(0, accountCount, i => accountValues[i] = AccountValue(counters[i]));

                accountArena.WriteShard(shard, accountKeys, accountValues, accountCount);
                lmdb.PutShard(storage: false, accountKeys, accountValues, accountCount);
                rocks.WriteShard(FlatDbColumns.Account, accountKeys, accountValues, accountCount);

                int slotCount = accountCount * spec.SlotsPerAccount;
                byte[][] slotKeys = new byte[slotCount][];
                byte[][] slotValues = new byte[slotCount][];
                Parallel.For(0, accountCount, i =>
                {
                    Span<byte> slotHash = stackalloc byte[32];
                    for (int k = 0; k < spec.SlotsPerAccount; k++)
                    {
                        int index = i * spec.SlotsPerAccount + k;
                        byte[] key = new byte[StorageKeyLength];
                        WriteSlotHash(counters[i], k, slotHash);
                        WriteStorageKey(accountKeys[i], slotHash, key);
                        slotKeys[index] = key;

                        byte[] value = new byte[SlotValueLength];
                        WriteSlotValue(counters[i], k, value);
                        slotValues[index] = value;
                    }
                });
                Array.Sort(slotKeys, slotValues, Bytes.Comparer);

                storageArena.WriteShard(shard, slotKeys, slotValues, slotCount);
                lmdb.PutShard(storage: true, slotKeys, slotValues, slotCount);
                rocks.WriteShard(FlatDbColumns.Storage, slotKeys, slotValues, slotCount);

                if ((shard & 31) == 31)
                    Console.WriteLine($"[FlatBase] Loaded shard {shard + 1}/{spec.ShardCount}");
            }

            Console.WriteLine("[FlatBase] Flushing and compacting RocksDB");
            rocks.FinishWrites();
        }

        Directory.Delete(spillDir, recursive: true);

        Console.WriteLine("[FlatBase] Validating backends");
        using (RocksDbFlatBackend rocks = new(spec.RocksDbDir))
            Validate(spec, rocks, sampleCount: 1000);
        using (SortedArenaBackend arena = SortedArenaBackend.OpenRead(spec.ArenaDir))
            Validate(spec, arena, sampleCount: 1000);
        using (LmdbFlatBackend lmdb = LmdbFlatBackend.OpenRead(spec.LmdbDir, spec.LmdbMapSize))
            Validate(spec, lmdb, sampleCount: 1000);
    }

    /// <summary>Phase 1: stream all account counters, hash them (in parallel chunks) and append
    /// <c>[counter i64 LE][key 20B]</c> records to one spill file per key-prefix shard.</summary>
    private static void SpillAccountKeys(FlatBaseDatasetSpec spec, string spillDir)
    {
        Directory.CreateDirectory(spillDir);
        int shardShift = ShardedSortedTableArena.ShardShift(spec.ShardCount);
        FileStream[] spills = new FileStream[spec.ShardCount];
        try
        {
            for (int shard = 0; shard < spec.ShardCount; shard++)
                spills[shard] = new FileStream(SpillPath(spillDir, shard), FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 16);

            const int ChunkSize = 1 << 16;
            byte[] chunkKeys = new byte[ChunkSize * AccountKeyLength];
            byte[] record = new byte[SpillRecordLength];
            for (long chunkStart = 0; chunkStart < spec.AccountCount; chunkStart += ChunkSize)
            {
                int chunkCount = (int)Math.Min(ChunkSize, spec.AccountCount - chunkStart);
                Parallel.For(0, chunkCount, i =>
                    WriteAccountKey(chunkStart + i, chunkKeys.AsSpan(i * AccountKeyLength, AccountKeyLength)));

                for (int i = 0; i < chunkCount; i++)
                {
                    ReadOnlySpan<byte> key = chunkKeys.AsSpan(i * AccountKeyLength, AccountKeyLength);
                    BinaryPrimitives.WriteInt64LittleEndian(record, chunkStart + i);
                    key.CopyTo(record.AsSpan(sizeof(long)));
                    spills[ShardedSortedTableArena.ShardOf(key, shardShift)].Write(record);
                }
            }
        }
        finally
        {
            foreach (FileStream spill in spills)
                spill?.Dispose();
        }
    }

    private static void LoadShard(string spillDir, int shard, out byte[][] keys, out long[] counters)
    {
        byte[] data = File.ReadAllBytes(SpillPath(spillDir, shard));
        int count = data.Length / SpillRecordLength;
        keys = new byte[count][];
        counters = new long[count];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> record = data.AsSpan(i * SpillRecordLength, SpillRecordLength);
            counters[i] = BinaryPrimitives.ReadInt64LittleEndian(record);
            keys[i] = record[sizeof(long)..].ToArray();
        }
    }

    private static string SpillPath(string spillDir, int shard) => Path.Combine(spillDir, $"shard-{shard:D3}.spill");

    /// <summary>Read <paramref name="sampleCount"/> known account and slot keys plus a handful of
    /// guaranteed-miss keys and compare byte-for-byte against the recomputed expected values,
    /// throwing on any mismatch.</summary>
    public static void Validate(FlatBaseDatasetSpec spec, IFlatPointReadBackend backend, int sampleCount)
    {
        using IFlatReadSession session = backend.BeginSession();
        Span<byte> accountKey = stackalloc byte[AccountKeyLength];
        Span<byte> slotHash = stackalloc byte[32];
        Span<byte> storageKey = stackalloc byte[StorageKeyLength];
        Span<byte> slotExpected = stackalloc byte[SlotValueLength];
        Span<byte> buffer = stackalloc byte[256];

        for (int i = 0; i < sampleCount; i++)
        {
            long counter = (long)(SplitMix64((ulong)i) % (ulong)spec.AccountCount);
            WriteAccountKey(counter, accountKey);
            byte[] expected = AccountValue(counter);
            int length = session.GetAccount(accountKey, buffer);
            if (length != expected.Length || !buffer[..length].SequenceEqual(expected))
                throw new InvalidOperationException(
                    $"{backend.GetType().Name}: account {counter} mismatch — expected {expected.Length} bytes, read {length}");

            int slotIndex = (int)(SplitMix64((ulong)i ^ SlotHashSalt) % (ulong)spec.SlotsPerAccount);
            WriteSlotHash(counter, slotIndex, slotHash);
            WriteStorageKey(accountKey, slotHash, storageKey);
            WriteSlotValue(counter, slotIndex, slotExpected);
            length = session.GetSlot(storageKey, buffer);
            if (length != SlotValueLength || !buffer[..length].SequenceEqual(slotExpected))
                throw new InvalidOperationException(
                    $"{backend.GetType().Name}: slot ({counter}, {slotIndex}) mismatch — expected {SlotValueLength} bytes, read {length}");
        }

        // Guaranteed misses: account counters ≥ AccountCount and slot indices ≥ SlotsPerAccount are never written.
        for (int i = 0; i < 16; i++)
        {
            WriteAccountKey(spec.AccountCount + i, accountKey);
            if (session.GetAccount(accountKey, buffer) != 0)
                throw new InvalidOperationException($"{backend.GetType().Name}: miss account {spec.AccountCount + i} returned a value");

            WriteAccountKey(i, accountKey);
            WriteSlotHash(i, spec.SlotsPerAccount + 1 + i, slotHash);
            WriteStorageKey(accountKey, slotHash, storageKey);
            if (session.GetSlot(storageKey, buffer) != 0)
                throw new InvalidOperationException($"{backend.GetType().Name}: miss slot ({i}, {spec.SlotsPerAccount + 1 + i}) returned a value");
        }
    }

    // --- Deterministic key/value derivations, shared with the benchmark ---

    /// <summary>Account key: keccak(8-byte BE counter) truncated to 20 bytes — the flat layout's
    /// truncated address hash. Counters ≥ <see cref="FlatBaseDatasetSpec.AccountCount"/> are never
    /// written, so they yield guaranteed-miss keys.</summary>
    public static void WriteAccountKey(long counter, Span<byte> key20)
    {
        Span<byte> input = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(input, counter);
        ValueHash256 hash = ValueKeccak.Compute(input);
        hash.Bytes[..AccountKeyLength].CopyTo(key20);
    }

    /// <summary>Production split storage key: <c>[4B addrHash | 32B slotHash | 16B addrHash]</c>
    /// (see <c>BaseFlatPersistence</c>) — both address parts come from the truncated 20-byte account key.</summary>
    public static void WriteStorageKey(ReadOnlySpan<byte> accountKey20, ReadOnlySpan<byte> slotHash32, Span<byte> key52)
    {
        accountKey20[..StoragePrefixLength].CopyTo(key52);
        slotHash32.CopyTo(key52[StoragePrefixLength..]);
        accountKey20[StoragePrefixLength..].CopyTo(key52[(StoragePrefixLength + 32)..]);
    }

    /// <summary>Slot hash for (account counter, slot index). Slot indices ≥
    /// <see cref="FlatBaseDatasetSpec.SlotsPerAccount"/> are never written, so they yield
    /// guaranteed-miss keys on a live account.</summary>
    public static void WriteSlotHash(long accountCounter, long slotIndex, Span<byte> hash32) =>
        Fill32(SplitMix64((ulong)accountCounter ^ SlotHashSalt) ^ (ulong)slotIndex, hash32);

    public static void WriteSlotValue(long accountCounter, long slotIndex, Span<byte> value32)
    {
        Fill32(SplitMix64((ulong)accountCounter ^ SlotValueSalt) ^ (ulong)slotIndex, value32);
        // A nonzero leading byte keeps the stored value at the full 32 bytes on every backend,
        // sidestepping leading-zero stripping concerns.
        value32[0] |= 0x10;
    }

    /// <summary>Slim-RLP account value (~70-90 bytes): nonce, balance, and non-empty storage root and
    /// code hash (contract-shaped, per the flat Account column's dominant size class).</summary>
    public static byte[] AccountValue(long counter)
    {
        ulong seed = (ulong)counter;
        ulong nonce = SplitMix64(seed ^ NonceSalt) & 0xFFFF;
        UInt256 balance = new(SplitMix64(seed ^ BalanceSalt), SplitMix64(seed ^ (BalanceSalt + 1)) & 0xFF);
        Span<byte> hash = stackalloc byte[32];
        Fill32(seed ^ StorageRootSalt, hash);
        Hash256 storageRoot = new(new ValueHash256(hash));
        Fill32(seed ^ CodeHashSalt, hash);
        Hash256 codeHash = new(new ValueHash256(hash));
        return AccountDecoder.Slim.EncodeAsBytes(new Account(nonce, balance, storageRoot, codeHash));
    }

    public static ulong SplitMix64(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    private static void Fill32(ulong seed, Span<byte> output32)
    {
        for (int i = 0; i < 4; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(output32[(i * sizeof(ulong))..], SplitMix64(seed + (ulong)i));
    }
}
