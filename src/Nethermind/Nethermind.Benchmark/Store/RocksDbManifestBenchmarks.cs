// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;

namespace Nethermind.Benchmarks.Store;

public enum RocksDbManifestVariant
{
    Baseline,
    OptimizeManifestRecovery,
    ReuseManifest,
}

public enum RocksDbManifestOperation
{
    Recovery,
    Reuse,
}

public static class RocksDbManifestBenchmarkSelection
{
    private const string VariantVariable = "NETHERMIND_ROCKSDB_MANIFEST_VARIANT";
    private static RocksDbManifestVariant? _variant;

    public static void Configure(RocksDbManifestVariant? variant)
    {
        _variant = variant;
        Environment.SetEnvironmentVariable(VariantVariable, variant?.ToString());
    }

    public static IEnumerable<RocksDbManifestVariant> GetVariantValues() =>
        GetSelectedVariant() is { } variant ? [variant] : Enum.GetValues<RocksDbManifestVariant>();

    private static RocksDbManifestVariant? GetSelectedVariant() =>
        _variant ?? ParseEnvironmentValue<RocksDbManifestVariant>(VariantVariable);

    private static TEnum? ParseEnvironmentValue<TEnum>(string variable) where TEnum : struct, Enum
    {
        string value = Environment.GetEnvironmentVariable(variable);
        return Enum.TryParse(value, ignoreCase: true, out TEnum result) ? result : null;
    }
}

public static class RocksDbManifestBenchmarkDefaults
{
    public const int SstCount = 512;
    public const int RecordsPerSst = 128;
}

internal static class RocksDbManifestBenchmarkSupport
{
    public const int SstCount = RocksDbManifestBenchmarkDefaults.SstCount;
    public const int RecordsPerSst = RocksDbManifestBenchmarkDefaults.RecordsPerSst;
    public const int RecordCount = SstCount * RecordsPerSst;

    private const int SampleStep = 257;
    private const int MutationIndex = 0;
    private const string FixtureOptions =
        "disable_auto_compactions=true;level0_slowdown_writes_trigger=1000000;level0_stop_writes_trigger=1000000;";

    public static DbConfig CreateConfig(RocksDbManifestVariant variant)
    {
        DbConfig config = RocksDbFeatureBenchmarkSupport.CreateConfig(
            RocksDbFeatureDatasetKind.Blocks,
            RocksDbFeatureVariant.Baseline);
        config.RocksDbOptions += FixtureOptions + CandidateOption(variant);
        return config;
    }

    public static DbOnTheRocks Open(string rootPath, DbConfig config, RocksDbFeatureDataset dataset)
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

    public static void WriteFixture(DbOnTheRocks db, RocksDbFeatureDataset dataset, int sstCount, int recordsPerSst)
    {
        int recordCount = GetRecordCount(sstCount, recordsPerSst, dataset);
        for (int sst = 0; sst < sstCount; sst++)
        {
            int firstRecord = sst * recordsPerSst;
            int lastRecord = Math.Min(firstRecord + recordsPerSst, recordCount);
            for (int index = firstRecord; index < lastRecord; index++)
            {
                db.Set(dataset.Keys[index], dataset.Values[index]);
            }

            db.Flush();
        }
    }

    public static ulong ValidatePersistedData(
        string rootPath,
        DbConfig config,
        RocksDbFeatureDataset dataset,
        int sstCount,
        int recordsPerSst,
        byte[] mutationValue = null)
    {
        int recordCount = GetRecordCount(sstCount, recordsPerSst, dataset);
        ulong checksum = FnvOffset;
        using DbOnTheRocks db = Open(rootPath, config, dataset);
        IReadOnlyKeyValueStore store = db;
        for (int index = 0; index < recordCount; index++)
        {
            byte[] expected = index == MutationIndex && mutationValue is not null
                ? mutationValue
                : dataset.Values[index];
            byte[] actual = store.Get(dataset.Keys[index]);
            if (actual is null || !actual.AsSpan().SequenceEqual(expected))
            {
                throw new InvalidOperationException($"Persisted fixture value mismatch at record {index}.");
            }

            checksum = Mix(checksum, dataset.Keys[index]);
            checksum = Mix(checksum, actual);
        }

        return checksum;
    }

