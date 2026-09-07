// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;

namespace Nethermind.Benchmarks.Store;

public enum RocksDbFeatureDatasetKind
{
    Account,
    Storage,
    Trie,
    Bytecode,
    Blocks,
}

public enum RocksDbFeatureVariant
{
    Baseline,
    FlatAccountInterpolation,
    AutoIndexUniform,
    SeparatedKeyValue,
    RibbonFilter,
    DataBlockBinaryHash,
    Lz4,
    Format7,
    MemtableBatchLookup,
}

public static class RocksDbFeatureBenchmarkSelection
{
    private const string DatasetVariable = "NETHERMIND_ROCKSDB_FEATURE_DATASET";
    private const string VariantVariable = "NETHERMIND_ROCKSDB_FEATURE_VARIANT";
    private static RocksDbFeatureDatasetKind? _dataset;
    private static RocksDbFeatureVariant? _variant;

    public static void Configure(RocksDbFeatureDatasetKind? dataset, RocksDbFeatureVariant? variant)
    {
        _dataset = dataset;
        _variant = variant;
        Environment.SetEnvironmentVariable(DatasetVariable, dataset?.ToString());
        Environment.SetEnvironmentVariable(VariantVariable, variant?.ToString());
    }

    public static IEnumerable<RocksDbFeatureDatasetKind> GetDatasetValues() =>
        GetSelectedDataset() is { } dataset ? [dataset] : Enum.GetValues<RocksDbFeatureDatasetKind>();

    public static IEnumerable<RocksDbFeatureVariant> GetVariantValues() =>
        GetSelectedVariant() is { } variant ? [variant] : Enum.GetValues<RocksDbFeatureVariant>();

    private static RocksDbFeatureDatasetKind? GetSelectedDataset() =>
        _dataset ?? ParseEnvironmentValue<RocksDbFeatureDatasetKind>(DatasetVariable);

    private static RocksDbFeatureVariant? GetSelectedVariant() =>
        _variant ?? ParseEnvironmentValue<RocksDbFeatureVariant>(VariantVariable);

    private static TEnum? ParseEnvironmentValue<TEnum>(string variable) where TEnum : struct, Enum
    {
        string? value = Environment.GetEnvironmentVariable(variable);
        return Enum.TryParse(value, ignoreCase: true, out TEnum result) ? result : null;
    }
}

public sealed class RocksDbFeatureDataset
{
    public required string DatabaseName { get; init; }
    public required byte[][] Keys { get; init; }
    public required byte[][] Values { get; init; }
    public required byte[][] MissingKeys { get; init; }
    public required byte[][] MemtableKeys { get; init; }

    public byte[] MissingKey => MissingKeys[0];
}

public static class RocksDbFeatureDatasetFactory
{
    public const int EntryCount = 65_536;
    private const int MissingKeyCount = 1_024;
    private const int MemtableKeyCount = 2_048;
    private const int StorageSlotsPerAddress = 64;

