using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.RocksDbBindings;
using Nethermind.State.Flat;

namespace Nethermind.Tools.AccountIndexPrepare;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0].Equals("--self-test", StringComparison.OrdinalIgnoreCase))
            {
                SelfTest.Run();
                Console.WriteLine("AccountIndexPrepare self-test passed.");
                return 0;
            }

            PrepareArguments parsed = PrepareArguments.Parse(args);
            AccountIndexPreparer.Prepare(parsed);
            return 0;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"argument error: {exception.Message}");
            Console.Error.WriteLine(PrepareArguments.Usage);
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"AccountIndexPrepare failed: {exception.Message}");
            return 1;
        }
    }
}

internal enum AccountIndexMode
{
    Binary,
    Interpolation,
    Auto,
}

internal sealed record PrepareArguments(
    string DbPath,
    string ScratchRoot,
    AccountIndexMode Mode,
    double Threshold,
    string Output)
{
    internal const string Usage =
        "AccountIndexPrepare --db-path <absolute isolated-view/mainnet/flat> " +
        "--scratch-root <isolated-view> --mode binary|interpolation|auto " +
        "--threshold <-1|0..1> --output <JSONpath>";

    internal static PrepareArguments Parse(string[] args)
    {
        if (args.Length == 0 || args.Length % 2 != 0)
        {
            throw new ArgumentException("all options require a value");
        }

        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> knownOptions = ["--db-path", "--scratch-root", "--mode", "--threshold", "--output"];
        for (int i = 0; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)
                || !knownOptions.Contains(args[i])
                || !values.TryAdd(args[i], args[i + 1]))
            {
                throw new ArgumentException($"invalid or duplicate option '{args[i]}'");
            }
        }

        string dbPath = Required(values, "--db-path");
        string scratchRoot = Required(values, "--scratch-root");
        string modeText = Required(values, "--mode");
        string output = Required(values, "--output");
        if (!Path.IsPathRooted(dbPath) || !Path.IsPathRooted(scratchRoot) || !Path.IsPathRooted(output))
        {
            throw new ArgumentException("--db-path, --scratch-root, and --output must be absolute paths");
        }

        if (!Enum.TryParse(modeText, ignoreCase: true, out AccountIndexMode mode))
        {
            throw new ArgumentException($"unsupported mode '{modeText}'");
        }
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentException($"unsupported mode '{modeText}'");
        }

        string thresholdText = Required(values, "--threshold");
        if (!double.TryParse(thresholdText, NumberStyles.Float, CultureInfo.InvariantCulture, out double threshold)
            || double.IsNaN(threshold)
            || double.IsInfinity(threshold)
            || (mode == AccountIndexMode.Auto && (threshold < 0 || threshold > 1))
            || (mode != AccountIndexMode.Auto && threshold != -1))
        {
            throw new ArgumentException("binary/interpolation require --threshold -1; auto requires a number in [0,1]");
        }

        return new PrepareArguments(dbPath, scratchRoot, mode, threshold, output);
    }

    private static string Required(IReadOnlyDictionary<string, string> values, string name) =>
        values.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"missing {name}");
}

internal static class AccountIndexPreparer
{
    private const string MarkerName = ".account-index-prepare-owned";
    private const string DisableAutomaticCompactions = "disable_auto_compactions=true;";
    private const string AccountSearchOption = "block_based_table_factory.index_block_search_type";
    private const string UniformThresholdOption = "block_based_table_factory.uniform_cv_threshold";
    private const string CurrentName = "CURRENT";
    private const string AccountColumnName = nameof(FlatDbColumns.Account);

    private static readonly FlatDbColumns[] FlatColumns = Enum.GetValues<FlatDbColumns>();

