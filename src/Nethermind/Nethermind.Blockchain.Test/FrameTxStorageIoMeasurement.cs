// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

/// <summary>
/// Measures mempool rejection latency for an EIP-8141 validation prefix that spends its whole budget on
/// cold <c>SLOAD</c>s, against a real RocksDB-backed state database rather than the in-memory one every
/// other frame-tx harness uses.
/// </summary>
/// <remarks>
/// Every other shape in this campaign runs on <c>MemDbFactory</c> (<c>TestEnvironmentModule</c>), where a
/// cold <c>SLOAD</c> is a dictionary lookup. This fixture swaps <see cref="IDbFactory"/> for
/// <see cref="RocksDbFactory"/> over a temp directory, the same override
/// <c>FullPruning.FullPruningDiskTest</c> uses, so a slot read is a patricia-trie walk ending in RocksDB
/// reads.
///
/// A single number would be meaningless here, because the answer depends entirely on where the slot is
/// resident. Each ceiling therefore emits one row per rung of a cache-residency ladder; the rungs are
/// described on <see cref="Rungs"/>. Every row carries the bytes this process actually pulled from the
/// storage layer during its timed window (<c>read_bytes</c> from <c>/proc/self/io</c>), so a row that
/// silently measured a warm cache is visible as such rather than being mistaken for a device read.
///
/// Results are appended as <c>RESULT key=value</c> lines to <c>FRAME_STORAGE_IO_OUT</c>, or
/// <c>frame-tx-storage-io.txt</c> in the temp directory. The <c>case=frame_reject</c> rows carry the same
/// keys as <c>FrameTxMempoolDosMeasurement</c>'s, so existing parsing keeps working; the ladder-specific
/// fields are additions.
///
/// Run under <c>taskset -c 0</c>, like the rest of the campaign.
/// </remarks>
[TestFixture]
[Explicit("measurement harness")]
[NonParallelizable]
public class FrameTxStorageIoMeasurement
{
    private const long BlockGasLimit = 30_000_000;

    /// <summary>Untimed submissions used to warm the interpreter and the JIT.</summary>
    private const int Warmup = 40;

    /// <summary>
    /// Timed submissions per rung. Far below the CPU harnesses' 1,000 because every cold sample needs its
    /// own never-read slot range, and the seeded slot count is the fixture's dominant cost.
    /// </summary>
    private const int Samples = 200;

    /// <summary>
    /// Gas charged by one loop iteration of <see cref="ColdSloadLoop"/>: JUMPDEST 1, DUP2 3, DUP2 3, ADD 3,
    /// PUSH32 3, MUL 5, cold SLOAD 2100 (EIP-2929), POP 2, PUSH1 3, ADD 3, PUSH1 3, JUMP 8.
    /// </summary>
    private const ulong IterationGas = 2_137;

    /// <summary>Gas charged by the loop preamble: PUSH1, CALLDATALOAD, PUSH1.</summary>
    private const ulong PreambleGas = 9;

    /// <summary>
    /// Odd 256-bit multiplier applied to the slot ordinal so consecutive ordinals land on unrelated trie
    /// paths. An attacker picks scattered slots; sequential ones would share trie nodes and database blocks
    /// and would understate the cost.
    /// </summary>
    private static readonly UInt256 ScatterMultiplier = UInt256.Parse(
        "71665204434143205566569301886907364200571850016233087600329496308237151723029");

    /// <summary>Non-zero value written to every seeded slot; a zero slot is not stored and is not read back.</summary>
    private static readonly UInt256 SeededValue = 1;

    private static readonly Address Attacker = TestItem.AddressF;
    private static readonly UInt256 AttackerBalance = 1_000.Ether;

    /// <summary>
    /// Shared RocksDB block cache, sized so that the seeded state cannot sit in it. The ladder varies
    /// residency deliberately; leaving the production-sized cache in place would pin the whole fixture in
    /// process memory and every rung would read the same number.
    /// </summary>
    private const ulong MinimalSharedBlockCacheSize = 4 * 1024 * 1024;

    /// <summary>
    /// Trie-store cache size. Large enough that a slot range read once stays resident, which is what makes
    /// the <c>all-warm</c> rung warm, and far too small to hold the whole seeded fixture, which is what
    /// keeps the cold rungs cold.
    /// </summary>
    private const long TrieCacheMb = 64;