    public static ulong ValidatePersistedDataOnCopy(
        string sourceRoot,
        DbConfig config,
        RocksDbFeatureDataset dataset,
        int sstCount,
        int recordsPerSst,
        byte[] mutationValue = null)
    {
        string validationRoot = Path.Combine(
            Path.GetTempPath(),
            "nethermind-rocksdb-manifest-validation",
            Guid.NewGuid().ToString("N"));
        try
        {
            CopyDirectory(sourceRoot, validationRoot);
            return ValidatePersistedData(
                validationRoot,
                config,
                dataset,
                sstCount,
                recordsPerSst,
                mutationValue);
        }
        finally
        {
            DeleteDirectory(validationRoot);
        }
    }

    public static int ReadValidatedSample(DbOnTheRocks db, RocksDbFeatureDataset dataset, int recordCount)
    {
        IReadOnlyKeyValueStore store = db;
        int checksum = 0;
        for (int index = 0; index < recordCount; index += SampleStep)
        {
            byte[] value = store.Get(dataset.Keys[index]);
            if (value is null || value.Length != dataset.Values[index].Length)
            {
                throw new InvalidOperationException($"Read sample mismatch at record {index}.");
            }

            checksum = unchecked(checksum + value[0] + value.Length);
        }

        return checksum;
    }

    public static ulong ComputeExpectedChecksum(
        RocksDbFeatureDataset dataset,
        int sstCount,
        int recordsPerSst,
        byte[] mutationValue = null)
    {
        int recordCount = GetRecordCount(sstCount, recordsPerSst, dataset);
        ulong checksum = FnvOffset;
        for (int index = 0; index < recordCount; index++)
        {
            byte[] value = index == MutationIndex && mutationValue is not null
                ? mutationValue
                : dataset.Values[index];
            checksum = Mix(checksum, dataset.Keys[index]);
            checksum = Mix(checksum, value);
        }

        return checksum;
    }

    public static ManifestSnapshot ReadManifest(string rootPath)
    {
        string databasePath = DbOnTheRocks.GetFullDbPath("db", rootPath);
        string currentPath = Path.Combine(databasePath, "CURRENT");
        if (!File.Exists(currentPath)) throw new InvalidOperationException($"RocksDB CURRENT is missing from {databasePath}.");

        string manifestName = File.ReadAllText(currentPath).Trim();
        string manifestPath = Path.Combine(databasePath, manifestName);
        if (!File.Exists(manifestPath)) throw new InvalidOperationException($"RocksDB MANIFEST '{manifestName}' is missing.");

        return new ManifestSnapshot(manifestName, new FileInfo(manifestPath).Length);
    }

    public static int CountSstFiles(string rootPath)
    {
        string databasePath = DbOnTheRocks.GetFullDbPath("db", rootPath);
        int count = 0;
        foreach (string file in Directory.EnumerateFiles(databasePath, "*.sst", SearchOption.AllDirectories))
        {
            if (File.Exists(file)) count++;
        }

        return count;
    }

    public static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relativePath));
        }

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(source, file);
            string destinationFile = Path.Combine(destination, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(file, destinationFile);
        }
    }

    public static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    public static byte[] CreateMutationValue(RocksDbFeatureDataset dataset)
    {
        byte[] mutation = (byte[])dataset.Values[MutationIndex].Clone();
        mutation[0] ^= 0x5A;
        return mutation;
    }

    public static int GetRecordCount(int sstCount, int recordsPerSst, RocksDbFeatureDataset dataset)
    {
        if (sstCount <= 0) throw new ArgumentOutOfRangeException(nameof(sstCount));
        if (recordsPerSst <= 0) throw new ArgumentOutOfRangeException(nameof(recordsPerSst));

        long recordCount = (long)sstCount * recordsPerSst;
        if (recordCount > dataset.Keys.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(recordsPerSst), "The fixture exceeds the deterministic Blocks dataset.");
        }

        return (int)recordCount;
    }

    public static ulong Mix(ulong state, ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            state ^= bytes[i];
            state *= FnvPrime;
        }

        return state;
    }

    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private static string CandidateOption(RocksDbManifestVariant variant) => variant switch
    {
        RocksDbManifestVariant.OptimizeManifestRecovery => "optimize_manifest_for_recovery=true;",
        RocksDbManifestVariant.ReuseManifest => "reuse_manifest_on_open=true;",
        _ => string.Empty,
    };
}

public readonly record struct ManifestSnapshot(string FileName, long Length);