    internal static void Prepare(PrepareArguments arguments)
    {
        ValidatedPaths paths = ValidatePaths(arguments);
        Stopwatch wallClock = Stopwatch.StartNew();
        Process process = Process.GetCurrentProcess();
        ProcessSnapshot before = ProcessSnapshot.Capture(process);
        DbLayoutManifest layout = ReadLayoutManifest(paths.DbPath);

        DbConfig config = CreateConfig(arguments.Mode, arguments.Threshold);
        RocksDbConfigFactory factory = new(
            config,
            new PruningConfig(),
            new FixedHardwareInfo(),
            LimboLogs.Instance,
            validateConfig: false);
        AccountContent beforeContent;
        AccountContent afterContent;
        TableProperties beforeProperties;
        TableProperties afterProperties;
        IReadOnlyList<AccountSstIdentity> beforeSstFiles;
        IReadOnlyList<AccountSstIdentity> afterSstFiles;
        string resolvedIndexSearch;
        string resolvedThreshold;

        IRocksDbConfig accountConfig = factory.GetForDatabase(DbNames.Flat, AccountColumnName);
        IDictionary<string, string> resolvedOptions = DbOnTheRocks.ExtractOptions(
            accountConfig.RocksDbOptions + accountConfig.AdditionalRocksDbOptions);
        resolvedIndexSearch = RequiredOption(resolvedOptions, AccountSearchOption);
        resolvedThreshold = RequiredOption(resolvedOptions, UniformThresholdOption);
        foreach (FlatDbColumns column in FlatColumns)
        {
            IRocksDbConfig columnConfig = factory.GetForDatabase(DbNames.Flat, column.ToString());
            IDictionary<string, string> columnOptions = DbOnTheRocks.ExtractOptions(
                columnConfig.RocksDbOptions + columnConfig.AdditionalRocksDbOptions);
            if (!columnOptions.TryGetValue("disable_auto_compactions", out string? disabled)
                || !disabled.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"disable_auto_compactions was not resolved for Flat column '{column}'");
            }
        }

        using (ColumnsDb<FlatDbColumns> columns = new(
                   paths.ScratchRoot,
                   new DbSettings(DbNames.Flat, paths.DbPath),
                   config,
                   factory,
                   LimboLogs.Instance,
                   FlatColumns))
        {
            IDb account = columns.GetColumnDb(FlatDbColumns.Account);
            beforeContent = ContentDigest(account);
            beforeProperties = ReadTableProperties(columns);
            beforeSstFiles = ReadAccountSstIdentities(columns);

            ForceAccountBottommostCompaction(columns);

            afterContent = ContentDigest(account);
            afterProperties = ReadTableProperties(columns);
            afterSstFiles = ReadAccountSstIdentities(columns);
        }

        process.Refresh();
        ProcessSnapshot after = ProcessSnapshot.Capture(process);
        wallClock.Stop();

        if (beforeContent != afterContent)
        {
            throw new InvalidOperationException("Account contents changed during bottommost compaction");
        }

        if (beforeSstFiles.Count == 0 || afterSstFiles.Count == 0)
        {
            throw new InvalidOperationException("Account column family has no SST files to rewrite");
        }

        HashSet<ulong> beforeSstNumbers = beforeSstFiles.Select(static file => file.Number).ToHashSet();
        HashSet<ulong> afterSstNumbers = afterSstFiles.Select(static file => file.Number).ToHashSet();
        bool sstChanged = beforeSstNumbers.Count != 0
            && afterSstNumbers.Count != 0
            && !beforeSstNumbers.Overlaps(afterSstNumbers);
        if (!sstChanged)
        {
            throw new InvalidOperationException("Account compaction did not replace every Account SST identity");
        }

        PrepareReport report = new(
            SchemaVersion: 1,
            DbPath: paths.DbPath,
            ScratchRoot: paths.ScratchRoot,
            Mode: arguments.Mode.ToString().ToLowerInvariant(),
            Threshold: arguments.Threshold,
            Options: new ResolvedOptions(resolvedIndexSearch, resolvedThreshold, AutomaticCompactionsDisabled: true),
            Content: new ContentReport(beforeContent, afterContent, Unchanged: true),
            Tables: new TableReport(beforeProperties, afterProperties),
            Ssts: new SstReport(
                beforeSstFiles,
                afterSstFiles,
                Rewritten: sstChanged),
            Timing: new TimingReport(
                wallClock.Elapsed.TotalMilliseconds,
                (after.CpuTime - before.CpuTime).TotalMilliseconds,
                before.WorkingSetBytes,
                after.WorkingSetBytes,
                Math.Max(before.PeakWorkingSetBytes, after.PeakWorkingSetBytes),
                before.PrivateBytes,
                after.PrivateBytes),
            Layout: layout);