    /// <summary>
    /// Ceilings that fit the compiled <see cref="Eip8141Constants.MaxVerifyGas"/>. 322,800 is above it, so
    /// an EVM-executing shape cannot reach it without a source edit; the ranked CPU shapes have the same
    /// limit and the campaign extrapolates there the same way.
    /// </summary>
    private static readonly ulong[] SweptCeilings = [100_000ul, 236_285ul, 300_000ul];

    /// <summary>
    /// The cache-residency ladder, coldest last. Each rung names what is cold at the moment a slot is read
    /// for the first time in a timed sample.
    /// </summary>
    /// <remarks>
    /// <c>all-warm</c> replays one slot range for every sample, so every read after the first is served by
    /// the trie store's node cache. It is the in-memory floor and the closest thing here to what the
    /// MemDb-backed harnesses measure.
    ///
    /// <c>cold-node-cache</c> gives each sample its own never-read slot range, so no Nethermind-level cache
    /// can hold it, but the pages were written by this same process moments earlier and the operating
    /// system still has them. This is the page-cache-hit rung.
    ///
    /// <c>cold-page-cache</c> is the same, with <c>posix_fadvise(POSIX_FADV_DONTNEED)</c> applied over the
    /// database directory before each timed sample, so the read reaches the device. Linux only.
    /// </remarks>
    private static readonly string[] Rungs = ["all-warm", "cold-node-cache", "cold-page-cache"];

    private const int PosixFadvDontNeed = 4;

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_fadvise(int fd, long offset, long len, int advice);

    /// <summary>Writes back every dirty page in the system. <c>posix_fadvise</c> cannot evict a dirty page,
    /// and this is the only unprivileged way to make the database's pages clean.</summary>
    [DllImport("libc", SetLastError = true)]
    private static extern void sync();

    private StorageIoTestBlockchain _chain = null!;
    private TempPath _dbDirectory = null!;
    private ulong _ceiling;
    private int _slotsPerTx;

    /// <summary>Slot ordinals reserved per sample, one above the reads a prefix completes so that the
    /// out-of-gas read at the end of one sample cannot warm the next sample's first slot.</summary>
    private int _saltStride;

    /// <summary>Next unused slot range. Rungs draw from one rising sequence so a later rung can never be
    /// served by a range an earlier one already read.</summary>
    private int _nextSalt;

    [SetUp]
    public void Setup()
    {
        _chain = null!;
        _dbDirectory = null!;
    }

    [TearDown]
    public void TearDown()
    {
        _chain?.Dispose();
        _dbDirectory?.Dispose();
    }

    private static IEnumerable<TestCaseData> CeilingCases()
    {
        foreach (ulong ceiling in SweptCeilings) yield return new TestCaseData(ceiling);
    }

    /// <summary>
    /// Measures rejection of a validation prefix that spends its budget on scattered cold storage reads,
    /// once per rung of the cache-residency ladder.
    /// </summary>
    [TestCaseSource(nameof(CeilingCases))]
    public async Task Reject_cost_of_a_cold_sload_prefix(ulong ceiling)
    {
        Eip8141MeasurementGuards.SkipIfCeilingUnreachable(ceiling);
        SkipUnlessLinux();

        _ceiling = ceiling;
        _slotsPerTx = (int)((ceiling - PreambleGas) / IterationGas);
        _saltStride = _slotsPerTx + 1;
        _nextSalt = 1;

        // Every cold sample needs a slot range no earlier sample touched, so the fixture seeds one range per
        // submission of the coldest rungs plus the probe and the warm-up.
        int coldRungs = Rungs.Length - 1;
        int coldSamples = (Warmup + Samples) * coldRungs + Warmup + Samples + 1;
        await BuildChain((coldSamples + 1) * _saltStride);

        int observedSloads = ProbeColdSloadCount();
        Assert.That(observedSloads, Is.EqualTo(_slotsPerTx).Within(1),
            $"the prefix completed {observedSloads} storage reads against the {_slotsPerTx} its gas budget was "
            + "sized for, so the code the EVM ran is not the loop this fixture seeded slots for and the "
            + "per-slot figures would be labelled with the wrong count");

        AssertProbeIsRejectedBySimulation();

        foreach (string rung in Rungs) MeasureRung(rung, observedSloads);
    }