[MemoryDiagnoser]
public class RocksDbManifestBenchmarks
{
    private RocksDbFeatureDataset _dataset = null!;
    private RocksDbManifestVariant _variant;
    private string _templateRoot = null!;
    private string _iterationRoot = null!;
    private DbConfig _config = null!;
    private byte[] _mutationValue = null!;
    private ManifestSnapshot _templateManifest;

    [ParamsSource(nameof(GetVariantValues))]
    public RocksDbManifestVariant Variant
    {
        get => _variant;
        set => _variant = value;
    }

    public static IEnumerable<RocksDbManifestVariant> GetVariantValues() =>
        RocksDbManifestBenchmarkSelection.GetVariantValues();

    [GlobalSetup]
    public void Setup()
    {
        _dataset = RocksDbFeatureDatasetFactory.Create(RocksDbFeatureDatasetKind.Blocks);
        _config = RocksDbManifestBenchmarkSupport.CreateConfig(Variant);
        _mutationValue = RocksDbManifestBenchmarkSupport.CreateMutationValue(_dataset);
        _templateRoot = Path.Combine(Path.GetTempPath(), "nethermind-rocksdb-manifest", Guid.NewGuid().ToString("N"));
        _iterationRoot = Path.Combine(Path.GetTempPath(), "nethermind-rocksdb-manifest-iteration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_templateRoot);

        using (DbOnTheRocks db = RocksDbManifestBenchmarkSupport.Open(_templateRoot, _config, _dataset))
        {
            RocksDbManifestBenchmarkSupport.WriteFixture(
                db,
                _dataset,
                RocksDbManifestBenchmarkSupport.SstCount,
                RocksDbManifestBenchmarkSupport.RecordsPerSst);
        }

        _templateManifest = RocksDbManifestBenchmarkSupport.ReadManifest(_templateRoot);
        AssertFixture(_templateRoot, _templateManifest);
        ulong checksum = RocksDbManifestBenchmarkSupport.ValidatePersistedDataOnCopy(
            _templateRoot,
            _config,
            _dataset,
            RocksDbManifestBenchmarkSupport.SstCount,
            RocksDbManifestBenchmarkSupport.RecordsPerSst);
        if (checksum == 0) throw new InvalidOperationException("The persisted manifest fixture checksum was empty.");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        RocksDbManifestBenchmarkSupport.DeleteDirectory(_iterationRoot);
        RocksDbManifestBenchmarkSupport.DeleteDirectory(_templateRoot);
    }

    [IterationSetup]
    public void IterationSetup()
    {
        RocksDbManifestBenchmarkSupport.DeleteDirectory(_iterationRoot);
        RocksDbManifestBenchmarkSupport.CopyDirectory(_templateRoot, _iterationRoot);
        ManifestSnapshot iterationManifest = RocksDbManifestBenchmarkSupport.ReadManifest(_iterationRoot);
        AssertFixture(_iterationRoot, iterationManifest);
        if (iterationManifest != _templateManifest)
        {
            throw new InvalidOperationException("The iteration copy changed the first-clean-close MANIFEST identity or size.");
        }
    }

    [IterationCleanup]
    public void IterationCleanup() => RocksDbManifestBenchmarkSupport.DeleteDirectory(_iterationRoot);

    [Benchmark]
    [InvocationCount(1)]
    public int RecoveryOpenReadClose()
    {
        using DbOnTheRocks db = RocksDbManifestBenchmarkSupport.Open(_iterationRoot, _config, _dataset);
        return RocksDbManifestBenchmarkSupport.ReadValidatedSample(
            db,
            _dataset,
            RocksDbManifestBenchmarkSupport.RecordCount);
    }

    [Benchmark]
    [InvocationCount(1)]
    public int ReuseOpenWriteFlushReadClose()
    {
        using DbOnTheRocks db = RocksDbManifestBenchmarkSupport.Open(_iterationRoot, _config, _dataset);
        db.Set(_dataset.Keys[0], _mutationValue);
        db.Flush();
        byte[] value = ((IReadOnlyKeyValueStore)db).Get(_dataset.Keys[0]);
        if (value is null || !value.AsSpan().SequenceEqual(_mutationValue))
        {
            throw new InvalidOperationException("The manifest reuse mutation was not persisted.");
        }

        return value.Length;
    }

    private static void AssertFixture(string rootPath, ManifestSnapshot manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.FileName) || manifest.Length <= 0)
        {
            throw new InvalidOperationException("The manifest fixture did not produce a usable MANIFEST file.");
        }

