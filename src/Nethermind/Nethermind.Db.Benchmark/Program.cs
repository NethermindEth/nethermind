// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using RocksDbSharp;
using RocksDb = RocksDbSharp.RocksDb;

namespace Nethermind.Db.Benchmark;

internal static class Program
{
    private const int KeySize = 32;

    public static int Main(string[] args)
    {
        try
        {
            BenchmarkOptions options = BenchmarkOptions.Parse(args);
            ValidatePlatform(options);

            Directory.CreateDirectory(options.Path);
            List<ProviderResult> results = [];
            foreach (string provider in options.Providers)
            {
                results.Add(RunProvider(provider, options));
            }

            Console.WriteLine(JsonSerializer.Serialize(new BenchmarkResult(options, results), JsonOptions));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private static void ValidatePlatform(BenchmarkOptions options)
    {
        if (options.Providers.Contains("mdbx", StringComparer.OrdinalIgnoreCase) &&
            (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.ProcessArchitecture != Architecture.X64))
        {
            throw new PlatformNotSupportedException("The MDBX benchmark provider is supported only on linux-x64.");
        }
    }

    private static ProviderResult RunProvider(string providerName, BenchmarkOptions options)
    {
        string providerPath = Path.Combine(options.Path, providerName);
        if (options.Clean && Directory.Exists(providerPath))
        {
            Directory.Delete(providerPath, recursive: true);
        }

        Directory.CreateDirectory(providerPath);
        long recordCount = Math.Max(1, options.TargetBytes / (KeySize + options.ValueSize));
        Console.WriteLine($"provider={providerName} records={recordCount:N0} logicalBytes={recordCount * (KeySize + options.ValueSize):N0}");

        StoreFactory factory = providerName.ToLowerInvariant() switch
        {
            "mdbx" => () => new MdbxStore(providerPath),
            "rocksdb" => () => new RocksDbStore(providerPath, options.DisableWal),
            _ => throw new ArgumentException($"Unknown provider '{providerName}'. Use mdbx, rocksdb, or both.", nameof(options)),
        };

        TimeSpan writeElapsed;
        TimeSpan flushElapsed;
        long directorySizeAfterWrite;
        using (IBenchmarkStore store = factory())
        {
            writeElapsed = WriteRecords(store, recordCount, options);
            Stopwatch flushWatch = Stopwatch.StartNew();
            store.Flush();
            flushWatch.Stop();
            flushElapsed = flushWatch.Elapsed;
            directorySizeAfterWrite = GetDirectorySize(providerPath);
        }

        ReadResult readResult;
        using (IBenchmarkStore store = factory())
        {
            readResult = ReadRandom(store, recordCount, options);
        }

        long directorySizeAfterRead = GetDirectorySize(providerPath);
        return new ProviderResult(
            providerName,
            recordCount,
            recordCount * (KeySize + options.ValueSize),
            directorySizeAfterWrite,
            directorySizeAfterRead,
            writeElapsed.TotalSeconds,
            flushElapsed.TotalSeconds,
            readResult.Elapsed.TotalSeconds,
            recordCount / writeElapsed.TotalSeconds,
            options.Reads / readResult.Elapsed.TotalSeconds,
            readResult.Found,
            readResult.Checksum);
    }

    private static TimeSpan WriteRecords(IBenchmarkStore store, long recordCount, BenchmarkOptions options)
    {
        byte[] key = new byte[KeySize];
        byte[] value = new byte[options.ValueSize];
        Stopwatch stopwatch = Stopwatch.StartNew();
        long nextReport = options.ReportEvery;

        for (long index = 0; index < recordCount;)
        {
            int count = (int)Math.Min(options.BatchSize, recordCount - index);
            store.WriteBatch(index, count, key, value, options.ValueSize);
            index += count;

            if (options.ReportEvery > 0 && index >= nextReport)
            {
                Console.WriteLine($"write records={index:N0}/{recordCount:N0} elapsed={stopwatch.Elapsed}");
                nextReport += options.ReportEvery;
            }
        }

        stopwatch.Stop();
        return stopwatch.Elapsed;
    }

    private static ReadResult ReadRandom(IBenchmarkStore store, long recordCount, BenchmarkOptions options)
    {
        byte[] key = new byte[KeySize];
        ulong random = options.ReadSeed;
        long found = 0;
        ulong checksum = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();

        for (long i = 0; i < options.Reads; i++)
        {
            random = SplitMix64(random);
            long index = (long)(random % (ulong)recordCount);
            FillKey(index, key);
            byte[]? value = store.Get(key);
            if (value is not null)
            {
                found++;
                checksum += value[0];
                checksum += (ulong)value[^1] << 8;
            }

            if (options.ReportEvery > 0 && i > 0 && i % options.ReportEvery == 0)
            {
                Console.WriteLine($"read records={i:N0}/{options.Reads:N0} elapsed={stopwatch.Elapsed}");
            }
        }

        stopwatch.Stop();
        return new ReadResult(stopwatch.Elapsed, found, checksum);
    }

    private static long GetDirectorySize(string path)
    {
        long size = 0;
        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            size += new FileInfo(file).Length;
        }

        return size;
    }

    private static void FillKey(long index, Span<byte> key)
    {
        ulong state = unchecked((ulong)index) + 0x9e3779b97f4a7c15UL;
        for (int offset = 0; offset < key.Length; offset += sizeof(ulong))
        {
            state = SplitMix64(state);
            WriteUInt64BigEndian(key[offset..], state);
        }
    }

    private static void FillValue(long index, Span<byte> value)
    {
        ulong state = unchecked((ulong)index) ^ 0xd6e8feb86659fd93UL;
        for (int offset = 0; offset < value.Length; offset += sizeof(ulong))
        {
            state = SplitMix64(state);
            WriteUInt64BigEndian(value[offset..], state);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong SplitMix64(ulong value)
    {
        value += 0x9e3779b97f4a7c15UL;
        value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL;
        value = (value ^ (value >> 27)) * 0x94d049bb133111ebUL;
        return value ^ (value >> 31);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteUInt64BigEndian(Span<byte> destination, ulong value)
    {
        destination[0] = (byte)(value >> 56);
        destination[1] = (byte)(value >> 48);
        destination[2] = (byte)(value >> 40);
        destination[3] = (byte)(value >> 32);
        destination[4] = (byte)(value >> 24);
        destination[5] = (byte)(value >> 16);
        destination[6] = (byte)(value >> 8);
        destination[7] = (byte)value;
    }

    private delegate IBenchmarkStore StoreFactory();

    private interface IBenchmarkStore : IDisposable
    {
        byte[]? Get(ReadOnlySpan<byte> key);
        void WriteBatch(long startIndex, int count, byte[] keyBuffer, byte[] valueBuffer, int valueSize);
        void Flush();
    }

    private sealed class MdbxStore : IBenchmarkStore
    {
        private readonly DbOnTheRocks _db;

        public MdbxStore(string path)
        {
            string fullPath = Path.GetFullPath(path);
            DbConfig dbConfig = new();
            RocksDbConfigFactory configFactory = new(dbConfig, new PruningConfig(), new HardwareInfo(), LimboLogs.Instance, validateConfig: false);
            DbSettings settings = new("State0", fullPath);
            _db = new DbOnTheRocks(fullPath, settings, dbConfig, configFactory, LimboLogs.Instance);
        }

        public byte[]? Get(ReadOnlySpan<byte> key) => _db.Get(key);

        public void WriteBatch(long startIndex, int count, byte[] keyBuffer, byte[] valueBuffer, int valueSize)
        {
            using Nethermind.Core.IWriteBatch batch = _db.StartWriteBatch();
            for (int i = 0; i < count; i++)
            {
                long index = startIndex + i;
                FillKey(index, keyBuffer);
                FillValue(index, valueBuffer.AsSpan(0, valueSize));
                batch.PutSpan(keyBuffer, valueBuffer.AsSpan(0, valueSize));
            }
        }

        public void Flush() => _db.Flush();

        public void Dispose() => _db.Dispose();
    }

    private sealed class RocksDbStore : IBenchmarkStore
    {
        private readonly RocksDb _db;
        private readonly WriteOptions _writeOptions;

        public RocksDbStore(string path, bool disableWal)
        {
            DbOptions options = new DbOptions()
                .SetCreateIfMissing()
                .SetCompression(Compression.Lz4);
            _db = RocksDb.Open(options, path);
            _writeOptions = new WriteOptions();
            if (disableWal)
            {
                _writeOptions.DisableWal(1);
            }
        }

        public byte[]? Get(ReadOnlySpan<byte> key) => _db.Get(key);

        public void WriteBatch(long startIndex, int count, byte[] keyBuffer, byte[] valueBuffer, int valueSize)
        {
            using WriteBatch batch = new();
            for (int i = 0; i < count; i++)
            {
                long index = startIndex + i;
                FillKey(index, keyBuffer);
                FillValue(index, valueBuffer.AsSpan(0, valueSize));
                batch.Put(keyBuffer, valueBuffer.AsSpan(0, valueSize));
            }

            _db.Write(batch, _writeOptions);
        }

        public void Flush() => _db.Flush(new FlushOptions());

        public void Dispose() => _db.Dispose();
    }

    private sealed record BenchmarkOptions(
        string Path,
        string[] Providers,
        long TargetBytes,
        int ValueSize,
        long Reads,
        int BatchSize,
        bool Clean,
        bool DisableWal,
        long ReportEvery,
        ulong ReadSeed)
    {
        public static BenchmarkOptions Parse(string[] args)
        {
            Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (!arg.StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Unexpected argument '{arg}'.");
                }

                string key = arg[2..];
                if (string.Equals(key, "clean", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "disable-wal", StringComparison.OrdinalIgnoreCase))
                {
                    values[key] = "true";
                    continue;
                }

                if (i + 1 == args.Length)
                {
                    throw new ArgumentException($"Missing value for '{arg}'.");
                }

                values[key] = args[++i];
            }

            string path = Get(values, "path", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nethermind-db-benchmark"));
            string[] providers = Get(values, "providers", "mdbx,rocksdb").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return new BenchmarkOptions(
                path,
                providers,
                ParseSize(Get(values, "target-size", "30GiB")),
                int.Parse(Get(values, "value-size", "4096"), CultureInfo.InvariantCulture),
                long.Parse(Get(values, "reads", "2000000"), CultureInfo.InvariantCulture),
                int.Parse(Get(values, "batch-size", "8192"), CultureInfo.InvariantCulture),
                bool.Parse(Get(values, "clean", "false")),
                bool.Parse(Get(values, "disable-wal", "true")),
                long.Parse(Get(values, "report-every", "250000"), CultureInfo.InvariantCulture),
                ulong.Parse(Get(values, "read-seed", "16045690984833335023"), CultureInfo.InvariantCulture));
        }

        private static string Get(IReadOnlyDictionary<string, string> values, string key, string fallback) =>
            values.TryGetValue(key, out string? value) ? value : fallback;

        private static long ParseSize(string value)
        {
            string trimmed = value.Trim();
            long multiplier = 1;
            string number = trimmed;
            if (trimmed.EndsWith("GiB", StringComparison.OrdinalIgnoreCase))
            {
                multiplier = 1L << 30;
                number = trimmed[..^3];
            }
            else if (trimmed.EndsWith("GB", StringComparison.OrdinalIgnoreCase))
            {
                multiplier = 1_000_000_000L;
                number = trimmed[..^2];
            }
            else if (trimmed.EndsWith("MiB", StringComparison.OrdinalIgnoreCase))
            {
                multiplier = 1L << 20;
                number = trimmed[..^3];
            }
            else if (trimmed.EndsWith("MB", StringComparison.OrdinalIgnoreCase))
            {
                multiplier = 1_000_000L;
                number = trimmed[..^2];
            }

            return checked((long)(double.Parse(number, CultureInfo.InvariantCulture) * multiplier));
        }
    }

    private sealed record ReadResult(TimeSpan Elapsed, long Found, ulong Checksum);

    private sealed record BenchmarkResult(BenchmarkOptions Options, IReadOnlyList<ProviderResult> Providers);

    private sealed record ProviderResult(
        string Provider,
        long Records,
        long LogicalBytes,
        long DirectorySizeAfterWrite,
        long DirectorySizeAfterRead,
        double WriteSeconds,
        double FlushSeconds,
        double RandomReadSeconds,
        double WriteRecordsPerSecond,
        double RandomReadRecordsPerSecond,
        long RandomReadFound,
        ulong RandomReadChecksum);
}