    private void MeasureRung(string rung, int sloadsPerTx)
    {
        bool coldSlots = rung != "all-warm";
        bool dropPageCache = rung == "cold-page-cache";

        // Salt 0 is the probe's; warm-up and timed samples take disjoint ranges above it so no timed sample
        // can be served by a range an earlier one already pulled in.
        int salt = _nextSalt;
        for (int i = 0; i < Warmup; i++, salt++) SubmitFrame(coldSlots ? salt : 0, salt);

        if (dropPageCache)
        {
            FlushState();
            sync();
        }

        long readBytesBefore = ProcessReadBytes();
        int fadvisedFiles = 0;
        List<double> submitMicros = new(Samples);
        for (int i = 0; i < Samples; i++, salt++)
        {
            if (dropPageCache) fadvisedFiles = DropPageCache();

            Transaction tx = FrameTx(coldSlots ? salt : 0, salt);
            long start = Stopwatch.GetTimestamp();
            AcceptTxResult result = _chain.TxPool.SubmitTx(tx, TxHandlingOptions.None);
            submitMicros.Add(Stopwatch.GetElapsedTime(start).TotalMicroseconds);

            if (result != AcceptTxResult.FrameSimulationFailed)
            {
                Assert.Fail($"{rung} sample {i} was not rejected by the simulation stage: {result}");
            }
        }

        _nextSalt = salt;
        long readBytes = ProcessReadBytes() - readBytesBefore;
        submitMicros.Sort();
        double p50 = Percentile(submitMicros, 0.50);

        Emit($"case=frame_reject shape=sload-cold storage_backend=rocksdb_trie rung={rung} "
             + $"verify_gas={_ceiling} frame_gas_available={_ceiling} frame_gas_burned={_ceiling} "
             + $"cold_sloads={sloadsPerTx} samples={Samples} "
             + $"submit_p50_us={p50:F1} "
             + $"submit_p90_us={Percentile(submitMicros, 0.90):F1} "
             + $"submit_p99_us={Percentile(submitMicros, 0.99):F1} "
             + $"submit_max_us={submitMicros[^1]:F1} "
             + $"submit_us_per_Mgas={p50 * 1_000_000 / _ceiling:F1} "
             + $"us_per_kgas={p50 * 1_000 / _ceiling:F3} "
             + $"us_per_sload={p50 / sloadsPerTx:F3} "
             + $"us_per_Mgas_basis=offered "
             + $"read_bytes_total={readBytes} read_bytes_per_sample={readBytes / Samples} "
             + $"db_bytes_on_disk={DatabaseBytesOnDisk()} fadvised_files={fadvisedFiles} "
             + $"reject_reason=\"FrameSimulationFailed\"");
    }

    /// <summary>
    /// Bytes this process caused to be fetched from the storage layer, which is what separates a device read
    /// from a page-cache hit. Page-cache hits do not move it.
    /// </summary>
    private static long ProcessReadBytes()
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

    /// <summary>Pushes the state database's memtables into files so the page cache is what holds them.</summary>
    private void FlushState()
    {
        _chain.DbProvider.StateDb.Flush();
        _chain.DbProvider.CodeDb.Flush();
    }

    /// <summary>
    /// Asks the kernel to drop the database directory's clean pages. This is the only page-cache control
    /// available without root: <c>/proc/sys/vm/drop_caches</c> needs it and this harness must not.
    /// </summary>
    /// <remarks>Dirty pages survive, which is why <see cref="FlushState"/> runs first.</remarks>
    private int DropPageCache()
    {
        int evicted = 0;
        foreach (string file in Directory.EnumerateFiles(_dbDirectory.Path, "*", SearchOption.AllDirectories))
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

    /// <summary>Bytes the database occupies on disk, which bounds how much of it the page cache can hold.</summary>
    private long DatabaseBytesOnDisk()
    {
        long total = 0;
        foreach (string file in Directory.EnumerateFiles(_dbDirectory.Path, "*", SearchOption.AllDirectories))
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

    private static void SkipUnlessLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("the ladder's cold-page-cache rung needs posix_fadvise and /proc/self/io, so the "
                          + "residency this harness reports can only be established on Linux");
        }
    }