    public static RocksDbFeatureDataset Create(RocksDbFeatureDatasetKind kind)
    {
        (string databaseName, int keyLength, int valueLength) = kind switch
        {
            RocksDbFeatureDatasetKind.Account => ("FlatAccount", 20, 70),
            RocksDbFeatureDatasetKind.Storage => ("FlatStorage", 52, 32),
            RocksDbFeatureDatasetKind.Trie => ("FlatStateNodes", 32, 400),
            RocksDbFeatureDatasetKind.Bytecode => (DbNames.Code, 32, 4_096),
            RocksDbFeatureDatasetKind.Blocks => (DbNames.Blocks, 32, 1_024),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

        (byte[] Key, byte[] Value)[] entries = new (byte[] Key, byte[] Value)[EntryCount];
        for (int i = 0; i < EntryCount; i++)
        {
            entries[i] = (CreateKey(kind, i, keyLength), CreateValue(kind, valueLength, i));
        }

        Array.Sort(entries, static (left, right) => left.Key.AsSpan().SequenceCompareTo(right.Key));

        byte[][] keys = new byte[EntryCount][];
        byte[][] values = new byte[EntryCount][];
        for (int i = 0; i < entries.Length; i++)
        {
            keys[i] = entries[i].Key;
            values[i] = entries[i].Value;
        }

        byte[][] missingKeys = new byte[MissingKeyCount][];
        for (int i = 0; i < missingKeys.Length; i++)
        {
            missingKeys[i] = CreateMissingKey(keyLength, i);
        }

        byte[][] memtableKeys = new byte[MemtableKeyCount][];
        for (int i = 0; i < memtableKeys.Length; i++)
        {
            memtableKeys[i] = CreateKey(kind, EntryCount + MissingKeyCount + i, keyLength);
        }

        return new RocksDbFeatureDataset
        {
            DatabaseName = databaseName,
            Keys = keys,
            Values = values,
            MissingKeys = missingKeys,
            MemtableKeys = memtableKeys,
        };
    }

    private static byte[] CreateKey(RocksDbFeatureDatasetKind kind, int index, int length)
    {
        byte[] hash = Digest(kind, index / (kind == RocksDbFeatureDatasetKind.Storage ? StorageSlotsPerAddress : 1));
        if (kind != RocksDbFeatureDatasetKind.Storage)
        {
            return hash[..length];
        }

        // Flat storage uses [4-byte address hash | 32-byte slot hash | 16-byte address hash].
        byte[] slotHash = Digest(kind, EntryCount + index);
        byte[] key = new byte[length];
        hash.AsSpan(0, 4).CopyTo(key);
        slotHash.CopyTo(key, 4);
        hash.AsSpan(4, 16).CopyTo(key.AsSpan(36));
        return key;
    }

    private static byte[] CreateValue(RocksDbFeatureDatasetKind kind, int length, int index)
    {
        if (kind == RocksDbFeatureDatasetKind.Bytecode)
        {
            byte[] bytecode = new byte[length];
            for (int offset = 0; offset < bytecode.Length; offset += 64)
            {
                int runLength = Math.Min(48, bytecode.Length - offset);
                bytecode.AsSpan(offset, runLength).Fill((byte)(0x60 + ((index + offset / 64) & 7)));

                int digestLength = Math.Min(16, bytecode.Length - offset - runLength);
                if (digestLength > 0)
                {
                    Digest(kind, index + offset).AsSpan(0, digestLength)
                        .CopyTo(bytecode.AsSpan(offset + runLength, digestLength));
                }
            }

            return bytecode;
        }

        byte[] value = new byte[length];
        int valueOffset = 0;
        while (valueOffset < value.Length)
        {
            byte[] block = Digest(kind, index + valueOffset);
            int copyLength = Math.Min(block.Length, value.Length - valueOffset);
            block.AsSpan(0, copyLength).CopyTo(value.AsSpan(valueOffset));
            valueOffset += copyLength;
        }

        if (kind == RocksDbFeatureDatasetKind.Blocks)
        {
            for (int offset = 0; offset < value.Length; offset += 128)
            {
                if (((index + offset / 128) & 3) != 0)
                {
                    value.AsSpan(offset, Math.Min(96, value.Length - offset)).Fill((byte)(0x20 + ((index + offset / 128) & 15)));
                }
            }
        }

        return value;
    }

    private static byte[] CreateMissingKey(int length, int index)
    {
        byte[] missingKey = new byte[length];
        missingKey.AsSpan().Fill(0xFF);
        BinaryPrimitives.WriteInt32BigEndian(missingKey.AsSpan(length - sizeof(int)), index);
        return missingKey;
    }

    private static byte[] Digest(RocksDbFeatureDatasetKind kind, int index)
    {
        Span<byte> input = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(input, (int)kind);
        BinaryPrimitives.WriteInt32LittleEndian(input[4..], index);
        return SHA256.HashData(input);
    }
}

internal static class RocksDbFeatureBenchmarkSupport
{
    private const int MultiGetBatchCount = 32;
    private const int MultiGetBatchSize = 64;
    private const int ReadStep = 131;