        WriteReport(arguments.Output, paths.ScratchRoot, report);
    }

    private static DbConfig CreateConfig(AccountIndexMode mode, double threshold)
    {
        string searchType = mode switch
        {
            AccountIndexMode.Binary => "kBinary",
            AccountIndexMode.Interpolation => "kInterpolation",
            AccountIndexMode.Auto => "kAuto",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };
        string thresholdOption = threshold.ToString("R", CultureInfo.InvariantCulture);
        return new DbConfig
        {
            // This option is applied to the DB and every column-family options object while it is opened.
            // It is deliberately an open-time diagnostic override; it does not alter the source defaults.
            AdditionalRocksDbOptions = DisableAutomaticCompactions,
            FlatAccountDbAdditionalRocksDbOptions =
                $"{AccountSearchOption}={searchType};{UniformThresholdOption}={thresholdOption};",
        };
    }

    private static (RocksDb Database, IColumnFamilyHandle Family) GetAccountHandles(ColumnsDb<FlatDbColumns> columns)
    {
        FieldInfo databaseField = typeof(DbOnTheRocks).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(DbOnTheRocks).FullName, "_db");
        FieldInfo familyField = typeof(ColumnDb).GetField("_columnFamily", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(ColumnDb).FullName, "_columnFamily");
        RocksDb database = (RocksDb)(databaseField.GetValue(columns)
            ?? throw new InvalidOperationException("RocksDB handle is unavailable"));
        ColumnDb account = (ColumnDb)columns.GetColumnDb(FlatDbColumns.Account);
        IColumnFamilyHandle family = (IColumnFamilyHandle)(familyField.GetValue(account)
            ?? throw new InvalidOperationException("Account column-family handle is unavailable"));
        return (database, family);
    }

    private static void ForceAccountBottommostCompaction(ColumnsDb<FlatDbColumns> columns)
    {
        (RocksDb rocksDb, IColumnFamilyHandle accountFamily) = GetAccountHandles(columns);

        // This is the diagnostic-only equivalent of DbOnTheRocks.CompactOpenRange. The production helper is
        // internal, so reflection is kept here rather than widening the production API for a one-shot tool.
        rocksDb.CompactRange(null, null, forceBottommost: true, accountFamily);
    }

    private static TableProperties ReadTableProperties(ColumnsDb<FlatDbColumns> columns)
    {
        (RocksDb rocksDb, IColumnFamilyHandle accountFamily) = GetAccountHandles(columns);

        string? aggregate = rocksDb.GetProperty("rocksdb.aggregated-table-properties", accountFamily);
        long indexBytes = ParseRequiredLongProperty(aggregate, "index block size (", ")", "index block size");
        long filterBytes = ParseRequiredLongProperty(aggregate, "filter block size=", null, "filter block size");
        long estimateTableReadersMemory = TryGetRequiredProperty(rocksDb, "rocksdb.estimate-table-readers-mem", accountFamily);
        return new TableProperties(
            UniformBlocks: ParseRequiredLongProperty(aggregate, "# uniform blocks=", null, "# uniform blocks"),
            IndexBytes: indexBytes,
            FilterBytes: filterBytes,
            EstimateTableReadersMemory: estimateTableReadersMemory,
            Entries: ParseRequiredLongProperty(aggregate, "# entries=", null, "# entries"));
    }

    private static IReadOnlyList<AccountSstIdentity> ReadAccountSstIdentities(ColumnsDb<FlatDbColumns> columns)
    {
        (RocksDb database, IColumnFamilyHandle family) = GetAccountHandles(columns);
        string metadata = database.GetProperty("rocksdb.sstables", family)
            ?? throw new InvalidOperationException("RocksDB did not report Account SST metadata");
        if (string.IsNullOrWhiteSpace(metadata))
        {
            throw new InvalidOperationException("RocksDB returned empty Account SST metadata");
        }

        List<AccountSstIdentity> files = [];
        foreach (string line in metadata.Split('\n'))
        {
            ReadOnlySpan<char> candidate = line.AsSpan().Trim();
            int separator = candidate.IndexOf(':');
            int details = candidate.IndexOf('[');
            if (separator <= 0 || details <= separator)
            {
                continue;
            }

            if (ulong.TryParse(candidate[..separator], NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong fileNumber)
                && long.TryParse(candidate[(separator + 1)..details], NumberStyles.Integer, CultureInfo.InvariantCulture, out long size)
                && size >= 0)
            {
                files.Add(new AccountSstIdentity(fileNumber, size));
            }
        }

        files.Sort(static (left, right) =>
        {
            int number = left.Number.CompareTo(right.Number);
            return number != 0 ? number : left.SizeBytes.CompareTo(right.SizeBytes);
        });
        return files;
    }

    private static long TryGetRequiredProperty(RocksDb database, string property, IColumnFamilyHandle family)
    {
        if (!database.TryGetIntProperty(property, family, out ulong value) || value > long.MaxValue)
        {
            throw new InvalidOperationException($"RocksDB did not report required Account property '{property}'");
        }

        return (long)value;
    }

    private static long ParseRequiredLongProperty(string? properties, string prefix, string? suffix, string propertyName)
    {
        if (string.IsNullOrEmpty(properties))
        {
            throw new InvalidOperationException($"RocksDB did not report required Account table property '{propertyName}'");
        }

        int start = properties.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException($"RocksDB did not report required Account table property '{propertyName}'");
        }

        start += prefix.Length;
        int valueStart = start;
        if (suffix is not null)
        {
            int suffixEnd = properties.IndexOf(suffix, start, StringComparison.Ordinal);
            if (suffixEnd < 0)
            {
                throw new InvalidOperationException($"RocksDB returned malformed Account table property '{propertyName}'");
            }

            int equals = properties.IndexOf('=', suffixEnd + suffix.Length);
            if (equals < 0)
            {
                throw new InvalidOperationException($"RocksDB returned malformed Account table property '{propertyName}'");
            }

            valueStart = equals + 1;
        }

        int end = properties.IndexOf(';', valueStart);
        if (end < 0)
        {
            throw new InvalidOperationException($"RocksDB returned malformed Account table property '{propertyName}'");
        }

        string valueText = properties[valueStart..end].Trim();

        if (!long.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
        {
            throw new InvalidOperationException($"RocksDB returned malformed Account table property '{propertyName}'");
        }

        return value;
    }

    private static AccountContent ContentDigest(IDb account)
    {
        long count = 0;
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (KeyValuePair<byte[], byte[]> entry in account.GetAll(ordered: true))
        {
            AppendLengthAndBytes(hash, entry.Key);
            AppendLengthAndBytes(hash, entry.Value);
            count++;
        }

        return new AccountContent(count, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static void AppendLengthAndBytes(IncrementalHash hash, ReadOnlySpan<byte> bytes)
    {
        Span<byte> length = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static ValidatedPaths ValidatePaths(PrepareArguments arguments)
    {
        string scratchRoot = Path.GetFullPath(arguments.ScratchRoot);
        string dbPath = Path.GetFullPath(arguments.DbPath);
        string output = Path.GetFullPath(arguments.Output);
        EnsureDirectory(scratchRoot, "scratch root");
        EnsureNoReparsePoints(scratchRoot);
        EnsureContained(scratchRoot, dbPath, "database path");
        EnsureDirectory(dbPath, "database path");
        string? mainnetPath = Path.GetDirectoryName(dbPath);
        if (!Path.GetFileName(dbPath).Equals("flat", StringComparison.OrdinalIgnoreCase)
            || mainnetPath is null
            || !Path.GetFileName(mainnetPath).Equals("mainnet", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("database path must end in mainnet\\flat");
        }
        EnsureNoReparsePoints(dbPath);
        string markerPath = Path.Combine(scratchRoot, MarkerName);
        EnsureRegularFile(markerPath, "ownership marker");
        string marker = File.ReadAllText(markerPath, Encoding.UTF8).Trim();
        if (!marker.Equals("createdbyharness", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("ownership marker must contain 'createdbyharness'");
        }

        string current = Path.Combine(dbPath, CurrentName);
        EnsureRegularFile(current, CurrentName);
        string manifestName = File.ReadAllText(current, Encoding.UTF8).Trim();
        if (!manifestName.StartsWith("MANIFEST-", StringComparison.Ordinal)
            || manifestName.IndexOfAny(['/', '\\']) >= 0)
        {
            throw new InvalidOperationException("CURRENT does not identify a local MANIFEST file");
        }

        string manifest = Path.Combine(dbPath, manifestName);
        EnsureRegularFile(manifest, manifestName);
        ValidateKnownColumnFamilies(dbPath);

        string outputDirectory = Path.GetDirectoryName(output)
            ?? throw new ArgumentException("--output has no parent directory");
        EnsureDirectory(outputDirectory, "output directory");
        EnsureNoReparsePointsToExistingPath(outputDirectory);
        string relativeToDb = Path.GetRelativePath(dbPath, output);
        if (relativeToDb.Equals(".", StringComparison.Ordinal)
            || (!relativeToDb.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !relativeToDb.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("output must not be inside the database directory");
        }
        if (File.Exists(output)) throw new IOException($"output file already exists: {output}");

        return new ValidatedPaths(scratchRoot, dbPath, output, manifestName);
    }

    private static DbLayoutManifest ReadLayoutManifest(string dbPath)
    {
        string current = File.ReadAllText(Path.Combine(dbPath, CurrentName), Encoding.UTF8).Trim();
        string manifest = Path.GetFileName(current);
        List<string> files = [];
        foreach (string file in Directory.EnumerateFiles(dbPath, "*", SearchOption.TopDirectoryOnly))
        {
            files.Add(Path.GetFileName(file));
        }

        files.Sort(StringComparer.Ordinal);
        return new DbLayoutManifest(current, manifest, files, FlatColumns.Select(static column => column.ToString()).ToArray());
    }

    private static void WriteReport(string output, string scratchRoot, PrepareReport report)
    {
        string parent = Path.GetDirectoryName(output)
            ?? throw new ArgumentException("--output has no parent directory");
        EnsureNoReparsePointsToExistingPath(parent);
        string relativeToDb = Path.GetRelativePath(report.DbPath, output);
        if (relativeToDb.Equals(".", StringComparison.Ordinal)
            || (!relativeToDb.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !relativeToDb.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("output must not be inside the database directory");
        }
        JsonSerializerOptions options = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };
        string json = JsonSerializer.Serialize(report, options);
        using FileStream stream = new(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using StreamWriter writer = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(json);
        writer.WriteLine();
    }

    private static string RequiredOption(IDictionary<string, string> options, string name) =>
        options.TryGetValue(name, out string? value) ? value : throw new InvalidOperationException($"resolved option '{name}' is unavailable");

    private static void EnsureDirectory(string path, string description)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"{description} does not exist: {path}");
        if ((File.GetAttributes(path) & FileAttributes.Directory) == 0) throw new IOException($"{description} is not a directory: {path}");
    }

    private static void EnsureRegularFile(string path, string description)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"{description} does not exist: {path}", path);
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new IOException($"{description} must be a regular file: {path}");
        }
    }

    private static void EnsureContained(string root, string path, string description)
    {
        string relative = Path.GetRelativePath(root, path);
        if (relative.Equals(".", StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException($"{description} must be inside scratch root '{root}': {path}");
        }
    }

    private static void EnsureNoReparsePoints(string root)
    {
        EnsureNoReparsePointsToExistingPath(root);
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.Count != 0)
        {
            string directory = pending.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException($"reparse point is not allowed below scratch root: {entry}");
                }

                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        }
    }

    private static void EnsureNoReparsePointsToExistingPath(string path)
    {
        string current = Path.GetFullPath(path);
        while (true)
        {
            if (Directory.Exists(current) || File.Exists(current))
            {
                FileAttributes attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException($"reparse point is not allowed in path: {current}");
                }
            }

            string? parent = Path.GetDirectoryName(current);
            if (parent is null || parent.Equals(current, StringComparison.Ordinal)) return;
            current = parent;
        }
    }

    private static void ValidateKnownColumnFamilies(string dbPath)
    {
        using DbOptions options = new();
        options.SetCreateIfMissing(false);
        options.SetCreateMissingColumnFamilies(false);

        IntPtr list = IntPtr.Zero;
        IntPtr error = IntPtr.Zero;
        nuint count = 0;
        try
        {
            list = RocksDbNative.ListColumnFamilies(
                GetNativeHandle(options),
                dbPath,
                out count,
                out error);
            if (error != IntPtr.Zero)
            {
                string message = Marshal.PtrToStringUTF8(error) ?? "unknown RocksDB error";
                RocksDbNative.Free(error);
                error = IntPtr.Zero;
                throw new InvalidOperationException($"RocksDB could not list column families: {message}");
            }

            if (list == IntPtr.Zero || count == 0)
            {
                throw new InvalidOperationException("RocksDB returned no column families");
            }

            HashSet<string> names = new(StringComparer.Ordinal);
            for (nuint index = 0; index < count; index++)
            {
                IntPtr name = Marshal.ReadIntPtr(list, checked((int)index * IntPtr.Size));
                string? value = Marshal.PtrToStringUTF8(name);
                if (!string.IsNullOrEmpty(value)) names.Add(value);
            }

            foreach (FlatDbColumns column in FlatColumns)
            {
                if (!names.Contains(column.ToString()))
                {
                    throw new InvalidOperationException($"RocksDB is missing Flat column family '{column}'");
                }
            }
        }
        finally
        {
            if (list != IntPtr.Zero) RocksDbNative.DestroyList(list, count);
            if (error != IntPtr.Zero) RocksDbNative.Free(error);
        }
    }

    private static IntPtr GetNativeHandle(DbOptions options)
    {
        FieldInfo handleField = typeof(NativeOptions).GetField("_handle", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(NativeOptions).FullName, "_handle");
        SafeHandle handle = (SafeHandle)(handleField.GetValue(options)
            ?? throw new InvalidOperationException("RocksDB options handle is unavailable"));
        return handle.DangerousGetHandle();
    }

    private static class RocksDbNative
    {
        [DllImport("rocksdb", CallingConvention = CallingConvention.Cdecl, EntryPoint = "rocksdb_list_column_families")]
        internal static extern IntPtr ListColumnFamilies(
            IntPtr options,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            out nuint count,
            out IntPtr error);

        [DllImport("rocksdb", CallingConvention = CallingConvention.Cdecl, EntryPoint = "rocksdb_list_column_families_destroy")]
        internal static extern void DestroyList(IntPtr list, nuint count);

        [DllImport("rocksdb", CallingConvention = CallingConvention.Cdecl, EntryPoint = "rocksdb_free")]
        internal static extern void Free(IntPtr value);
    }

    internal sealed class FixedHardwareInfo : IHardwareInfo
    {
        public long AvailableMemoryBytes => 8L * 1024 * 1024 * 1024;
        public int? MaxOpenFilesLimit => null;
    }

    private sealed record ValidatedPaths(string ScratchRoot, string DbPath, string Output, string ManifestName);
}