    private void AssertProbeIsRejectedBySimulation()
    {
        long failuresBefore = Nethermind.TxPool.Metrics.PendingTransactionsFrameTxSimulationFailed;
        AcceptTxResult result = _chain.TxPool.SubmitTx(FrameTx(0), TxHandlingOptions.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(AcceptTxResult.FrameSimulationFailed),
                $"the probe must be rejected by the simulation stage, not by a cheaper upstream filter (got {result})");
            Assert.That(Nethermind.TxPool.Metrics.PendingTransactionsFrameTxSimulationFailed,
                Is.GreaterThan(failuresBefore), "the simulation-failure counter did not move");
            Assert.That(_chain.TxPool.GetPendingTransactionsCount(), Is.Zero,
                "a rejected frame transaction must not occupy a pool slot");
        }
    }

    private void SubmitFrame(int slotSalt, int uniqueSalt) =>
        _chain.TxPool.SubmitTx(FrameTx(slotSalt, uniqueSalt), TxHandlingOptions.None);

    /// <summary>
    /// Runs one prefix outside the timed path and counts the storage reads it completed, so the per-slot
    /// figures are divided by observed work rather than by an arithmetic expectation.
    /// </summary>
    private int ProbeColdSloadCount()
    {
        SloadCountingTracer tracer = new();
        BlockHeader head = _chain.BlockTree.Head!.Header;

        using IReadOnlyTxProcessorSource source = _chain.ReadOnlyTxProcessingEnvFactory.Create();
        using IReadOnlyTxProcessingScope scope = source.Build(head);
        scope.TransactionProcessor.SetBlockExecutionContext(head);
        scope.TransactionProcessor.Process(FrameTx(0), tracer, ExecutionOptions.FrameValidationPrefixOnly);

        return tracer.Sloads;
    }

    private async Task BuildChain(int slotsToSeed)
    {
        _dbDirectory = TempPath.GetTempDirectory();
        Directory.CreateDirectory(_dbDirectory.Path);

        byte[] attackCode = ColdSloadLoop();
        string dbPath = _dbDirectory.Path;

        _chain = await StorageIoTestBlockchain.CreateStorageIo(dbPath, builder =>
        {
            builder.AddSingleton<ISpecProvider>(new TestSpecProvider(Eip8141Prototype.Instance));
            builder.AddScoped<IGenesisPostProcessor, IWorldState, ISpecProvider>((worldState, specProvider) =>
                new FunctionalGenesisPostProcessor(_ =>
                {
                    worldState.CreateAccount(Attacker, AttackerBalance);
                    worldState.InsertCode(Attacker, attackCode, specProvider.GenesisSpec);
                    for (int i = 0; i < slotsToSeed; i++)
                    {
                        worldState.Set(new StorageCell(Attacker, SlotOf(i)), SeededValue);
                    }
                    worldState.Commit(specProvider.GenesisSpec);
                    worldState.RecalculateStateRoot();
                }));
        });

        // The pool refuses everything while the tree reports a zero best-suggested block.
        await _chain.AddBlock();

        FlushState();
        AssertSeededSlotIsVisible(slotsToSeed);
    }

    private void AssertSeededSlotIsVisible(int slotsSeeded)
    {
        BlockHeader head = _chain.BlockTree.Head!.Header;
        using IReadOnlyTxProcessorSource source = _chain.ReadOnlyTxProcessingEnvFactory.Create();
        using IReadOnlyTxProcessingScope scope = source.Build(head);

        scope.WorldState.Get(new StorageCell(Attacker, SlotOf(slotsSeeded - 1)), out UInt256 last);
        Assert.That(last, Is.EqualTo(SeededValue),
            "the last seeded slot is not readable at the head, so the prefix would read empty slots and the "
            + "measurement would describe missing-slot handling rather than a storage read");
    }

    /// <summary>Maps a slot ordinal to the scattered index the prefix computes on-chain.</summary>
    private static UInt256 SlotOf(int ordinal) => ScatterMultiplier * (UInt256)ordinal;

    /// <summary>
    /// Reads the salt from calldata and then walks <c>SLOAD</c>s at <c>(salt + i) * ScatterMultiplier</c>
    /// until the frame budget is exhausted.
    /// </summary>
    /// <remarks>The loop never terminates on its own; it runs out of gas, which is how the other
    /// budget-burning shapes fail too.</remarks>
    private static byte[] ColdSloadLoop()
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

    /// <summary>
    /// Builds a frame transaction whose calldata word selects the slot range the prefix reads, so each
    /// sample can be given slots no earlier sample touched.
    /// </summary>
    private Transaction FrameTx(int slotSalt, int uniqueSalt = 0)
    {
        // Word 0 is the only one the prefix reads. Word 1 keeps hashes distinct when a rung deliberately
        // replays one slot range, which the pool would otherwise refuse as already known.
        byte[] data = new byte[64];
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(28), slotSalt * _saltStride);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(60), uniqueSalt);

        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            ChainId = TestBlockchainIds.ChainId,
            Nonce = 0,
            SenderAddress = Attacker,
            Frames = [new TxFrame(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, target: null, gasLimit: _ceiling, UInt256.Zero, data)],
            FrameSignatures = [],
            GasLimit = 1_000_000,
            GasPrice = 1.GWei,
            DecodedMaxFeePerGas = 1.GWei,
        };
        tx.Hash = tx.CalculateHash();
        return tx;
    }

    // Nearest-rank keeps every reported percentile tied to an observed sample.
    private static double Percentile(List<double> sorted, double quantile)
    {
        if (sorted.Count == 0) return double.NaN;
        int rank = (int)Math.Ceiling(quantile * sorted.Count);
        return sorted[Math.Clamp(rank, 1, sorted.Count) - 1];
    }

    private static void Emit(string line)
    {
        string path = Environment.GetEnvironmentVariable("FRAME_STORAGE_IO_OUT")
                      ?? Path.Combine(Path.GetTempPath(), "frame-tx-storage-io.txt");
        string record = $"RESULT {line}";
        TestContext.Out.WriteLine(record);
        File.AppendAllText(path, record + Environment.NewLine);
    }

    /// <summary>Counts completed storage reads in the outermost validation frame.</summary>
    /// <remarks>An operation only counts once a later one starts, so the trailing <c>SLOAD</c> that the
    /// budget cannot pay for is charged gas, aborts before any read, and is not counted.</remarks>
    private sealed class SloadCountingTracer : TxTracer
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
    /// A test chain on the patricia-trie state backend over a real RocksDB, with the caches that would
    /// otherwise hold the whole seeded fixture held at their floor.
    /// </summary>
    /// <remarks>The flat-state backend resolves a slot in one keyed read rather than a trie walk, so it is
    /// the cheaper of the two and the trie backend is the adversarial one. The rows say which was measured.</remarks>
    private sealed class StorageIoTestBlockchain : BasicTestBlockchain
    {
        private string _dbPath = null!;

        public static async Task<StorageIoTestBlockchain> CreateStorageIo(
            string dbPath, Action<ContainerBuilder>? configurer = null)
        {
            StorageIoTestBlockchain chain = new() { _dbPath = dbPath, UseFlatDb = false };
            await chain.Build(configurer);
            return chain;
        }

        protected override IEnumerable<IConfig> CreateConfigs() =>
        [
            new BlocksConfig { MinGasPrice = 0 },
            new TxPoolConfig
            {
                GasLimit = BlockGasLimit,
                // The per-head budget sheds admission after a second of simulation against one head; this
                // harness times single rejections against a fixed head, so leaving it on would measure the
                // shed path rather than the prefix.
                FrameTxSimulationBudgetPerHeadMs = int.MaxValue,
            },
            // Archive mode persists every block, so the seeded state is in the database rather than pending
            // in the trie store's dirty set when the first sample reads it.
            new PruningConfig
            {
                Mode = PruningMode.None,
                PersistenceInterval = 1,
                CacheMb = TrieCacheMb,
                DirtyCacheMb = TrieCacheMb,
            },
            new DbConfig { SharedBlockCacheSize = MinimalSharedBlockCacheSize },
        ];

        protected override ContainerBuilder ConfigureContainer(ContainerBuilder builder, IConfigProvider configProvider) =>
            base.ConfigureContainer(builder, configProvider)
                .AddSingleton<IDbFactory, RocksDbFactory>()
                .Intercept<IInitConfig>(initConfig => initConfig.BaseDbPath = _dbPath);
    }
}