    public static DbConfig CreateConfig(RocksDbFeatureDatasetKind dataset, RocksDbFeatureVariant variant)
    {
        DbConfig config = new();
        config.RocksDbOptions += GlobalOptions(variant);

        // The production flat layout composes FlatDb common options with each column's
        // specific options. Keep the same ordering so account's no-compression and
        // 4-KiB block settings, storage's LZ4 and 8-KiB blocks, and trie settings win.
        if (dataset is RocksDbFeatureDatasetKind.Account or RocksDbFeatureDatasetKind.Storage or RocksDbFeatureDatasetKind.Trie)
        {
            config.RocksDbOptions += config.FlatDbRocksDbOptions;
        }

        string options = DatasetAdditionalOptions(dataset, variant);

        switch (dataset)
        {
            case RocksDbFeatureDatasetKind.Account:
                config.FlatAccountDbAdditionalRocksDbOptions = options;
                break;
            case RocksDbFeatureDatasetKind.Storage:
                config.FlatStorageDbAdditionalRocksDbOptions = options;
                break;
            case RocksDbFeatureDatasetKind.Trie:
                config.FlatStateNodesDbAdditionalRocksDbOptions = options;
                break;
            case RocksDbFeatureDatasetKind.Bytecode:
                config.CodeDbAdditionalRocksDbOptions = options;
                break;
            case RocksDbFeatureDatasetKind.Blocks:
                config.BlocksDbAdditionalRocksDbOptions = options;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(dataset), dataset, null);
        }

        return config;
    }

    public static DbOnTheRocks Open(string rootPath, RocksDbFeatureDataset dataset, DbConfig config)
    {
        RocksDbConfigFactory configFactory = new(
            config,
            new PruningConfig(),
            new RocksDbFeatureHardwareInfo(),
            LimboLogs.Instance,
            validateConfig: false);
        return new DbOnTheRocks(
            rootPath,
            new DbSettings(dataset.DatabaseName, "db"),
            config,
            configFactory,
            LimboLogs.Instance);
    }

    public static byte[][][] CreateMultiGetBatches(RocksDbFeatureDataset dataset)
        => CreateMultiGetBatches(dataset.Keys, dataset.MissingKeys);

    public static byte[][][] CreateMemtableBatches(RocksDbFeatureDataset dataset)
        => CreateMultiGetBatches(dataset.MemtableKeys, dataset.MissingKeys);

    private static byte[][][] CreateMultiGetBatches(byte[][] hitKeys, byte[][] missKeys)
    {
        byte[][][] batches = new byte[MultiGetBatchCount][][];
        for (int batchIndex = 0; batchIndex < batches.Length; batchIndex++)
        {
            byte[][] keys = new byte[MultiGetBatchSize][];
            for (int i = 0; i < keys.Length; i++)
            {
                int keyIndex = (batchIndex * MultiGetBatchSize + i) * ReadStep % hitKeys.Length;
                keys[i] = (i & 1) == 0 ? hitKeys[keyIndex] : missKeys[keyIndex % missKeys.Length];
            }

            batches[batchIndex] = keys;
        }

        return batches;
    }

    private static string GlobalOptions(RocksDbFeatureVariant variant) => variant switch
    {
        RocksDbFeatureVariant.AutoIndexUniform =>
            "block_based_table_factory.index_block_search_type=kAuto;block_based_table_factory.uniform_cv_threshold=0.2;",
        RocksDbFeatureVariant.SeparatedKeyValue =>
            "block_based_table_factory.separate_key_value_in_data_block=true;",
        RocksDbFeatureVariant.RibbonFilter =>
            "block_based_table_factory.filter_policy=ribbonfilter:10:1;",
        RocksDbFeatureVariant.DataBlockBinaryHash =>
            "block_based_table_factory.data_block_index_type=kDataBlockBinaryAndHash;",
        RocksDbFeatureVariant.Lz4 =>
            "compression=kLZ4Compression;",
        RocksDbFeatureVariant.Format7 =>
            "block_based_table_factory.format_version=7;",
        RocksDbFeatureVariant.MemtableBatchLookup =>
            "memtable_batch_lookup_optimization=true;",
        _ => string.Empty,
    };

    private static string DatasetAdditionalOptions(RocksDbFeatureDatasetKind dataset, RocksDbFeatureVariant variant) =>
        variant == RocksDbFeatureVariant.FlatAccountInterpolation && dataset == RocksDbFeatureDatasetKind.Account
            ? "block_based_table_factory.index_block_search_type=kInterpolation;"
            : string.Empty;
}