internal static class SelfTest
{
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "account-index-prepare-self-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, ".account-index-prepare-owned"), "createdbyharness\n", Encoding.UTF8);
            string parseDb = Path.Combine(root, "mainnet", "flat");
            string parseOutput = Path.Combine(root, "parse.json");
            string[] validPrefix = [
                "--db-path", parseDb,
                "--scratch-root", root,
                "--mode", "auto",
                "--output", parseOutput,
            ];
            AssertThrows(() => PrepareArguments.Parse([.. validPrefix, "--unknown", "x", "--threshold", "0.2"]), "unknown option");
            AssertThrows(() => PrepareArguments.Parse([.. validPrefix, "--threshold", "-1"]), "auto threshold outside range");
            AssertThrows(() => PrepareArguments.Parse([
                "--db-path", parseDb,
                "--scratch-root", root,
                "--mode", "binary",
                "--threshold", "0.2",
                "--output", parseOutput,
            ]), "binary threshold other than -1");

            string outside = Path.Combine(Path.GetTempPath(), "account-index-prepare-outside-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outside);
            try
            {
                string outsideDb = Path.Combine(outside, "db");
                Directory.CreateDirectory(outsideDb);
                AssertThrows(() => AccountIndexPreparer.Prepare(new PrepareArguments(
                    outsideDb, root, AccountIndexMode.Auto, 0.2, Path.Combine(root, "rejected.json"))),
                    "outside database path");
            }
            finally
            {
                Directory.Delete(outside, recursive: true);
            }

            string dbPath = Path.Combine(root, "mainnet", "flat");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            DbConfig config = new()
            {
                AdditionalRocksDbOptions = "disable_auto_compactions=true;",
                FlatAccountDbAdditionalRocksDbOptions =
                    "block_based_table_factory.index_block_search_type=kBinary;" +
                    "block_based_table_factory.uniform_cv_threshold=-1;",
            };
            RocksDbConfigFactory factory = new(config, new PruningConfig(), new AccountIndexPreparer.FixedHardwareInfo(), LimboLogs.Instance, validateConfig: false);
            using (ColumnsDb<FlatDbColumns> columns = new(
                       root,
                       new DbSettings(DbNames.Flat, dbPath),
                       config,
                       factory,
                       LimboLogs.Instance,
                       Enum.GetValues<FlatDbColumns>()))
            {
                IDb account = columns.GetColumnDb(FlatDbColumns.Account);
                for (int i = 0; i < 4096; i++)
                {
                    byte[] key = new byte[20];
                    BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(16), i);
                    byte[] value = new byte[96];
                    BinaryPrimitives.WriteInt32BigEndian(value, i);
                    account.PutSpan(key, value, WriteFlags.DisableWAL);
                }

                columns.Flush();
            }

