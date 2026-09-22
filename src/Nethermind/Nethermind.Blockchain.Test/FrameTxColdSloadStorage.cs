// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
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
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
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

                string mountPoint = head[4];
                if (!path.StartsWith(mountPoint, StringComparison.Ordinal) || mountPoint.Length < best.Length) continue;

                best = mountPoint;
                type = tail[0];
            }
        }
        catch (IOException)
        {
            return "unknown";
        }

        return type;
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
/// A test chain on the patricia-trie state backend over a real RocksDB, with the caches that would
/// otherwise hold the whole seeded fixture held at their floor.
/// </summary>
/// <remarks>
/// Every other shape in this campaign runs on <c>MemDbFactory</c> (<c>TestEnvironmentModule</c>), where a
/// cold <c>SLOAD</c> is a dictionary lookup. This swaps <see cref="IDbFactory"/> for
/// <see cref="RocksDbFactory"/> over a temp directory, the same override
/// <c>FullPruning.FullPruningDiskTest</c> uses, so a slot read is a patricia-trie walk ending in RocksDB
/// reads.
///
/// The flat-state backend resolves a slot in one keyed read rather than a trie walk, so it is the cheaper
/// of the two and the trie backend is the adversarial one. Rows say which was measured.
/// </remarks>
internal sealed class ColdSloadTestBlockchain : BasicTestBlockchain
{
    public const long BlockGasLimit = 30_000_000;

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

    private string _dbPath = null!;

    public static async Task<ColdSloadTestBlockchain> CreateColdSload(
        string dbPath, Action<ContainerBuilder>? configurer = null)
    {
        ColdSloadTestBlockchain chain = new() { _dbPath = dbPath, UseFlatDb = false };
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
        string dbPath, Address attacker, UInt256 attackerBalance, int slotsToSeed,
        IReadOnlyList<(Address Address, string Shape)>? otherShapes = null)
    {
        byte[] attackCode = ColdSloadPrefix.Code();

        ColdSloadTestBlockchain chain = await ColdSloadTestBlockchain.CreateColdSload(dbPath, builder =>
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