internal sealed class RocksDbFeatureHardwareInfo : IHardwareInfo
{
    public long AvailableMemoryBytes => 8L * 1024 * 1024 * 1024;
    public int? MaxOpenFilesLimit => null;
}

[MemoryDiagnoser]
public class RocksDbFeatureBenchmarks
{
    private const int RewriteCount = 128;
    private const int ReadStep = 131;

    private DbOnTheRocks _db = null!;
    private IReadOnlyKeyValueStore _store = null!;
    private RocksDbFeatureDataset _dataset = null!;
    private byte[][][] _multiGetBatches = null!;
    private byte[][][] _memtableBatches = null!;
    private byte[] _rewriteValue = null!;
    private string _rootPath = null!;
    private int _readIndex;
    private int _missingIndex;
    private int _multiGetIndex;
    private int _rewriteOffset;

    [ParamsSource(nameof(GetDatasetValues))]
    public RocksDbFeatureDatasetKind Dataset { get; set; }

    [ParamsSource(nameof(GetVariantValues))]
    public RocksDbFeatureVariant Variant { get; set; }

    public static IEnumerable<RocksDbFeatureDatasetKind> GetDatasetValues() =>
        RocksDbFeatureBenchmarkSelection.GetDatasetValues();

    public static IEnumerable<RocksDbFeatureVariant> GetVariantValues() =>
        RocksDbFeatureBenchmarkSelection.GetVariantValues();

    [GlobalSetup]
    public void Setup()
    {
        _dataset = RocksDbFeatureDatasetFactory.Create(Dataset);
        _multiGetBatches = RocksDbFeatureBenchmarkSupport.CreateMultiGetBatches(_dataset);
        _memtableBatches = RocksDbFeatureBenchmarkSupport.CreateMemtableBatches(_dataset);
        _rewriteValue = (byte[])_dataset.Values[0].Clone();
        _rootPath = Path.Combine(Path.GetTempPath(), "nethermind-rocksdb-feature", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootPath);

        DbConfig config = RocksDbFeatureBenchmarkSupport.CreateConfig(Dataset, Variant);
        _db = RocksDbFeatureBenchmarkSupport.Open(_rootPath, _dataset, config);
        _store = _db;
        for (int i = 0; i < _dataset.Keys.Length; i++)
        {
            _db.Set(_dataset.Keys[i], _dataset.Values[i], WriteFlags.DisableWAL);
        }

        _db.Flush();
        _db.Compact();
        for (int i = 0; i < _dataset.MemtableKeys.Length; i++)
        {
            _db.Set(_dataset.MemtableKeys[i], _dataset.Values[i % _dataset.Values.Length], WriteFlags.DisableWAL);
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        if (Directory.Exists(_rootPath)) Directory.Delete(_rootPath, true);
    }

    [Benchmark]
    public int ReadHit()
    {
        byte[]? value = _store.Get(_dataset.Keys[_readIndex]);
        _readIndex = (_readIndex + ReadStep) % _dataset.Keys.Length;
        return value?.Length ?? 0;
    }

    [Benchmark]
    public int ReadMiss()
    {
        byte[]? value = _store.Get(_dataset.MissingKeys[_missingIndex]);
        _missingIndex = (_missingIndex + 1) % _dataset.MissingKeys.Length;
        return value?.Length ?? -1;
    }

    [Benchmark]
    public int MultiGetMixed()
    {
        KeyValuePair<byte[], byte[]?>[] results = _db[_multiGetBatches[_multiGetIndex]];
        _multiGetIndex = (_multiGetIndex + 1) % _multiGetBatches.Length;

        int checksum = 0;
        for (int i = 0; i < results.Length; i++) checksum += results[i].Value?.Length ?? -1;
        return checksum;
    }

    [Benchmark]
    public int MultiGetMemtableResident()
    {
        KeyValuePair<byte[], byte[]?>[] results = _db[_memtableBatches[_multiGetIndex]];
        _multiGetIndex = (_multiGetIndex + 1) % _memtableBatches.Length;

        int checksum = 0;
        for (int i = 0; i < results.Length; i++) checksum += results[i].Value?.Length ?? -1;
        return checksum;
    }

    [Benchmark]
    public int BoundedScan()
    {
        int count = 0;
        int lower = _dataset.Keys.Length / 4;
        int upper = _dataset.Keys.Length * 3 / 4;
        using ISortedView view = ((ISortedKeyValueStore)_db).GetViewBetween(
            _dataset.Keys[lower], _dataset.Keys[upper], ReadFlags.HintReadAhead);
        while (view.MoveNext()) count += view.CurrentValue.Length;
        return count;
    }

    [Benchmark]
    public int RewriteAndFlush()
    {
        int start = _rewriteOffset;
        for (int i = 0; i < RewriteCount; i++)
        {
            int index = (start + i) % _dataset.Keys.Length;
            _store.Set(_dataset.Keys[index], _rewriteValue, WriteFlags.DisableWAL);
        }

        _rewriteOffset = (start + RewriteCount) % _dataset.Keys.Length;
        _db.Flush();
        return RewriteCount;
    }

}

public static class RocksDbFeatureStandaloneRunner
{
    public static void Run(RocksDbFeatureDatasetKind datasetKind, RocksDbFeatureVariant variant, int operations)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(operations);