        if (RocksDbManifestBenchmarkSupport.CountSstFiles(rootPath) != RocksDbManifestBenchmarkSupport.SstCount)
        {
            throw new InvalidOperationException("The manifest fixture did not produce exactly 512 SST files.");
        }
    }
}

public static class RocksDbManifestStandaloneRunner
{
    public static void Run(
        RocksDbManifestVariant variant,
        RocksDbManifestOperation operation,
        int sstCount,
        int recordsPerSst)
    {
        using Process process = Process.GetCurrentProcess();
        RocksDbFeatureDataset dataset = RocksDbFeatureDatasetFactory.Create(RocksDbFeatureDatasetKind.Blocks);
        int recordCount = RocksDbManifestBenchmarkSupport.GetRecordCount(sstCount, recordsPerSst, dataset);
        byte[] mutationValue = RocksDbManifestBenchmarkSupport.CreateMutationValue(dataset);
        string templateRoot = Path.Combine(Path.GetTempPath(), "nethermind-rocksdb-manifest-standalone", Guid.NewGuid().ToString("N"));
        string operationRoot = Path.Combine(Path.GetTempPath(), "nethermind-rocksdb-manifest-operation", Guid.NewGuid().ToString("N"));
        DbOnTheRocks db = null;

        try
        {
            DbConfig config = RocksDbManifestBenchmarkSupport.CreateConfig(variant);
            Directory.CreateDirectory(templateRoot);

            ProcessSnapshot prepareBefore = Capture(process);
            Stopwatch prepareClock = Stopwatch.StartNew();
            db = RocksDbManifestBenchmarkSupport.Open(templateRoot, config, dataset);
            RocksDbManifestBenchmarkSupport.WriteFixture(db, dataset, sstCount, recordsPerSst);
            prepareClock.Stop();
            ProcessSnapshot prepareAfter = Capture(process);
            PhaseReport prepare = PhaseReport.Create(prepareClock, prepareBefore, prepareAfter);

            ProcessSnapshot closeBefore = Capture(process);
            Stopwatch closeClock = Stopwatch.StartNew();
            db.Dispose();
            db = null;
            closeClock.Stop();
            ProcessSnapshot closeAfter = Capture(process);
            PhaseReport cleanClose = PhaseReport.Create(closeClock, closeBefore, closeAfter);

            ManifestSnapshot templateManifest = RocksDbManifestBenchmarkSupport.ReadManifest(templateRoot);
            int templateSstCount = RocksDbManifestBenchmarkSupport.CountSstFiles(templateRoot);
            if (templateSstCount != sstCount)
            {
                throw new InvalidOperationException($"The template contained {templateSstCount} SST files; expected {sstCount}.");
            }

            ulong templateChecksum = RocksDbManifestBenchmarkSupport.ValidatePersistedDataOnCopy(
                templateRoot,
                config,
                dataset,
                sstCount,
                recordsPerSst);

            ProcessSnapshot copyBefore = Capture(process);
            Stopwatch copyClock = Stopwatch.StartNew();
            RocksDbManifestBenchmarkSupport.CopyDirectory(templateRoot, operationRoot);
            copyClock.Stop();
            ProcessSnapshot copyAfter = Capture(process);
            PhaseReport templateCopy = PhaseReport.Create(copyClock, copyBefore, copyAfter);

            ManifestSnapshot operationManifestBefore = RocksDbManifestBenchmarkSupport.ReadManifest(operationRoot);
            int operationSstCountBefore = RocksDbManifestBenchmarkSupport.CountSstFiles(operationRoot);
            if (operationSstCountBefore != sstCount)
            {
                throw new InvalidOperationException($"The operation template contained {operationSstCountBefore} SST files; expected {sstCount}.");
            }

            if (operationManifestBefore != templateManifest)
            {
                throw new InvalidOperationException("The operation copy changed the first-clean-close MANIFEST identity or size.");
            }

            ProcessSnapshot operationBefore = Capture(process);
            Stopwatch operationClock = Stopwatch.StartNew();
            using (DbOnTheRocks operationDb = RocksDbManifestBenchmarkSupport.Open(operationRoot, config, dataset))
            {
                if (operation == RocksDbManifestOperation.Recovery)
                {
                    RocksDbManifestBenchmarkSupport.ReadValidatedSample(operationDb, dataset, recordCount);
                }
                else
                {
                    operationDb.Set(dataset.Keys[0], mutationValue);
                    operationDb.Flush();
                    byte[] value = ((IReadOnlyKeyValueStore)operationDb).Get(dataset.Keys[0]);
                    if (value is null || !value.AsSpan().SequenceEqual(mutationValue))
                    {
                        throw new InvalidOperationException("The manifest reuse mutation was not persisted.");
                    }
                }
            }

            operationClock.Stop();
            ProcessSnapshot operationAfter = Capture(process);
            PhaseReport operationPhase = PhaseReport.Create(operationClock, operationBefore, operationAfter);

            // Capture immediately after the measured close. A validation reopen below must not affect this evidence.
            ManifestSnapshot operationManifestAfter = RocksDbManifestBenchmarkSupport.ReadManifest(operationRoot);
            int operationSstCountAfter = RocksDbManifestBenchmarkSupport.CountSstFiles(operationRoot);
            int expectedSstCountAfter = operation == RocksDbManifestOperation.Reuse
                ? checked(operationSstCountBefore + 1)
                : operationSstCountBefore;
            if (operationSstCountAfter != expectedSstCountAfter)
            {
                throw new InvalidOperationException(
                    $"The {operation} operation produced {operationSstCountAfter} SST files; expected {expectedSstCountAfter}.");
            }

            byte[] expectedMutation = operation == RocksDbManifestOperation.Reuse ? mutationValue : null;
            ulong persistedChecksum = RocksDbManifestBenchmarkSupport.ValidatePersistedData(
                operationRoot,
                config,
                dataset,
                sstCount,
                recordsPerSst,
                expectedMutation);
            ulong expectedChecksum = operation == RocksDbManifestOperation.Reuse
                ? RocksDbManifestBenchmarkSupport.ComputeExpectedChecksum(
                    dataset,
                    sstCount,
                    recordsPerSst,
                    mutationValue)
                : templateChecksum;

            ManifestStandaloneReport report = new()
            {
                Schema = "rocksdb-manifest-v1",
                Dataset = RocksDbFeatureDatasetKind.Blocks.ToString(),
                Variant = variant.ToString(),
                OperationName = operation.ToString(),
                SstCountExpected = sstCount,
                SstCountTemplate = templateSstCount,
                SstCountBefore = operationSstCountBefore,
                SstCountAfter = operationSstCountAfter,
                RecordCount = recordCount,
                TemplateChecksum = templateChecksum,
                PersistedChecksum = persistedChecksum,
                ExpectedChecksum = expectedChecksum,
                DataChecksumMatches = persistedChecksum == expectedChecksum,
                TemplateManifestName = templateManifest.FileName,
                TemplateManifestBytes = templateManifest.Length,
                ManifestBeforeName = operationManifestBefore.FileName,
                ManifestBeforeBytes = operationManifestBefore.Length,
                ManifestAfterName = operationManifestAfter.FileName,
                ManifestAfterBytes = operationManifestAfter.Length,
                ManifestSameFile = operationManifestBefore.FileName == operationManifestAfter.FileName,
                ManifestBytesDelta = operationManifestAfter.Length - operationManifestBefore.Length,
                Prepare = prepare,
                CleanClose = cleanClose,
                TemplateCopy = templateCopy,
                OperationPhase = operationPhase,
                MemorySemantics = "private_bytes_phase_max and working_set_bytes_phase_max are sampled phase maxima, not true process peaks",
                ActivationEvidence = "indirect MANIFEST filename/size evidence only; shipped bindings expose no native SyncPoint counters",
            };

            if (!report.DataChecksumMatches) throw new InvalidOperationException("The persisted checksum did not match the expected fixture.");
            Console.WriteLine(JsonSerializer.Serialize(report));
        }
        finally
        {
            db?.Dispose();
            RocksDbManifestBenchmarkSupport.DeleteDirectory(operationRoot);
            RocksDbManifestBenchmarkSupport.DeleteDirectory(templateRoot);
        }
    }

