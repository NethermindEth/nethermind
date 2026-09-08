// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using BenchmarkDotNet.Attributes;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.RocksDbBindings;
using Nethermind.State.Flat;

namespace Nethermind.Benchmarks.Store;

public enum RocksDbAccountAutoMode
{
    BinaryBaseline,
    AutoSst,
    LegacyBinarySstReopenedAuto,
}

public static class RocksDbAccountAutoBenchmarkSelection
{
    private const string ModeVariable = "NETHERMIND_ROCKSDB_ACCOUNT_AUTO_MODE";
    private static RocksDbAccountAutoMode? _mode;

    public static void Configure(RocksDbAccountAutoMode? mode)
    {
        _mode = mode;
        Environment.SetEnvironmentVariable(ModeVariable, mode?.ToString());
    }

    public static IEnumerable<RocksDbAccountAutoMode> GetModeValues() =>
        GetSelectedMode() is { } mode ? [mode] : Enum.GetValues<RocksDbAccountAutoMode>();

    private static RocksDbAccountAutoMode? GetSelectedMode() =>
        _mode ?? ParseEnvironmentValue(ModeVariable);

    private static RocksDbAccountAutoMode? ParseEnvironmentValue(string variable)
    {
        string? value = Environment.GetEnvironmentVariable(variable);
        return Enum.TryParse(value, ignoreCase: true, out RocksDbAccountAutoMode mode) ? mode : null;
    }
}

public sealed class RocksDbAccountAutoDataset
{
    public required byte[][] Keys { get; init; }
    public required byte[][] Values { get; init; }
    public required byte[][] MissingKeys { get; init; }
}

public static class RocksDbAccountAutoDatasetFactory
{
    public const int EntryCount = 65_536;
    private const int MissingKeyCount = 1_024;
    private const int MissingKeyIndexStart = 3 * EntryCount;
    private const int AccountValueLength = 70;

    public static RocksDbAccountAutoDataset Create()
    {
        byte[][] keys = new byte[EntryCount][];
        byte[][] values = new byte[EntryCount][];
        for (int i = 0; i < EntryCount; i++)
        {
            keys[i] = CreateKey(i);
            values[i] = CreateValue(i);
        }

        Array.Sort(keys, values, ByteArrayComparer.Instance);

        byte[][] missingKeys = new byte[MissingKeyCount][];
        for (int i = 0; i < missingKeys.Length; i++)
        {
            missingKeys[i] = CreateKey(MissingKeyIndexStart + i);
        }

        return new RocksDbAccountAutoDataset
        {
            Keys = keys,
            Values = values,
            MissingKeys = missingKeys,
        };
    }

    private static byte[] CreateKey(int index)
    {
        Span<byte> input = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(input, 0xAC_C0_01);
        BinaryPrimitives.WriteInt32LittleEndian(input[4..], index);
        return SHA256.HashData(input)[..20];
    }

    private static byte[] CreateValue(int index)
    {
        byte[] value = new byte[AccountValueLength];
        Span<byte> input = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(input, 0xAC_C0_02);
        BinaryPrimitives.WriteInt32LittleEndian(input[4..], index);
        byte[] digest = SHA256.HashData(input);
        for (int i = 0; i < value.Length; i++)
        {
            value[i] = digest[i % digest.Length];
        }

        return value;
    }

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static ByteArrayComparer Instance { get; } = new();

        public int Compare(byte[]? left, byte[]? right) =>
            left.AsSpan().SequenceCompareTo(right);
    }
}

internal static class RocksDbAccountAutoBenchmarkSupport
{
    private const string AutoOptions =
        "block_based_table_factory.index_block_search_type=kAuto;" +
        "block_based_table_factory.uniform_cv_threshold=0.2;";

    private const string BinaryOptions =
        "block_based_table_factory.index_block_search_type=kBinary;" +
        "block_based_table_factory.uniform_cv_threshold=-1;";

    public static DbConfig CreateConfig(RocksDbAccountAutoMode mode, bool writer)
    {
        DbConfig config = new();
        config.FlatAccountDbAdditionalRocksDbOptions = GetAccountOverride(mode, writer);
        return config;
    }