        RocksDbFeatureDataset dataset = RocksDbFeatureDatasetFactory.Create(datasetKind);
        string rootPath = Path.Combine(Path.GetTempPath(), "nethermind-rocksdb-feature-standalone", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);

        DbOnTheRocks? db = null;
        using Process process = Process.GetCurrentProcess();
        try
        {
            DbConfig config = RocksDbFeatureBenchmarkSupport.CreateConfig(datasetKind, variant);
            db = RocksDbFeatureBenchmarkSupport.Open(rootPath, dataset, config);
            IReadOnlyKeyValueStore store = db;
            ProcessSnapshot beforeIngest = Capture(process);

            Stopwatch ingestClock = Stopwatch.StartNew();
            for (int i = 0; i < dataset.Keys.Length; i++) db.Set(dataset.Keys[i], dataset.Values[i], WriteFlags.DisableWAL);
            db.Flush();
            ingestClock.Stop();
            ProcessSnapshot afterFlush = Capture(process);

            Stopwatch compactClock = Stopwatch.StartNew();
            db.Compact();
            compactClock.Stop();
            ProcessSnapshot afterCompaction = Capture(process);

            Stopwatch memtableClock = Stopwatch.StartNew();
            for (int i = 0; i < dataset.MemtableKeys.Length; i++)
            {
                db.Set(dataset.MemtableKeys[i], dataset.Values[i % dataset.Values.Length], WriteFlags.DisableWAL);
            }
            memtableClock.Stop();
            ProcessSnapshot afterMemtable = Capture(process);
            long diskBytes = DirectorySize(rootPath);

            ProcessSnapshot beforeReads = Capture(process);
            Stopwatch readClock = Stopwatch.StartNew();
            int checksum = RunReads(store, db, dataset, operations);
            readClock.Stop();
            ProcessSnapshot afterReads = Capture(process);

            TimeSpan disposeCpuBefore = process.TotalProcessorTime;
            Stopwatch disposeClock = Stopwatch.StartNew();
            db.Dispose();
            db = null;
            disposeClock.Stop();
            ProcessSnapshot afterDispose = Capture(process);

            long privateBytesPhaseMax = Max(
                beforeIngest.PrivateBytes,
                afterFlush.PrivateBytes,
                afterCompaction.PrivateBytes,
                afterMemtable.PrivateBytes,
                beforeReads.PrivateBytes,
                afterReads.PrivateBytes,
                afterDispose.PrivateBytes);
            long workingSetBytesPhaseMax = Max(
                beforeIngest.WorkingSetBytes,
                afterFlush.WorkingSetBytes,
                afterCompaction.WorkingSetBytes,
                afterMemtable.WorkingSetBytes,
                beforeReads.WorkingSetBytes,
                afterReads.WorkingSetBytes,
                afterDispose.WorkingSetBytes);

            Console.WriteLine($"dataset={datasetKind} variant={variant} operations={operations} checksum={checksum} " +
                              $"ingest_ms={ingestClock.ElapsedMilliseconds} ingest_cpu_ms={CpuMilliseconds(afterFlush, beforeIngest):F2} " +
                              $"compact_ms={compactClock.ElapsedMilliseconds} compact_cpu_ms={CpuMilliseconds(afterCompaction, afterFlush):F2} " +
                              $"memtable_ms={memtableClock.ElapsedMilliseconds} memtable_cpu_ms={CpuMilliseconds(afterMemtable, afterCompaction):F2} " +
                              $"read_ms={readClock.ElapsedMilliseconds} read_cpu_ms={CpuMilliseconds(afterReads, beforeReads):F2} " +
                              $"dispose_ms={disposeClock.ElapsedMilliseconds} dispose_cpu_ms={(process.TotalProcessorTime - disposeCpuBefore).TotalMilliseconds:F2} " +
                              $"total_cpu_ms={CpuMilliseconds(afterDispose, beforeIngest):F2} " +
                              $"private_bytes_before={beforeIngest.PrivateBytes} private_bytes_after={afterReads.PrivateBytes} private_bytes_phase_max={privateBytesPhaseMax} " +
                              $"working_set_bytes_before={beforeIngest.WorkingSetBytes} working_set_bytes_after={afterReads.WorkingSetBytes} working_set_bytes_phase_max={workingSetBytesPhaseMax} " +
                              $"disk_bytes={diskBytes}");
        }
        finally
        {
            db?.Dispose();
            if (Directory.Exists(rootPath)) Directory.Delete(rootPath, true);
        }
    }