    private static ProcessSnapshot Capture(Process process)
    {
        process.Refresh();
        return new ProcessSnapshot(process.TotalProcessorTime, process.PrivateMemorySize64, process.WorkingSet64);
    }

    private readonly record struct ProcessSnapshot(TimeSpan CpuTime, long PrivateBytes, long WorkingSetBytes);

    private sealed class PhaseReport
    {
        [JsonPropertyName("wall_ms")]
        public double WallMilliseconds { get; init; }

        [JsonPropertyName("cpu_ms")]
        public double CpuMilliseconds { get; init; }

        [JsonPropertyName("private_bytes_before")]
        public long PrivateBytesBefore { get; init; }

        [JsonPropertyName("private_bytes_after")]
        public long PrivateBytesAfter { get; init; }

        [JsonPropertyName("private_bytes_phase_max")]
        public long PrivateBytesPhaseMax { get; init; }

        [JsonPropertyName("working_set_bytes_before")]
        public long WorkingSetBytesBefore { get; init; }

        [JsonPropertyName("working_set_bytes_after")]
        public long WorkingSetBytesAfter { get; init; }

        [JsonPropertyName("working_set_bytes_phase_max")]
        public long WorkingSetBytesPhaseMax { get; init; }

        public static PhaseReport Create(Stopwatch clock, ProcessSnapshot before, ProcessSnapshot after) => new()
        {
            WallMilliseconds = clock.Elapsed.TotalMilliseconds,
            CpuMilliseconds = (after.CpuTime - before.CpuTime).TotalMilliseconds,
            PrivateBytesBefore = before.PrivateBytes,
            PrivateBytesAfter = after.PrivateBytes,
            PrivateBytesPhaseMax = Math.Max(before.PrivateBytes, after.PrivateBytes),
            WorkingSetBytesBefore = before.WorkingSetBytes,
            WorkingSetBytesAfter = after.WorkingSetBytes,
            WorkingSetBytesPhaseMax = Math.Max(before.WorkingSetBytes, after.WorkingSetBytes),
        };
    }