    public static ColumnsDb<FlatDbColumns> Open(string rootPath, DbConfig config)
    {
        RocksDbConfigFactory configFactory = new(
            config,
            new PruningConfig(),
            new RocksDbAccountAutoHardwareInfo(),
            LimboLogs.Instance,
            validateConfig: false);

        return new ColumnsDb<FlatDbColumns>(
            rootPath,
            new DbSettings(DbNames.Flat, "db"),
            config,
            configFactory,
            LimboLogs.Instance,
            Enum.GetValues<FlatDbColumns>());
    }

    public static (string IndexSearch, string UniformCvThreshold) GetResolvedAccountOptions(DbConfig config)
    {
        RocksDbConfigFactory configFactory = new(
            config,
            new PruningConfig(),
            new RocksDbAccountAutoHardwareInfo(),
            LimboLogs.Instance,
            validateConfig: false);
        IRocksDbConfig resolved = configFactory.GetForDatabase(DbNames.Flat, nameof(FlatDbColumns.Account));
        IDictionary<string, string> options = DbOnTheRocks.ExtractOptions(
            resolved.RocksDbOptions + resolved.AdditionalRocksDbOptions);
        return (options["block_based_table_factory.index_block_search_type"],
            options["block_based_table_factory.uniform_cv_threshold"]);
    }

    public static long GetUniformBlockCount(ColumnsDb<FlatDbColumns> columns)
    {
        FieldInfo dbField = typeof(DbOnTheRocks).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(DbOnTheRocks).FullName, "_db");
        FieldInfo familyField = typeof(ColumnDb).GetField("_columnFamily", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(ColumnDb).FullName, "_columnFamily");
        RocksDb rocksDb = (RocksDb)(dbField.GetValue(columns) ?? throw new InvalidOperationException("RocksDB handle is unavailable."));
        ColumnDb account = (ColumnDb)columns.GetColumnDb(FlatDbColumns.Account);
        IColumnFamilyHandle accountFamily = (IColumnFamilyHandle)(familyField.GetValue(account)
            ?? throw new InvalidOperationException("Account column family handle is unavailable."));
        string? properties = rocksDb.GetProperty("rocksdb.aggregated-table-properties", accountFamily);
        const string prefix = "# uniform blocks=";
        int start = properties?.IndexOf(prefix, StringComparison.Ordinal) ?? -1;
        if (start < 0) throw new InvalidOperationException("RocksDB did not report Account table properties.");

        start += prefix.Length;
        int end = properties!.IndexOf(';', start);
        if (end < 0 || !long.TryParse(properties[start..end], out long uniformBlocks))
        {
            throw new InvalidOperationException("RocksDB returned an invalid Account uniform-block count.");
        }

        return uniformBlocks;
    }

    public static void ValidateUniformBlockCount(RocksDbAccountAutoMode mode, long uniformBlocks)
    {
        bool expectsUniformBlocks = mode == RocksDbAccountAutoMode.AutoSst;
        if ((uniformBlocks > 0) != expectsUniformBlocks)
        {
            throw new InvalidOperationException(
                $"Unexpected Account uniform-block count for {mode}: {uniformBlocks}.");
        }
    }

    private static string GetAccountOverride(RocksDbAccountAutoMode mode, bool writer) =>
        mode switch
        {
            RocksDbAccountAutoMode.BinaryBaseline => BinaryOptions,
            RocksDbAccountAutoMode.AutoSst => AutoOptions,
            RocksDbAccountAutoMode.LegacyBinarySstReopenedAuto => writer ? BinaryOptions : AutoOptions,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };
}

internal sealed class RocksDbAccountAutoHardwareInfo : IHardwareInfo
{
    public long AvailableMemoryBytes => 8L * 1024 * 1024 * 1024;
    public int? MaxOpenFilesLimit => null;
}

[MemoryDiagnoser]
public class RocksDbAccountAutoBenchmarks
{
    private const int ReadStep = 131;