            string output = Path.Combine(root, "report.json");
            AccountIndexPreparer.Prepare(new PrepareArguments(dbPath, root, AccountIndexMode.Auto, 0.2, output));
            using JsonDocument report = JsonDocument.Parse(File.ReadAllText(output));
            JsonElement content = report.RootElement.GetProperty("Content");
            if (!content.GetProperty("Unchanged").GetBoolean()) throw new InvalidOperationException("self-test content check failed");
            if (!report.RootElement.GetProperty("Ssts").GetProperty("Rewritten").GetBoolean()) throw new InvalidOperationException("self-test rewrite check failed");
            JsonElement afterTables = report.RootElement.GetProperty("Tables").GetProperty("After");
            if (content.GetProperty("After").GetProperty("Count").GetInt64() <= 0
                || afterTables.GetProperty("Entries").GetInt64() <= 0
                || afterTables.GetProperty("IndexBytes").GetInt64() <= 0
                || afterTables.GetProperty("FilterBytes").GetInt64() <= 0
                || afterTables.GetProperty("EstimateTableReadersMemory").GetInt64() <= 0
                || afterTables.GetProperty("UniformBlocks").GetInt64() <= 0)
            {
                throw new InvalidOperationException("self-test native table metrics check failed");
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertThrows(Action action, string description)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            return;
        }