    private sealed class ManifestStandaloneReport
    {
        [JsonPropertyName("schema")]
        public string Schema { get; init; } = null!;

        [JsonPropertyName("dataset")]
        public string Dataset { get; init; } = null!;

        [JsonPropertyName("variant")]
        public string Variant { get; init; } = null!;

        [JsonPropertyName("operation")]
        public string OperationName { get; init; } = null!;

        [JsonPropertyName("sst_count_expected")]
        public int SstCountExpected { get; init; }

        [JsonPropertyName("sst_count_template")]
        public int SstCountTemplate { get; init; }

        [JsonPropertyName("sst_count_before")]
        public int SstCountBefore { get; init; }

        [JsonPropertyName("sst_count_after")]
        public int SstCountAfter { get; init; }

        [JsonPropertyName("record_count")]
        public int RecordCount { get; init; }

        [JsonPropertyName("template_checksum")]
        public ulong TemplateChecksum { get; init; }

        [JsonPropertyName("persisted_checksum")]
        public ulong PersistedChecksum { get; init; }

        [JsonPropertyName("expected_checksum")]
        public ulong ExpectedChecksum { get; init; }

        [JsonPropertyName("data_checksum_matches")]
        public bool DataChecksumMatches { get; init; }

        [JsonPropertyName("template_manifest_name")]
        public string TemplateManifestName { get; init; } = null!;

        [JsonPropertyName("template_manifest_bytes")]
        public long TemplateManifestBytes { get; init; }

        [JsonPropertyName("manifest_before_name")]
        public string ManifestBeforeName { get; init; } = null!;

        [JsonPropertyName("manifest_before_bytes")]
        public long ManifestBeforeBytes { get; init; }

        [JsonPropertyName("manifest_after_name")]
        public string ManifestAfterName { get; init; } = null!;

        [JsonPropertyName("manifest_after_bytes")]
        public long ManifestAfterBytes { get; init; }

        [JsonPropertyName("manifest_same_file")]
        public bool ManifestSameFile { get; init; }

        [JsonPropertyName("manifest_bytes_delta")]
        public long ManifestBytesDelta { get; init; }

        [JsonPropertyName("prepare")]
        public PhaseReport Prepare { get; init; } = null!;

        [JsonPropertyName("clean_close")]
        public PhaseReport CleanClose { get; init; } = null!;

        [JsonPropertyName("template_copy")]
        public PhaseReport TemplateCopy { get; init; } = null!;

        [JsonPropertyName("operation_phase")]
        public PhaseReport OperationPhase { get; init; } = null!;

        [JsonPropertyName("memory_semantics")]
        public string MemorySemantics { get; init; } = null!;

        [JsonPropertyName("activation_evidence")]
        public string ActivationEvidence { get; init; } = null!;
    }
}