    private ColumnsDb<FlatDbColumns> _db = null!;
    private IDb _account = null!;
    private RocksDbAccountAutoDataset _dataset = null!;
    private string _rootPath = null!;
    private int _readIndex;
    private int _missingIndex;

    [ParamsSource(nameof(GetModeValues))]
    public RocksDbAccountAutoMode Mode { get; set; }

    public static IEnumerable<RocksDbAccountAutoMode> GetModeValues() =>
        RocksDbAccountAutoBenchmarkSelection.GetModeValues();

    [GlobalSetup]
    public void Setup()
    {
        _dataset = RocksDbAccountAutoDatasetFactory.Create();
        _rootPath = Path.Combine(Path.GetTempPath(), "nethermind-rocksdb-account-auto", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootPath);

        DbConfig writerConfig = RocksDbAccountAutoBenchmarkSupport.CreateConfig(Mode, writer: true);
        using (ColumnsDb<FlatDbColumns> writer = RocksDbAccountAutoBenchmarkSupport.Open(_rootPath, writerConfig))
        {
            IDb account = writer.GetColumnDb(FlatDbColumns.Account);
            for (int i = 0; i < _dataset.Keys.Length; i++)
            {
                account.PutSpan(_dataset.Keys[i], _dataset.Values[i], WriteFlags.DisableWAL);
            }

            writer.Flush();
            writer.Compact();
        }

        DbConfig readerConfig = RocksDbAccountAutoBenchmarkSupport.CreateConfig(Mode, writer: false);
        _db = RocksDbAccountAutoBenchmarkSupport.Open(_rootPath, readerConfig);
        _account = _db.GetColumnDb(FlatDbColumns.Account);

        RocksDbAccountAutoBenchmarkSupport.ValidateUniformBlockCount(
            Mode,
            RocksDbAccountAutoBenchmarkSupport.GetUniformBlockCount(_db));

        if (_account.Get(_dataset.Keys[0]) is null || _account.Get(_dataset.MissingKeys[0]) is not null)
        {
            throw new InvalidOperationException($"Account round trip failed for {Mode}.");
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        if (Directory.Exists(_rootPath)) Directory.Delete(_rootPath, recursive: true);
    }

    [Benchmark]
    public int ReadHit()
    {
        byte[]? value = _account.Get(_dataset.Keys[_readIndex]);
        _readIndex = (_readIndex + ReadStep) % _dataset.Keys.Length;
        return value?.Length ?? -1;
    }

    [Benchmark]
    public int ReadMiss()
    {
        byte[]? value = _account.Get(_dataset.MissingKeys[_missingIndex]);
        _missingIndex = (_missingIndex + 1) % _dataset.MissingKeys.Length;
        return value?.Length ?? 0;
    }
}

public static class RocksDbAccountAutoStandaloneRunner
{
    public static void Run(RocksDbAccountAutoMode mode, int operations)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(operations);

        RocksDbAccountAutoDataset dataset = RocksDbAccountAutoDatasetFactory.Create();
        string rootPath = Path.Combine(Path.GetTempPath(), "nethermind-rocksdb-account-auto-standalone", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);

