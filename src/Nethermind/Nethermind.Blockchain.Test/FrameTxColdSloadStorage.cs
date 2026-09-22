// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Container;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Init.Modules;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State.Flat;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

/// <summary>
/// The EIP-8141 validation prefix that spends its whole declared budget on scattered cold <c>SLOAD</c>s,
/// shared by the admission and the block-production storage harnesses.
/// </summary>
internal static class ColdSloadPrefix
{
    /// <summary>
    /// Gas charged by one loop iteration of <see cref="Code"/>: JUMPDEST 1, DUP2 3, DUP2 3, ADD 3,
    /// PUSH32 3, MUL 5, cold SLOAD 2100 (EIP-2929), POP 2, PUSH1 3, ADD 3, PUSH1 3, JUMP 8.
    /// </summary>
    public const ulong IterationGas = 2_137;

    /// <summary>Gas charged by the loop preamble: PUSH1, CALLDATALOAD, PUSH1.</summary>
    public const ulong PreambleGas = 9;

    /// <summary>
    /// Odd 256-bit multiplier applied to the slot ordinal so consecutive ordinals land on unrelated trie
    /// paths. An attacker picks scattered slots; sequential ones would share trie nodes and database blocks
    /// and would understate the cost.
    /// </summary>
    public static readonly UInt256 ScatterMultiplier = UInt256.Parse(
        "71665204434143205566569301886907364200571850016233087600329496308237151723029");

    /// <summary>Non-zero value written to every seeded slot; a zero slot is not stored and is not read back.</summary>
    public static readonly UInt256 SeededValue = 1;

    /// <summary>Storage reads a prefix declaring <paramref name="ceiling"/> gas can pay for.</summary>
    public static int SlotsPerPrefix(ulong ceiling) => (int)((ceiling - PreambleGas) / IterationGas);

    /// <summary>Maps a slot ordinal to the scattered index the prefix computes on-chain.</summary>
    public static UInt256 SlotOf(int ordinal) => ScatterMultiplier * (UInt256)ordinal;

    /// <summary>
    /// Reads the salt from calldata and then walks <c>SLOAD</c>s at <c>(salt + i) * ScatterMultiplier</c>
    /// until the frame budget is exhausted.
    /// </summary>
    /// <remarks>The loop never terminates on its own; it runs out of gas, which is how the other
    /// budget-burning shapes fail too.</remarks>
    public static byte[] Code()
    {
        byte[] preamble = Prepare.EvmCode
            .PushData(0)
            .Op(Instruction.CALLDATALOAD)
            .PushData(0)
            .Done;

        byte[] body = Prepare.EvmCode
            .Op(Instruction.JUMPDEST)
            .Op(Instruction.DUP2)
            .Op(Instruction.DUP2)
            .Op(Instruction.ADD)
            .PushData(ScatterMultiplier)
            .Op(Instruction.MUL)
            .Op(Instruction.SLOAD)
            .Op(Instruction.POP)
            .PushData(1)
            .Op(Instruction.ADD)
            .PushData(preamble.Length)
            .Op(Instruction.JUMP)
            .Done;

        return [.. preamble, .. body];
    }
}

/// <summary>Counts completed storage reads in the outermost validation frame.</summary>
/// <remarks>An operation only counts once a later one starts, so the trailing <c>SLOAD</c> that the
/// budget cannot pay for is charged gas, aborts before any read, and is not counted.</remarks>
internal sealed class SloadCountingTracer : TxTracer
{
    private int _depth;
    private bool _sloadPending;

    public SloadCountingTracer() { IsTracingActions = true; IsTracingInstructions = true; }

    public int Sloads { get; private set; }

    public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
    {
        if (_depth != 1) return;
        if (_sloadPending) Sloads++;
        _sloadPending = opcode == Instruction.SLOAD;
    }

    public override void ReportAction(ulong gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false) => _depth++;

    public override void ReportActionEnd(ulong gas, ReadOnlyMemory<byte> output) => Leave(completed: true);

    public override void ReportActionEnd(ulong gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode) => Leave(completed: true);

    public override void ReportActionRevert(ulong gas, ReadOnlyMemory<byte> output) => Leave(completed: true);