        throw new InvalidOperationException($"self-test expected rejection: {description}");
    }
}

internal sealed record PrepareReport(
    int SchemaVersion,
    string DbPath,
    string ScratchRoot,
    string Mode,
    double Threshold,
    ResolvedOptions Options,
    ContentReport Content,
    TableReport Tables,
    SstReport Ssts,
    TimingReport Timing,
    DbLayoutManifest Layout);

internal sealed record ResolvedOptions(string AccountIndexSearchType, string UniformCvThreshold, bool AutomaticCompactionsDisabled);
internal sealed record AccountContent(long Count, string Sha256);
internal sealed record ContentReport(AccountContent Before, AccountContent After, bool Unchanged);
internal sealed record TableProperties(
    long UniformBlocks,
    long IndexBytes,
    long FilterBytes,
    long EstimateTableReadersMemory,
    long Entries);
internal sealed record TableReport(TableProperties Before, TableProperties After);
internal sealed record AccountSstIdentity(ulong Number, long SizeBytes);
internal sealed record SstReport(
    IReadOnlyList<AccountSstIdentity> Before,
    IReadOnlyList<AccountSstIdentity> After,
    bool Rewritten);
internal sealed record TimingReport(double WallMilliseconds, double CpuMilliseconds, long WorkingSetBytesBefore, long WorkingSetBytesAfter, long PeakWorkingSetBytes, long PrivateBytesBefore, long PrivateBytesAfter);
internal sealed record DbLayoutManifest(string Current, string Manifest, IReadOnlyList<string> Files, IReadOnlyList<string> FlatColumnFamilies);

internal sealed record ProcessSnapshot(TimeSpan CpuTime, long WorkingSetBytes, long PeakWorkingSetBytes, long PrivateBytes)
{
    internal static ProcessSnapshot Capture(Process process)
    {
        process.Refresh();
        return new ProcessSnapshot(
            process.TotalProcessorTime,
            process.WorkingSet64,
            process.PeakWorkingSet64,
            process.PrivateMemorySize64);
    }
}