    private static int RunReads(IReadOnlyKeyValueStore store, DbOnTheRocks db, RocksDbFeatureDataset dataset, int operations)
    {
        byte[][][] batches = RocksDbFeatureBenchmarkSupport.CreateMultiGetBatches(dataset);
        byte[][][] memtableBatches = RocksDbFeatureBenchmarkSupport.CreateMemtableBatches(dataset);
        int checksum = 0;
        for (int i = 0; i < operations; i++)
        {
            checksum += store.Get(dataset.Keys[i * 131 % dataset.Keys.Length])?.Length ?? 0;
            checksum += store.Get(dataset.MissingKeys[i % dataset.MissingKeys.Length])?.Length ?? -1;

            // Point, batch, and scan operations are combined here for one process-level sample;
            // the BDN methods measure each workload separately.
            if ((i & 63) == 0)
            {
                using ISortedView view = ((ISortedKeyValueStore)db).GetViewBetween(
                    dataset.Keys[dataset.Keys.Length / 4], dataset.Keys[dataset.Keys.Length * 3 / 4], ReadFlags.HintReadAhead);
                while (view.MoveNext()) checksum += view.CurrentValue.Length;
            }

            if ((i & 31) == 0)
            {
                KeyValuePair<byte[], byte[]?>[] results = db[batches[(i / 32) % batches.Length]];
                for (int j = 0; j < results.Length; j++) checksum += results[j].Value?.Length ?? -1;
            }

            if ((i & 31) == 0)
            {
                KeyValuePair<byte[], byte[]?>[] results = db[memtableBatches[(i / 32) % memtableBatches.Length]];
                for (int j = 0; j < results.Length; j++) checksum += results[j].Value?.Length ?? -1;
            }
        }

        return checksum;
    }

    private static ProcessSnapshot Capture(Process process)
    {
        process.Refresh();
        return new ProcessSnapshot(process.TotalProcessorTime, process.PrivateMemorySize64, process.WorkingSet64);
    }

    private static double CpuMilliseconds(ProcessSnapshot after, ProcessSnapshot before) =>
        (after.CpuTime - before.CpuTime).TotalMilliseconds;

    private static long Max(params long[] values)
    {
        long maximum = 0;
        for (int i = 0; i < values.Length; i++) maximum = Math.Max(maximum, values[i]);
        return maximum;
    }

    private static long DirectorySize(string path)
    {
        long total = 0;
        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            total += new FileInfo(file).Length;
        }

        return total;
    }

    private readonly record struct ProcessSnapshot(TimeSpan CpuTime, long PrivateBytes, long WorkingSetBytes);
}