    public override void ReportActionError(EvmExceptionType evmExceptionType) => Leave(completed: false);

    private void Leave(bool completed)
    {
        if (_depth == 1)
        {
            if (_sloadPending && completed) Sloads++;
            _sloadPending = false;
        }
        _depth--;
    }
}

/// <summary>
/// Page-cache and device-read accounting, so a row that silently measured a warm cache is visible as such
/// rather than being mistaken for a device read.
/// </summary>
internal static class StorageResidency
{
    private const int PosixFadvDontNeed = 4;

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_fadvise(int fd, long offset, long len, int advice);

    /// <summary>Writes back every dirty page in the system. <c>posix_fadvise</c> cannot evict a dirty page,
    /// and this is the only unprivileged way to make the database's pages clean.</summary>
    [DllImport("libc", SetLastError = true)]
    private static extern void sync();

    public static void SyncAll() => sync();

    /// <summary>
    /// Bytes this process caused to be fetched from the storage layer, which is what separates a device read
    /// from a page-cache hit. Page-cache hits do not move it.
    /// </summary>
    public static long ProcessReadBytes()
    {
        foreach (string line in File.ReadLines("/proc/self/io"))
        {
            if (line.StartsWith("read_bytes:", StringComparison.Ordinal))
            {
                return long.Parse(line["read_bytes:".Length..].Trim());
            }
        }

        return -1;
    }

    /// <summary>
    /// Asks the kernel to drop the directory's clean pages. This is the only page-cache control available
    /// without root: <c>/proc/sys/vm/drop_caches</c> needs it and these harnesses must not.
    /// </summary>
    /// <remarks>Dirty pages survive, which is why the caller flushes first.</remarks>
    public static int DropPageCache(string directory)
    {
        int evicted = 0;
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                using Microsoft.Win32.SafeHandles.SafeFileHandle handle = File.OpenHandle(file);
                if (posix_fadvise((int)handle.DangerousGetHandle(), 0, 0, PosixFadvDontNeed) == 0) evicted++;
            }
            catch (IOException)
            {
                // A file RocksDB deleted between the enumeration and the open is not one whose pages matter.
            }
        }

        return evicted;
    }

    /// <summary>
    /// The file system the database sits on, so a row can be read for what it is. A harness whose temp
    /// directory is <c>tmpfs</c> has no block device under it at all, and no rung it reports can reach one
    /// however the caches are arranged. Set <c>TMPDIR</c> to a disk-backed directory to change that.
    /// </summary>
    /// <remarks>The deepest mount point <paramref name="path"/> sits under wins, so a bind mount inside the
    /// temp directory is reported rather than the file system it was carved out of.</remarks>
    public static string FileSystemOf(string path)
    {
        string best = string.Empty;
        string type = "unknown";

        try
        {
            foreach (string line in File.ReadLines("/proc/self/mountinfo"))
            {
                int separator = line.IndexOf(" - ", StringComparison.Ordinal);
                if (separator < 0) continue;

                string[] head = line[..separator].Split(' ');
                string[] tail = line[(separator + 3)..].Split(' ');
                if (head.Length < 5 || tail.Length < 1) continue;

                string mountPoint = Unescape(head[4]);
                if (!IsUnder(path, mountPoint) || mountPoint.Length < best.Length) continue;

                best = mountPoint;
                type = Unescape(tail[0]);
            }
        }
        catch (IOException)
        {
            return "unknown";
        }

        return type;
    }

    /// <summary>Whether <paramref name="path"/> lies in <paramref name="mountPoint"/>'s subtree, comparing
    /// whole path components so that <c>/mnt/sda</c> does not claim a path under <c>/mnt/sda2</c>.</summary>
    private static bool IsUnder(string path, string mountPoint) =>
        path.StartsWith(mountPoint, StringComparison.Ordinal)
        && (mountPoint.Length == path.Length || mountPoint[^1] == '/' || path[mountPoint.Length] == '/');

    /// <summary>
    /// Decodes the octal escapes the kernel writes into <c>mountinfo</c>'s space-separated fields.
    /// </summary>
    /// <remarks>Only space, tab, newline and backslash are escaped (<c>fs/proc_namespace.c</c>), but any
    /// three-octal-digit sequence decodes the same way, so the general form costs nothing extra. Leaving
    /// them encoded makes a mount point containing one of the four fail to match any real path.</remarks>
    private static string Unescape(string field)
    {
        if (!field.Contains('\\')) return field;

        StringBuilder decoded = new(field.Length);
        for (int i = 0; i < field.Length; i++)
        {
            if (field[i] == '\\' && i + 3 < field.Length
                && char.IsBetween(field[i + 1], '0', '3')
                && char.IsBetween(field[i + 2], '0', '7')
                && char.IsBetween(field[i + 3], '0', '7'))
            {
                decoded.Append((char)(((field[i + 1] - '0') << 6) + ((field[i + 2] - '0') << 3) + (field[i + 3] - '0')));
                i += 3;
            }
            else
            {
                decoded.Append(field[i]);
            }
        }

        return decoded.ToString();
    }

    /// <summary>Bytes the database occupies on disk, which bounds how much of it the page cache can hold.</summary>
    public static long BytesOnDisk(string directory)
    {
        long total = 0;
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (IOException)
            {
                // Same race as above; a file that vanished contributes nothing.
            }
        }

        return total;
    }
}