        ColumnsDb<FlatDbColumns>? db = null;
        using Process process = Process.GetCurrentProcess();
        ProcessSnapshot before = Capture(process);
        try
        {
            DbConfig writerConfig = RocksDbAccountAutoBenchmarkSupport.CreateConfig(mode, writer: true);
            Stopwatch writeClock = Stopwatch.StartNew();
            using (ColumnsDb<FlatDbColumns> writer = RocksDbAccountAutoBenchmarkSupport.Open(rootPath, writerConfig))
            {
                IDb account = writer.GetColumnDb(FlatDbColumns.Account);
                for (int i = 0; i < dataset.Keys.Length; i++)
                {
                    account.PutSpan(dataset.Keys[i], dataset.Values[i], WriteFlags.DisableWAL);
                }

                writer.Flush();
                writer.Compact();
            }

            writeClock.Stop();
            ProcessSnapshot afterWrite = Capture(process);
            DbConfig readerConfig = RocksDbAccountAutoBenchmarkSupport.CreateConfig(mode, writer: false);
            Stopwatch openClock = Stopwatch.StartNew();
            db = RocksDbAccountAutoBenchmarkSupport.Open(rootPath, readerConfig);
            openClock.Stop();
            IDb accountReader = db.GetColumnDb(FlatDbColumns.Account);
            long uniformBlocks = RocksDbAccountAutoBenchmarkSupport.GetUniformBlockCount(db);
            RocksDbAccountAutoBenchmarkSupport.ValidateUniformBlockCount(mode, uniformBlocks);
            ProcessSnapshot beforeReads = Capture(process);

            Stopwatch readHitClock = Stopwatch.StartNew();
            int hitChecksum = ReadHits(accountReader, dataset, operations);
            readHitClock.Stop();
            Stopwatch readMissClock = Stopwatch.StartNew();
            int missChecksum = ReadMisses(accountReader, dataset, operations);
            readMissClock.Stop();
            ProcessSnapshot afterRead = Capture(process);

            int expectedHitChecksum = operations * dataset.Values[0].Length;
            if (hitChecksum != expectedHitChecksum || missChecksum != 0)
            {
                throw new InvalidOperationException(
                    $"Read checksums differ: hit={hitChecksum} expected_hit={expectedHitChecksum} miss={missChecksum}.");
            }

            (string writerIndex, string writerThreshold) = RocksDbAccountAutoBenchmarkSupport.GetResolvedAccountOptions(writerConfig);
            (string readerIndex, string readerThreshold) = RocksDbAccountAutoBenchmarkSupport.GetResolvedAccountOptions(readerConfig);
            Console.WriteLine($"mode={mode} operations={operations} checksum={hitChecksum + missChecksum} " +
                              $"hit_checksum={hitChecksum} miss_checksum={missChecksum} " +
                              $"writer_index={writerIndex} writer_threshold={writerThreshold} " +
                              $"reader_index={readerIndex} reader_threshold={readerThreshold} " +
                              $"write_ms={writeClock.ElapsedMilliseconds} open_ms={openClock.ElapsedMilliseconds} " +
                              $"uniform_blocks={uniformBlocks} read_hit_ms={readHitClock.ElapsedMilliseconds} " +
                              $"read_miss_ms={readMissClock.ElapsedMilliseconds} " +
                              $"read_cpu_ms={CpuMilliseconds(afterRead, beforeReads):F2} " +
                              $"total_cpu_ms={CpuMilliseconds(afterRead, before):F2} " +
                              $"private_bytes_before={before.PrivateBytes} private_bytes_after={afterRead.PrivateBytes} " +
                              $"private_bytes_phase_max={Max(before.PrivateBytes, afterWrite.PrivateBytes, afterRead.PrivateBytes)} " +
                              $"working_set_bytes_before={before.WorkingSetBytes} working_set_bytes_after={afterRead.WorkingSetBytes} " +
                              $"working_set_bytes_phase_max={Max(before.WorkingSetBytes, afterWrite.WorkingSetBytes, afterRead.WorkingSetBytes)} " +
                              $"disk_bytes={DirectorySize(rootPath)}");

            db.Dispose();
            db = null;
        }
        finally
        {
            db?.Dispose();
            if (Directory.Exists(rootPath)) Directory.Delete(rootPath, recursive: true);
        }
    }

    private static int ReadHits(IDb account, RocksDbAccountAutoDataset dataset, int operations)
    {
        int checksum = 0;
        for (int i = 0; i < operations; i++)
        {
            checksum += account.Get(dataset.Keys[i * 131 % dataset.Keys.Length])?.Length ?? -1;
        }

        return checksum;
    }

    private static int ReadMisses(IDb account, RocksDbAccountAutoDataset dataset, int operations)
    {
        int checksum = 0;
        for (int i = 0; i < operations; i++)
        {
            checksum += account.Get(dataset.MissingKeys[i % dataset.MissingKeys.Length])?.Length ?? 0;
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