/// <summary>
/// One state-backend and cache configuration the storage harnesses sweep, so that a single run compares the
/// adversarial configuration against production defaults instead of comparing two runs taken on two days.
/// </summary>
/// <param name="Name">The label every row of this arm carries.</param>
/// <param name="UseFlatDb">Whether the flat backend, which is the production default, resolves a slot in one
/// keyed read instead of walking the trie.</param>
/// <param name="TrieCacheMb">Total trie-store cache; the persisted-node read cache is this minus
/// <paramref name="DirtyTrieCacheMb"/> (<c>PruningTrieStateFactory</c>), which is the number that decides
/// whether a seeded slot stays resident.</param>
/// <param name="DirtyTrieCacheMb">Dirty share of <paramref name="TrieCacheMb"/>. It has to stay strictly
/// below the total, or <c>PruningTrieStateFactory.AdviseConfig</c> throws.</param>
/// <param name="SharedBlockCacheBytes">RocksDB's shared block cache. A block-cache hit is served inside the
/// process, above the page cache, so <c>posix_fadvise</c> cannot reach it; this is the setting that decides
/// whether a cold rung can exist at all.</param>
/// <param name="ExpectsDeviceCold">Whether a rung this arm labels cold is claimed to reach the device. An arm
/// that claims it is asserted against <c>read_bytes</c>; an arm that does not is exactly what the sweep is
/// testing, so its rows report the collapse rather than failing on it.</param>
public sealed record StorageArm(
    string Name,
    bool UseFlatDb,
    long TrieCacheMb,
    long DirtyTrieCacheMb,
    ulong SharedBlockCacheBytes,
    bool ExpectsDeviceCold)
{
    /// <summary>
    /// The arm every storage figure of this campaign was measured on: the non-default patricia backend with
    /// both caches at their floor.
    /// </summary>
    /// <remarks>Held byte-for-byte at the values CI run <c>35740377693</c> used, so a sweep that includes it
    /// reproduces the published numbers and shows the harness did not drift underneath them.</remarks>
    public static readonly StorageArm Adversarial = new("adversarial", false, 64, 32, 4 * 1024 * 1024, true);

    /// <summary>The same backend with the caches a default node runs.</summary>
    public static readonly StorageArm ProductionCaches =
        new("production-caches", false, 1792, 1536, 256 * 1024 * 1024, false);

    /// <summary>The configuration a default node runs: flat backend, production caches.</summary>
    public static readonly StorageArm Production = new("production", true, 1792, 1536, 256 * 1024 * 1024, false);

    private static readonly StorageArm[] Known = [Adversarial, ProductionCaches, Production];

    /// <summary>Environment variable selecting a comma-separated subset of arms, so one dispatch can carry a
    /// single arm when the whole sweep does not fit the job budget. Unset means every arm.</summary>
    public const string ArmsVariable = "FRAME_STORAGE_ARMS";

    /// <summary>The arms this run sweeps.</summary>
    public static IEnumerable<StorageArm> Swept()
    {
        string? selection = Environment.GetEnvironmentVariable(ArmsVariable);
        foreach (StorageArm arm in Known)
        {
            if (string.IsNullOrWhiteSpace(selection) || Selects(selection, arm.Name)) yield return arm;
        }
    }

    private static bool Selects(string selection, string name)
    {
        foreach (string entry in selection.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(entry, name, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    public override string ToString() => Name;
}

/// <summary>
/// A test chain on a real RocksDB whose state backend and cache sizes come from a swept
/// <see cref="StorageArm"/> rather than from constants.
/// </summary>
/// <remarks>
/// Every other shape in this campaign runs on <c>MemDbFactory</c> (<c>TestEnvironmentModule</c>), where a
/// cold <c>SLOAD</c> is a dictionary lookup. This swaps <see cref="IDbFactory"/> for
/// <see cref="RocksDbFactory"/> over a temp directory, the same override
/// <c>FullPruning.FullPruningDiskTest</c> uses, so a slot read ends in RocksDB reads. On the flat arm the
/// flat column database is re-registered over that same factory, because the test modules otherwise hold it
/// in memory and no arrangement of caches could then reach a device.
///
/// Two deviations from a production node survive every arm and are reported rather than hidden.
/// <c>Pruning.Mode</c> is <c>None</c> (archive), because the seeded state has to be in the database rather
/// than pending in the trie store's dirty set when the first sample reads it; a production node runs Hybrid.
/// <c>IHardwareInfo</c> stays at the test default, so <c>AdviseConfig</c> does not raise
/// <c>CacheMb</c> the way it would on a host with spare memory. Archive mode is what selects RocksDB's
/// State-Db options here, so available memory does not reach them (<c>RocksDbConfigFactory</c>). Rows carry
/// <c>pruning_mode</c> and <c>available_memory_gb</c> so neither has to be taken on trust.
/// </remarks>
internal sealed class ColdSloadTestBlockchain : BasicTestBlockchain
{
    public const long BlockGasLimit = 30_000_000;

    private string _dbPath = null!;
    private ulong _verifyGasCeiling;
    private StorageArm _arm = null!;

    public static async Task<ColdSloadTestBlockchain> CreateColdSload(
        string dbPath, ulong verifyGasCeiling, StorageArm arm, Action<ContainerBuilder>? configurer = null)
    {
        ColdSloadTestBlockchain chain = new()
        {
            _dbPath = dbPath,
            _verifyGasCeiling = verifyGasCeiling,
            _arm = arm,
            UseFlatDb = arm.UseFlatDb,
        };
        await chain.Build(configurer);
        return chain;
    }

    protected override IEnumerable<IConfig> CreateConfigs() =>
    [
        new BlocksConfig { MinGasPrice = 0 },
        new TxPoolConfig
        {
            GasLimit = BlockGasLimit,
            // The per-head budget sheds admission after a second of simulation against one head; these
            // harnesses time work against a fixed head, so leaving it on would measure the shed path
            // rather than the prefix.
            FrameTxSimulationBudgetPerHeadMs = int.MaxValue,
            // Without this the declared-budget filter rejects a prefix above its 300,000 default before the
            // simulator ever runs, and the swept ceiling would never be measured.
            FrameTxMaxVerifyGas = _verifyGasCeiling,
        },
        // Archive mode persists every block, so the seeded state is in the database rather than pending
        // in the trie store's dirty set when the first sample reads it. The trie cache sizes cannot be set
        // here; see ConfigureContainer.
        new PruningConfig
        {
            Mode = PruningMode.None,
            PersistenceInterval = 1,
        },
        new DbConfig { SharedBlockCacheSize = _arm.SharedBlockCacheBytes },
    ];

    /// <summary>The state backend the container resolved, as the <c>storage_backend</c> row value.</summary>
    public string StorageBackend => Container.Resolve<IFlatDbConfig>().Enabled ? "flat" : "rocksdb_trie";

    /// <summary>
    /// The trie-store cache sizes the container resolved, as result-row fields.
    /// </summary>
    /// <remarks>
    /// What <see cref="CreateConfigs"/> asks for is not what a chain runs on, and neither is what
    /// <see cref="ConfigureContainer"/> asks for: <c>PruningTrieStateFactory.AdviseConfig</c> may raise
    /// <c>CacheMb</c> again on a host with spare memory. A whole campaign of rows once claimed 64 MB while
    /// running on 8, so rows report what was resolved rather than what was requested.
    /// </remarks>
    public string TrieCacheFields
    {
        get
        {
            IPruningConfig pruningConfig = Container.Resolve<IPruningConfig>();
            return $"trie_cache_mb={pruningConfig.CacheMb} trie_dirty_cache_mb={pruningConfig.DirtyCacheMb}";
        }
    }

    /// <summary>
    /// The arm label and every other resolved setting that decides residency, as result-row fields that
    /// follow the fields older rows already carried.
    /// </summary>
    /// <remarks>The RocksDB block cache is the setting that decides whether a cold rung can exist at all, and
    /// no row reported it before the arms were swept.</remarks>
    public string ResolvedConfigFields
    {
        get
        {
            IPruningConfig pruningConfig = Container.Resolve<IPruningConfig>();
            IDbConfig dbConfig = Container.Resolve<IDbConfig>();
            IFlatDbConfig flatDbConfig = Container.Resolve<IFlatDbConfig>();
            IHardwareInfo hardwareInfo = Container.Resolve<IHardwareInfo>();

            string flatFields = flatDbConfig.Enabled
                ? $"flat_block_cache_mb={flatDbConfig.BlockCacheSizeBudget / MegaByte} "
                  + $"flat_trie_cache_mb={flatDbConfig.TrieCacheMemoryBudget / MegaByte} "
                  + $"flat_trie_warmer_workers={flatDbConfig.TrieWarmerWorkerCount} "
                : string.Empty;

            return $"arm={_arm.Name} db_shared_block_cache_mb={dbConfig.SharedBlockCacheSize / MegaByte} "
                   + flatFields
                   + $"pruning_mode={pruningConfig.Mode} "
                   + $"available_memory_gb={hardwareInfo.AvailableMemoryBytes / (1024 * 1024 * 1024d):F1}";
        }
    }

    /// <summary>The database's size on disk and the file system under it, as result-row fields.</summary>
    public string DbFileFields =>
        $"db_bytes_on_disk={StorageResidency.BytesOnDisk(_dbPath)} db_fs={StorageResidency.FileSystemOf(_dbPath)}";

    private const ulong MegaByte = 1024 * 1024;

    /// <summary>
    /// Pushes seeded state out of memory and into the database files, whichever backend holds it, so that the
    /// page cache rather than the process is what a warm rung is served from.
    /// </summary>
    /// <remarks>The flat backend keeps recent blocks in memory snapshots and persists them on its own
    /// schedule, so flushing its column database alone would leave the seeded state unreachable by any rung.
    /// The trie backend in archive mode has already written every node, and flushing only its memtables keeps
    /// the <see cref="StorageArm.Adversarial"/> arm identical to the runs it reproduces.</remarks>
    public void PersistState()
    {
        if (_arm.UseFlatDb)
        {
            WorldStateManager.FlushCache(CancellationToken.None);
            Container.Resolve<IColumnsDb<FlatDbColumns>>().Flush();
        }
        else
        {
            DbProvider.StateDb.Flush();
        }

        DbProvider.CodeDb.Flush();
    }

    protected override ContainerBuilder ConfigureContainer(ContainerBuilder builder, IConfigProvider configProvider)
    {
        builder = base.ConfigureContainer(builder, configProvider)
            .AddSingleton<IDbFactory, RocksDbFactory>()
            .Intercept<IInitConfig>(initConfig => initConfig.BaseDbPath = _dbPath)
            // TestEnvironmentModule holds every test chain's trie cache at 8 MB, far below the seeded state
            // the all-warm rung is meant to keep resident, and the base call above is what registers it.
            // Autofac applies decorators in registration order, so re-applying the sizes here is what makes
            // them the resolved ones; rows carry the resolved pair so a run cannot claim one and use another.
            .Intercept<IPruningConfig>(pruningConfig =>
            {
                pruningConfig.CacheMb = _arm.TrieCacheMb;
                pruningConfig.DirtyCacheMb = _arm.DirtyTrieCacheMb;
            });

        // PseudoNethermindModule swaps the flat column database for an in-memory one, which would make every
        // flat rung memory-resident whatever the caches say. This is the production registration.
        return _arm.UseFlatDb ? builder.AddColumnDatabase<FlatDbColumns>(DbNames.Flat) : builder;
    }
}

/// <summary>
/// Repetition and across-repeat spread, shared by the storage harnesses.
/// </summary>
/// <remarks>Every storage figure this campaign has published came from a single run of a single cell, so a
/// reader had no way to tell a real difference between two rungs from the noise of one machine.</remarks>
internal static class StorageRepetition
{
    /// <summary>Environment variable overriding the repeat count, so a local check can be cheap while CI
    /// keeps the full count.</summary>
    public const string RepeatsVariable = "FRAME_STORAGE_REPEATS";

    /// <summary>Repeats per cell when the variable is unset.</summary>
    private const int DefaultRepeats = 5;

    public static int Repeats =>
        int.TryParse(Environment.GetEnvironmentVariable(RepeatsVariable), out int repeats) && repeats > 0
            ? repeats
            : DefaultRepeats;

    /// <summary>Median, extremes and coefficient of variation across repeats, as row fields.</summary>
    public static string SpreadFields(string prefix, IReadOnlyList<double> values)
    {
        double median = Median(values);
        double min = double.MaxValue;
        double max = double.MinValue;
        double sum = 0;
        foreach (double value in values)
        {
            if (value < min) min = value;
            if (value > max) max = value;
            sum += value;
        }

        double mean = sum / values.Count;
        double variance = 0;
        foreach (double value in values) variance += (value - mean) * (value - mean);
        double deviation = values.Count > 1 ? Math.Sqrt(variance / (values.Count - 1)) : 0;

        return $"{prefix}_n={values.Count} {prefix}_median={median:F3} {prefix}_min={min:F3} "
               + $"{prefix}_max={max:F3} {prefix}_cv_pct={(mean > 0 ? deviation * 100 / mean : 0):F2}";
    }

    /// <summary>Nearest-rank median, so the reported value is one that was observed.</summary>
    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return double.NaN;
        List<double> sorted = [.. values];
        sorted.Sort();
        return sorted[(int)Math.Ceiling(0.5 * sorted.Count) - 1];
    }
}

/// <summary>
/// Whether a rung a harness labels cold actually reached the device, and whether the ladder it belongs to
/// still exists.
/// </summary>
/// <remarks>
/// The campaign measured its storage ladder with a 4 MiB RocksDB block cache against a 164 MB database. A
/// production node's 256 MiB block cache sits above the page cache and inside this process, so
/// <c>posix_fadvise</c> cannot evict it and a rung that claims to be cold may read nothing at all. That
/// outcome is a result, not a failure, so it is reported on the row rather than thrown.
/// </remarks>
internal static class ColdRung
{
    /// <summary>A cold rung this much slower than the warm one is still a ladder; anything below it has
    /// collapsed into the warm rung and the ladder no longer separates anything.</summary>
    private const double AliveFactor = 1.5;

    /// <summary>Whether the timed window pulled bytes from the storage layer at all.</summary>
    public static bool ReachedDevice(long readBytesPerSample, string fileSystem) =>
        readBytesPerSample > 0 && fileSystem != "tmpfs";

    /// <summary>The verdict fields a cold-labelled row carries, so a collapse is visible without arithmetic
    /// on the reader's side.</summary>
    public static string Fields(long readBytesPerSample, string fileSystem, double coldOverWarm)
    {
        bool reachedDevice = ReachedDevice(readBytesPerSample, fileSystem);
        bool alive = reachedDevice && coldOverWarm >= AliveFactor;
        return $"device_cold={(reachedDevice ? "yes" : "no")} cold_over_warm={coldOverWarm:F3} "
               + $"cold_rung={(alive ? "alive" : "COLLAPSED")}";
    }

    /// <summary>
    /// Fails an arm that claims its cold rung reaches the device when the rung read nothing, which is the
    /// check that would have caught a whole campaign measured on <c>tmpfs</c>.
    /// </summary>
    public static void AssertReachedDevice(StorageArm arm, string rung, long readBytesPerSample, string fileSystem)
    {
        if (!arm.ExpectsDeviceCold) return;

        Assert.That(ReachedDevice(readBytesPerSample, fileSystem), Is.True,
            $"the {rung} rung of the {arm.Name} arm pulled {readBytesPerSample} bytes per sample from a "
            + $"{fileSystem} file system, so it did not reach a device and its cost is a memory-resident "
            + "lower bound wearing a cold label");
    }
}

/// <summary>Seeds and validates the RocksDB-backed chain both cold-<c>SLOAD</c> harnesses measure against.</summary>
internal static class ColdSloadStorageFixture
{
    public static void SkipUnlessLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("the cold rungs need posix_fadvise and /proc/self/io, so the residency these "
                          + "harnesses report can only be established on Linux");
        }
    }

    /// <summary>Builds a chain whose attacker account carries the prefix code and <paramref name="slotsToSeed"/>
    /// scattered non-zero slots.</summary>
    public static async Task<ColdSloadTestBlockchain> BuildChain(
        string dbPath, ulong verifyGasCeiling, StorageArm arm, Address attacker, UInt256 attackerBalance,
        int slotsToSeed, IReadOnlyList<(Address Address, string Shape)>? otherShapes = null)
    {
        byte[] attackCode = ColdSloadPrefix.Code();

        ColdSloadTestBlockchain chain = await ColdSloadTestBlockchain.CreateColdSload(dbPath, verifyGasCeiling, arm, builder =>
        {
            builder.AddSingleton<ISpecProvider>(new TestSpecProvider(Eip8141Prototype.Instance));
            builder.AddScoped<IGenesisPostProcessor, IWorldState, ISpecProvider>((worldState, specProvider) =>
                new FunctionalGenesisPostProcessor(_ =>
                {
                    worldState.CreateAccount(attacker, attackerBalance);
                    worldState.InsertCode(attacker, attackCode, specProvider.GenesisSpec);
                    foreach ((Address address, string shape) in otherShapes ?? [])
                    {
                        worldState.CreateAccount(address, attackerBalance);
                        worldState.InsertCode(address, FrameTxPrefixShapes.Code(shape), specProvider.GenesisSpec);
                    }
                    for (int i = 0; i < slotsToSeed; i++)
                    {
                        worldState.Set(new StorageCell(attacker, ColdSloadPrefix.SlotOf(i)), ColdSloadPrefix.SeededValue);
                    }
                    worldState.Commit(specProvider.GenesisSpec);
                    worldState.RecalculateStateRoot();
                }));
        });

        // The pool refuses everything while the tree reports a zero best-suggested block.
        await chain.AddBlock();
        return chain;
    }

    public static void AssertSeededSlotIsVisible(ColdSloadTestBlockchain chain, Address attacker, int slotsSeeded)
    {
        BlockHeader head = chain.BlockTree.Head!.Header;
        using IReadOnlyTxProcessorSource source = chain.ReadOnlyTxProcessingEnvFactory.Create();
        using IReadOnlyTxProcessingScope scope = source.Build(head);

        scope.WorldState.Get(new StorageCell(attacker, ColdSloadPrefix.SlotOf(slotsSeeded - 1)), out UInt256 last);
        Assert.That(last, Is.EqualTo(ColdSloadPrefix.SeededValue),
            "the last seeded slot is not readable at the head, so the prefix would read empty slots and the "
            + "measurement would describe missing-slot handling rather than a storage read");
    }

    /// <summary>
    /// Builds a cold-<c>SLOAD</c> frame transaction whose calldata word selects the slot range the prefix
    /// reads, so each sample can be given slots no earlier sample touched.
    /// </summary>
    public static Transaction FrameTx(Address attacker, ulong ceiling, int slotBase, int uniqueSalt) =>
        FrameTxPrefixShapes.FrameTx(attacker, "sload-cold", ceiling, slotBase, uniqueSalt);
}
