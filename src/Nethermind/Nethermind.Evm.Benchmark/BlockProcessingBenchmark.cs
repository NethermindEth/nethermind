// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Autofac;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Container;
using Nethermind.Core.Test.Db;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Api;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Init.Modules;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.State.SnapServer;
using Nethermind.Trie.Pruning;

namespace Nethermind.Evm.Benchmark;

public enum StateBackend { Trie, FlatState }

/// <summary>
/// Block-level processing benchmark measuring <see cref="BranchProcessor.Process"/>
/// with mainnet-like block scenarios under <see cref="Osaka"/> rules.
///
/// Uses the full <see cref="BranchProcessor"/> pipeline — including the
/// <see cref="BlockCachePreWarmer"/> — to match the live client's block processing
/// path. Pre-warming triggers for blocks with 3+ transactions.
///
/// A single world state and branch processor are built once in <see cref="GlobalSetup"/>.
/// <see cref="BranchProcessor.Process"/> manages its own scope internally, so each
/// iteration opens a fresh scope at the genesis root and disposes it on exit —
/// exactly as the runtime does.
///
/// Each benchmark method loops <see cref="N"/> times with
/// <c>OperationsPerInvoke = N</c> so BDN divides the total time by N.
/// This keeps iteration time well above the 100 ms minimum, eliminating
/// MinIterationTime warnings and reducing coefficient of variation caused
/// by OS scheduling noise on sub-millisecond measurements.
///
/// Scenarios:
/// - EmptyBlock, SingleTransfer, Transfers_50, Transfers_200
/// - Eip1559_200, AccessList_50, ContractDeploy_10
/// - ContractCall_200, MixedBlock (100 legacy + 60 EIP-1559 + 30 AL + 10 calls)
/// - ERC20_Transfer_200 (storage-heavy: 2 SLOAD + 2 SSTORE + 2 KECCAK per tx)
/// - Swap_200 (storage-heavy: 8 SLOAD + 6 SSTORE + 1 KECCAK per tx)
/// </summary>
[Config(typeof(BlockProcessingConfig))]
[MemoryDiagnoser]
[JsonExporterAttribute.FullCompressed]
public class BlockProcessingBenchmark
{
    [Params(StateBackend.Trie, StateBackend.FlatState)]
    public StateBackend Backend { get; set; }

    /// <summary>
    /// Repetitions per BDN invocation. Used for low per-op benchmarks
    /// (EmptyBlock ~21 us, SingleTransfer ~46 us, ContractDeploy_10 ~400 us)
    /// where higher rep counts amortize OS scheduling noise and prewarmer
    /// thread contention, keeping iteration time above BDN's 100 ms minimum.
    /// </summary>
    private const int N_XLARGE = 20_000;

    private const int N_LARGE = 5000;

    private const int N_SMALL = 200;

    private const int N_MEDIUM = 500;

    /// <summary>
    /// 10 data points per benchmark (1 launch x 10 iterations, 3 warmup).
    /// GcForce ensures a GC collection between iterations to reduce allocation noise.
    /// Server GC and concurrent GC are disabled for deterministic collection pauses.
    /// </summary>
    private class BlockProcessingConfig : ManualConfig
    {
        public BlockProcessingConfig()
        {
            AddJob(Job.Default
                .WithToolchain(InProcessNoEmitToolchain.Default)
                .WithInvocationCount(1)
                .WithUnrollFactor(1)
                .WithLaunchCount(1)
                .WithWarmupCount(3)
                .WithIterationCount(10)
                .WithGcForce(true)
                .WithEnvironmentVariable("DOTNET_GCServer", "0")
                .WithEnvironmentVariable("DOTNET_gcConcurrent", "0"));
            AddColumn(StatisticColumn.Min);
            AddColumn(StatisticColumn.Max);
            AddColumn(StatisticColumn.Median);
            AddColumn(StatisticColumn.P90);
            AddColumn(StatisticColumn.P95);
        }
    }

    private static readonly IReleaseSpec Spec = Osaka.Instance;

    private static readonly Address Erc20Address = Address.FromNumber(0x1000);
    private static readonly Address SwapAddress = Address.FromNumber(0x2000);
    private static readonly Address WriteHeavyAddress = Address.FromNumber(0x3000);
    private static readonly Address ReadHeavyAddress = Address.FromNumber(0x4000);
    private static readonly Address MixedStorageAddress = Address.FromNumber(0x5000);

    private static readonly byte[] ContractCode = Prepare.EvmCode
        .PushData(0x01)
        .Op(Instruction.STOP)
        .Done;

    // Minimal bytecode (STOP) for system contract stubs
    private static readonly byte[] StopCode = [0x00];

    private static readonly AccessList SampleAccessList = new AccessList.Builder()
        .AddAddress(TestItem.AddressC)
        .AddStorage(UInt256.Zero)
        .AddStorage(UInt256.One)
        .AddStorage(new UInt256(2))
        .AddAddress(TestItem.AddressD)
        .AddStorage(new UInt256(10))
        .AddStorage(new UInt256(11))
        .AddStorage(new UInt256(12))
        .Build();

    private readonly PrivateKey _senderKey = TestItem.PrivateKeyA;
    private Address _sender = null!;

    // RocksDB environment — temp directory and DB lifecycle
    private BenchmarkEnvironmentModule? _benchmarkModule;
    private IDbProvider? _dbProvider;
    private RocksDbPersistence? _flatPersistence;
    private IColumnsDb<FlatDbColumns>? _flatColumnsDb;

    // DI container — built once, shared across all iterations
    private IContainer _container = null!;

    // Single processing scope, branch processor and parent header — shared across all iterations
    private ILifetimeScope _processingScope = null!;
    private IBranchProcessor _branchProcessor = null!;
    private BlockHeader _parentHeader = null!;

    // Pre-built blocks (immutable after GlobalSetup)
    private Block _emptyBlock = null!;
    private Block _singleTransferBlock = null!;
    private Block _transfers50Block = null!;
    private Block _transfers200Block = null!;
    private Block _eip1559_200Block = null!;
    private Block _accessList50Block = null!;
    private Block _contractDeploy10Block = null!;
    private Block _contractCall200Block = null!;
    private Block _mixedBlock = null!;
    private Block _erc20Transfer200Block = null!;
    private Block _swap200Block = null!;
    private Block _storageWrite200Block = null!;
    private Block _storageRead200Block = null!;
    private Block _storageMixed200Block = null!;

    private BlockHeader _header = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        // Pin to a single core to reduce OS scheduler jitter
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            Process.GetCurrentProcess().ProcessorAffinity = new IntPtr(1);
        }

        _sender = _senderKey.Address;

        _header = Build.A.BlockHeader
            .WithNumber(1)
            .WithGasLimit(30_000_000)
            .WithBaseFee(1.GWei)
            .WithTimestamp(1)
            .TestObject;

        _emptyBlock = BuildBlock();
        _singleTransferBlock = BuildBlock(BuildLegacyTransfers(1, 0));
        _transfers50Block = BuildBlock(BuildLegacyTransfers(50, 0));
        _transfers200Block = BuildBlock(BuildLegacyTransfers(200, 0));
        _eip1559_200Block = BuildBlock(BuildEip1559Transfers(200, 0));
        _accessList50Block = BuildBlock(BuildAccessListTxs(50, 0));
        _contractDeploy10Block = BuildBlock(BuildContractDeploys(10, 0));
        _contractCall200Block = BuildBlock(BuildContractCalls(200, 0));

        // MixedBlock: 100 legacy + 60 EIP-1559 + 30 access-list + 10 contract calls
        Transaction[] mixedTxs = new Transaction[200];
        int nonce = 0;
        BuildLegacyTransfers(100, nonce).CopyTo(mixedTxs, 0);
        nonce += 100;
        BuildEip1559Transfers(60, nonce).CopyTo(mixedTxs, 100);
        nonce += 60;
        BuildAccessListTxs(30, nonce).CopyTo(mixedTxs, 160);
        nonce += 30;
        BuildContractCalls(10, nonce).CopyTo(mixedTxs, 190);
        _mixedBlock = BuildBlock(mixedTxs);

        _erc20Transfer200Block = BuildBlock(BuildErc20Transfers(200, 0));
        _swap200Block = BuildBlock(BuildSwapCalls(200, 0));
        _storageWrite200Block = BuildBlock(BuildStorageCalls(200, 0, WriteHeavyAddress));
        _storageRead200Block = BuildBlock(BuildStorageCalls(200, 0, ReadHeavyAddress));
        _storageMixed200Block = BuildBlock(BuildStorageCalls(200, 0, MixedStorageAddress));

        // Build DI container using standard modules instead of hand-wiring.
        // TestNethermindModule wires PseudoNethermindModule + TestEnvironmentModule
        // with TestSpecProvider(Osaka.Instance) and in-memory databases.
        // Includes PrewarmerModule (via NethermindModule) for block cache pre-warming.
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(Osaka.Instance))
            .Build();

        // Single world state — BranchProcessor.Process() manages scope internally,
        // matching the live client's block processing path.
        // Use RocksDB-backed storage to match production I/O characteristics.
        _benchmarkModule = new BenchmarkEnvironmentModule();
        InitConfig initConfig = new() { BaseDbPath = _benchmarkModule.BasePath };
        IContainer dbContainer = new ContainerBuilder()
            .AddModule(new DbModule(
                initConfig,
                new ReceiptConfig(),
                new SyncConfig()
            ))
            .AddSingleton<IInitConfig>(initConfig)
            .AddSingleton<IDbConfig>(new DbConfig { SharedBlockCacheSize = 256UL * 1024 * 1024 })
            .AddSingleton<IPruningConfig>(new PruningConfig())
            .AddSingleton<IHardwareInfo>(new TestHardwareInfo())
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton<IDbProvider, ContainerOwningDbProvider>()
            .Build();
        IDbProvider dbProvider = dbContainer.Resolve<IDbProvider>();
        IDbFactory dbFactory = dbContainer.Resolve<IDbFactory>();
        _dbProvider = dbProvider;
        IWorldStateManager wsm;
        BenchmarkFlatDbManager? flatDbManagerRef = null;
        if (Backend == StateBackend.FlatState)
        {
            _flatColumnsDb = dbFactory.CreateColumnsDb<FlatDbColumns>(
                new DbSettings(DbNames.Flat, DbNames.Flat));
            _flatPersistence = new RocksDbPersistence(_flatColumnsDb);

            FlatDbConfig flatDbConfig = new() { TrieWarmerWorkerCount = Math.Max(Environment.ProcessorCount - 1, 1) };
            ResourcePool resourcePool = new(flatDbConfig);
            TrieNodeCache trieNodeCache = new(flatDbConfig, LimboLogs.Instance);
            BenchmarkFlatDbManager flatDbManager = new(resourcePool, trieNodeCache, _flatPersistence);
            flatDbManagerRef = flatDbManager;
            BenchmarkProcessExitSource exitSource = new();
            TrieWarmer trieWarmer = new(exitSource, LimboLogs.Instance, flatDbConfig);
            FlatScopeProvider flatScopeProvider = new(
                dbProvider.CodeDb,
                flatDbManager,
                flatDbConfig,
                trieWarmer,
                ResourcePool.Usage.MainBlockProcessing,
                LimboLogs.Instance,
                isReadOnly: false);
            wsm = new BenchmarkFlatWorldStateManager(flatScopeProvider, flatDbManager, dbProvider.CodeDb);
        }
        else
        {
            wsm = TestWorldStateFactory.CreateWorldStateManagerForTest(dbProvider, LimboLogs.Instance);
        }

        IWorldStateScopeProvider scopeProvider = wsm.GlobalWorldState;

        IBlockValidationModule[] validationModules = _container.Resolve<IBlockValidationModule[]>();
        IMainProcessingModule[] mainProcessingModules = _container.Resolve<IMainProcessingModule[]>();
        _processingScope = _container.BeginLifetimeScope(b =>
        {
            b.RegisterInstance(scopeProvider).As<IWorldStateScopeProvider>().ExternallyOwned();
            b.RegisterInstance(wsm).As<IWorldStateManager>().ExternallyOwned();
            b.AddModule(validationModules);
            b.AddModule(mainProcessingModules);
        });

        IWorldState stateProvider = _processingScope.Resolve<IWorldState>();

        using (stateProvider.BeginScope(IWorldState.PreGenesis))
        {
            stateProvider.CreateAccount(_sender, 1_000_000.Ether);

            stateProvider.CreateAccount(TestItem.AddressB, UInt256.Zero);
            stateProvider.InsertCode(TestItem.AddressB, ContractCode, Spec);

            stateProvider.CreateAccount(Eip7002Constants.WithdrawalRequestPredeployAddress, UInt256.Zero);
            stateProvider.InsertCode(Eip7002Constants.WithdrawalRequestPredeployAddress, StopCode, Spec);
            stateProvider.CreateAccount(Eip7251Constants.ConsolidationRequestPredeployAddress, UInt256.Zero);
            stateProvider.InsertCode(Eip7251Constants.ConsolidationRequestPredeployAddress, StopCode, Spec);

            // ── ERC20 contract: deploy code and pre-seed sender balance ──
            stateProvider.CreateAccount(Erc20Address, UInt256.Zero);
            stateProvider.InsertCode(Erc20Address, StorageBenchmarkContracts.BuildErc20RuntimeCode(), Spec);

            UInt256 senderBalanceSlot = StorageBenchmarkContracts.ComputeMappingSlot(_sender, UInt256.Zero);
            byte[] senderBalance = new byte[32];
            ((UInt256)1_000_000).ToBigEndian(senderBalance);
            stateProvider.Set(new StorageCell(Erc20Address, senderBalanceSlot), senderBalance);

            // Pre-seed first half of recipient balances so the benchmark measures a realistic mix:
            // - 100 recipients with existing balance (non-zero->non-zero SSTORE, 2,900 gas)
            // - 100 recipients with zero balance (zero->non-zero SSTORE, 20,000 gas)
            byte[] recipientInitialBalance = new byte[32];
            ((UInt256)100).ToBigEndian(recipientInitialBalance);
            for (int i = 0; i < 100; i++)
            {
                Address recipient = Address.FromNumber((UInt256)(100 + i));
                UInt256 recipientSlot = StorageBenchmarkContracts.ComputeMappingSlot(recipient, UInt256.Zero);
                stateProvider.Set(new StorageCell(Erc20Address, recipientSlot), recipientInitialBalance);
            }

            // Seed Swap contract's ERC20 balance so nested CALLs to ERC20.transfer succeed
            UInt256 swapErc20Slot = StorageBenchmarkContracts.ComputeMappingSlot(SwapAddress, UInt256.Zero);
            byte[] swapErc20Balance = new byte[32];
            ((UInt256)1_000_000_000).ToBigEndian(swapErc20Balance);
            stateProvider.Set(new StorageCell(Erc20Address, swapErc20Slot), swapErc20Balance);

            // ── Swap contract: deploy code and pre-seed pool state ──
            stateProvider.CreateAccount(SwapAddress, UInt256.Zero);
            stateProvider.InsertCode(SwapAddress, StorageBenchmarkContracts.BuildSwapRuntimeCode(Erc20Address), Spec);

            // Pre-seed slots 0-7 with non-zero values so SSTOREs are non-zero->non-zero (2,900 gas, not 20,000)
            SeedSwapSlot(stateProvider, 0, 1_000_000_000);    // reserve0
            SeedSwapSlot(stateProvider, 1, 1_000_000_000);    // reserve1
            SeedSwapSlot(stateProvider, 2, 500_000);           // totalLiquidity
            SeedSwapSlot(stateProvider, 3, 30);                // feeNumerator (initial accumulator)
            SeedSwapSlot(stateProvider, 4, 1);                 // lastTimestamp
            SeedSwapSlot(stateProvider, 5, 1);                 // priceCumulative0
            SeedSwapSlot(stateProvider, 6, 1);                 // priceCumulative1
            SeedSwapSlot(stateProvider, 7, 1_000_000_000);    // kLast

            // Pre-seed sender balance in mapping slot 8 so first SSTORE is non-zero->non-zero
            UInt256 senderSwapSlot = StorageBenchmarkContracts.ComputeMappingSlot(_sender, (UInt256)8);
            byte[] senderSwapBalance = new byte[32];
            ((UInt256)1_000).ToBigEndian(senderSwapBalance);
            stateProvider.Set(new StorageCell(SwapAddress, senderSwapSlot), senderSwapBalance);

            // ── Storage write-heavy contract ──
            stateProvider.CreateAccount(WriteHeavyAddress, UInt256.Zero);
            stateProvider.InsertCode(WriteHeavyAddress, StorageBenchmarkContracts.BuildStorageWriteHeavyCode(), Spec);
            for (int s = 0; s < 8; s++) SeedSwapSlot(stateProvider, WriteHeavyAddress, (UInt256)s, 1);

            // ── Storage read-heavy contract ──
            stateProvider.CreateAccount(ReadHeavyAddress, UInt256.Zero);
            stateProvider.InsertCode(ReadHeavyAddress, StorageBenchmarkContracts.BuildStorageReadHeavyCode(), Spec);
            for (int s = 0; s < 8; s++) SeedSwapSlot(stateProvider, ReadHeavyAddress, (UInt256)s, (ulong)(s + 1) * 1000);

            // ── Storage mixed contract ──
            stateProvider.CreateAccount(MixedStorageAddress, UInt256.Zero);
            stateProvider.InsertCode(MixedStorageAddress, StorageBenchmarkContracts.BuildStorageMixedCode(), Spec);
            for (int s = 0; s < 8; s++) SeedSwapSlot(stateProvider, MixedStorageAddress, (UInt256)s, (ulong)(s + 1) * 100);

            stateProvider.Commit(Spec);
            stateProvider.CommitTree(0);

            _parentHeader = Build.A.BlockHeader
                .WithNumber(0)
                .WithStateRoot(stateProvider.StateRoot)
                .WithGasLimit(30_000_000)
                .TestObject;
        }

        // Freeze the flat db manager so benchmark iterations don't accumulate snapshots
        flatDbManagerRef?.Freeze();

        _branchProcessor = _processingScope.Resolve<IBranchProcessor>();

        // Verify contracts are executing (not reverting) by checking gasUsed
        VerifyBlock("Transfers_200", _transfers200Block, 200 * 21_000L);
        VerifyBlock("ERC20_Transfer_200", _erc20Transfer200Block, 200 * 30_000L);
        VerifyBlock("Swap_200", _swap200Block, 200 * 40_000L);
        VerifyBlock("StorageWrite_200", _storageWrite200Block, 200 * 25_000L);
        VerifyBlock("StorageRead_200", _storageRead200Block, 200 * 22_000L);
        VerifyBlock("StorageMixed_200", _storageMixed200Block, 200 * 25_000L);
        VerifyBlock("MixedBlock", _mixedBlock, 200 * 21_000L);
    }

    private void VerifyBlock(string name, Block block, long minExpectedGas)
    {
        Block[] result = _branchProcessor.Process(_parentHeader, [block],
            ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        long gasUsed = result[0].GasUsed;
        long gasPerTx = block.Transactions.Length > 0 ? gasUsed / block.Transactions.Length : 0;
        if (gasUsed < minExpectedGas)
            throw new InvalidOperationException(
                $"BENCHMARK VERIFICATION FAILED: {name} gasUsed={gasUsed:N0} < minExpected={minExpectedGas:N0} " +
                $"(txs={block.Transactions.Length}, gasPerTx={gasPerTx:N0}). Transactions are likely reverting!");
    }

    [IterationSetup]
    public void IterationSetup()
    {
        if (_dbProvider is not null)
        {
            BenchmarkEnvironmentModule.FlushAndCompact(_dbProvider);
            BenchmarkEnvironmentModule.DisableAutoCompaction(_dbProvider);
        }

        if (_flatColumnsDb is DbOnTheRocks flatRocksDb)
        {
            flatRocksDb.Flush();
            flatRocksDb.Compact();
        }

        if (_flatColumnsDb is ITunableDb flatTunableDb)
        {
            flatTunableDb.Tune(ITunableDb.TuneType.DisableCompaction);
        }
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        if (_dbProvider is not null)
            BenchmarkEnvironmentModule.EnableAutoCompaction(_dbProvider);

        if (_flatColumnsDb is ITunableDb flatTunableDb)
            flatTunableDb.Tune(ITunableDb.TuneType.Default);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _processingScope.Dispose();
        _container.Dispose();
        _flatColumnsDb?.Dispose();
        _dbProvider?.Dispose();
        _benchmarkModule?.Cleanup();
    }

    // ── Benchmarks ────────────────────────────────────────────────────────

    [Benchmark(OperationsPerInvoke = N_XLARGE)]
    public Block[] EmptyBlock()
    {
        Block[] result = null!;
        for (int i = 0; i < N_XLARGE; i++)
            result = _branchProcessor.Process(_parentHeader, [_emptyBlock],
                ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        return result;
    }

    [Benchmark(OperationsPerInvoke = N_LARGE)]
    public Block[] SingleTransfer()
    {
        Block[] result = null!;
        for (int i = 0; i < N_LARGE; i++)
            result = _branchProcessor.Process(_parentHeader, [_singleTransferBlock],
                ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        return result;
    }

    [Benchmark(OperationsPerInvoke = N_MEDIUM)]
    public Block[] Transfers_50()
    {
        Block[] result = null!;
        for (int i = 0; i < N_MEDIUM; i++)
            result = _branchProcessor.Process(_parentHeader, [_transfers50Block],
                ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        return result;
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = N_SMALL)]
    public Block[] Transfers_200()
    {
        Block[] result = null!;
        for (int i = 0; i < N_SMALL; i++)
            result = _branchProcessor.Process(_parentHeader, [_transfers200Block],
                ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        return result;
    }

    [Benchmark(OperationsPerInvoke = N_SMALL)]
    public Block[] Eip1559_200()
    {
        Block[] result = null!;
        for (int i = 0; i < N_SMALL; i++)
            result = _branchProcessor.Process(_parentHeader, [_eip1559_200Block],
                ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        return result;
    }

    [Benchmark(OperationsPerInvoke = N_MEDIUM)]
    public Block[] AccessList_50()
    {
        Block[] result = null!;
        for (int i = 0; i < N_MEDIUM; i++)
            result = _branchProcessor.Process(_parentHeader, [_accessList50Block],
                ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        return result;
    }

    [Benchmark(OperationsPerInvoke = N_XLARGE)]
    public Block[] ContractDeploy_10()
    {
        Block[] result = null!;
        for (int i = 0; i < N_XLARGE; i++)
            result = _branchProcessor.Process(_parentHeader, [_contractDeploy10Block],
                ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        return result;
    }

    [Benchmark(OperationsPerInvoke = N_SMALL)]
    public Block[] ContractCall_200()
    {
        Block[] result = null!;
        for (int i = 0; i < N_SMALL; i++)
            result = _branchProcessor.Process(_parentHeader, [_contractCall200Block],
                ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        return result;
    }

    [Benchmark(OperationsPerInvoke = N_SMALL)]
    public Block[] MixedBlock()
    {
        Block[] result = null!;
        for (int i = 0; i < N_SMALL; i++)
            result = _branchProcessor.Process(_parentHeader, [_mixedBlock],
                ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        return result;
    }

    [Benchmark(OperationsPerInvoke = N_SMALL)]
    public Block[] ERC20_Transfer_200()
    {
        Block[] result = null!;
        for (int i = 0; i < N_SMALL; i++)
            result = _branchProcessor.Process(_parentHeader, [_erc20Transfer200Block],
                ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        return result;
    }

    [Benchmark(OperationsPerInvoke = N_SMALL)]
    public Block[] Swap_200()
    {
        Block[] result = null!;
        for (int i = 0; i < N_SMALL; i++)
            result = _branchProcessor.Process(_parentHeader, [_swap200Block],
                ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        return result;
    }

    [Benchmark(OperationsPerInvoke = N_SMALL)]
    public Block[] StorageWrite_200()
    {
        Block[] result = null!;
        for (int i = 0; i < N_SMALL; i++)
            result = _branchProcessor.Process(_parentHeader, [_storageWrite200Block],
                ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        return result;
    }

    [Benchmark(OperationsPerInvoke = N_SMALL)]
    public Block[] StorageRead_200()
    {
        Block[] result = null!;
        for (int i = 0; i < N_SMALL; i++)
            result = _branchProcessor.Process(_parentHeader, [_storageRead200Block],
                ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        return result;
    }

    [Benchmark(OperationsPerInvoke = N_SMALL)]
    public Block[] StorageMixed_200()
    {
        Block[] result = null!;
        for (int i = 0; i < N_SMALL; i++)
            result = _branchProcessor.Process(_parentHeader, [_storageMixed200Block],
                ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        return result;
    }

    // ── Block builder ─────────────────────────────────────────────────────

    private Block BuildBlock(params Transaction[] transactions)
        => Build.A.Block
            .WithHeader(_header)
            .WithTransactions(transactions)
            .TestObject;

    // ── Transaction builders ──────────────────────────────────────────────

    private Transaction[] BuildLegacyTransfers(int count, int startNonce)
    {
        Transaction[] txs = new Transaction[count];
        for (int i = 0; i < count; i++)
        {
            txs[i] = Build.A.Transaction
                .WithNonce((UInt256)(startNonce + i))
                .WithTo(TestItem.AddressC)
                .WithValue(1.Wei)
                .WithGasLimit(21_000)
                .WithGasPrice(2.GWei)
                .SignedAndResolved(_senderKey)
                .TestObject;
        }
        return txs;
    }

    private Transaction[] BuildEip1559Transfers(int count, int startNonce)
    {
        Transaction[] txs = new Transaction[count];
        for (int i = 0; i < count; i++)
        {
            txs[i] = Build.A.Transaction
                .WithType(TxType.EIP1559)
                .WithNonce((UInt256)(startNonce + i))
                .WithTo(TestItem.AddressC)
                .WithValue(1.Wei)
                .WithGasLimit(21_000)
                .WithMaxFeePerGas(2.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .SignedAndResolved(_senderKey)
                .TestObject;
        }
        return txs;
    }

    private Transaction[] BuildAccessListTxs(int count, int startNonce)
    {
        Transaction[] txs = new Transaction[count];
        for (int i = 0; i < count; i++)
        {
            txs[i] = Build.A.Transaction
                .WithType(TxType.AccessList)
                .WithNonce((UInt256)(startNonce + i))
                .WithTo(TestItem.AddressC)
                .WithValue(1.Wei)
                .WithGasLimit(50_000)
                .WithGasPrice(2.GWei)
                .WithAccessList(SampleAccessList)
                .SignedAndResolved(_senderKey)
                .TestObject;
        }
        return txs;
    }

    private Transaction[] BuildContractDeploys(int count, int startNonce)
    {
        Transaction[] txs = new Transaction[count];
        for (int i = 0; i < count; i++)
        {
            txs[i] = Build.A.Transaction
                .WithNonce((UInt256)(startNonce + i))
                .WithTo(null)
                .WithData(ContractCode)
                .WithGasLimit(100_000)
                .WithGasPrice(2.GWei)
                .SignedAndResolved(_senderKey)
                .TestObject;
        }
        return txs;
    }

    private Transaction[] BuildContractCalls(int count, int startNonce)
    {
        Transaction[] txs = new Transaction[count];
        for (int i = 0; i < count; i++)
        {
            txs[i] = Build.A.Transaction
                .WithNonce((UInt256)(startNonce + i))
                .WithTo(TestItem.AddressB)
                .WithGasLimit(50_000)
                .WithGasPrice(2.GWei)
                .SignedAndResolved(_senderKey)
                .TestObject;
        }
        return txs;
    }

    private Transaction[] BuildErc20Transfers(int count, int startNonce)
    {
        Transaction[] txs = new Transaction[count];
        for (int i = 0; i < count; i++)
        {
            // Calldata: [to (32 bytes), amount (32 bytes)]
            byte[] calldata = new byte[64];
            Address.FromNumber((UInt256)(100 + i)).Bytes.CopyTo(calldata.AsSpan(12));
            ((UInt256)1).ToBigEndian(calldata.AsSpan(32));

            txs[i] = Build.A.Transaction
                .WithNonce((UInt256)(startNonce + i))
                .WithTo(Erc20Address)
                .WithData(calldata)
                .WithGasLimit(100_000)
                .WithGasPrice(2.GWei)
                .SignedAndResolved(_senderKey)
                .TestObject;
        }
        return txs;
    }

    private Transaction[] BuildSwapCalls(int count, int startNonce)
    {
        Transaction[] txs = new Transaction[count];
        for (int i = 0; i < count; i++)
        {
            // Calldata: [amountIn (32 bytes)]
            byte[] calldata = new byte[32];
            ((UInt256)(i + 1)).ToBigEndian(calldata);

            txs[i] = Build.A.Transaction
                .WithNonce((UInt256)(startNonce + i))
                .WithTo(SwapAddress)
                .WithData(calldata)
                .WithGasLimit(200_000)
                .WithGasPrice(2.GWei)
                .SignedAndResolved(_senderKey)
                .TestObject;
        }
        return txs;
    }

    private Transaction[] BuildStorageCalls(int count, int startNonce, Address target)
    {
        Transaction[] txs = new Transaction[count];
        for (int i = 0; i < count; i++)
        {
            byte[] calldata = new byte[32];
            ((UInt256)(i + 1)).ToBigEndian(calldata);

            txs[i] = Build.A.Transaction
                .WithNonce((UInt256)(startNonce + i))
                .WithTo(target)
                .WithData(calldata)
                .WithGasLimit(100_000)
                .WithGasPrice(2.GWei)
                .SignedAndResolved(_senderKey)
                .TestObject;
        }
        return txs;
    }

    private static void SeedSwapSlot(IWorldState stateProvider, UInt256 slot, UInt256 value) =>
        SeedSwapSlot(stateProvider, SwapAddress, slot, value);

    private static void SeedSwapSlot(IWorldState stateProvider, Address address, UInt256 slot, UInt256 value)
    {
        byte[] bytes = new byte[32];
        value.ToBigEndian(bytes);
        stateProvider.Set(new StorageCell(address, slot), bytes);
    }

    // ── FlatState helper types ───────────────────────────────────────────

    internal sealed class BenchmarkFlatDbManager(ResourcePool resourcePool, TrieNodeCache trieNodeCache, IPersistence persistence) : IFlatDbManager
    {
        private readonly Lock _lock = new();
        private readonly List<Nethermind.State.Flat.Snapshot> _snapshots = new();
        private bool _frozen;

        /// <summary>
        /// After initial state setup, freeze so benchmark iterations don't accumulate snapshots.
        /// </summary>
        public void Freeze() => _frozen = true;

        public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
        {
            add { }
            remove { }
        }

        public SnapshotBundle GatherSnapshotBundle(in StateId baseBlock, ResourcePool.Usage usage)
        {
            lock (_lock)
            {
                SnapshotPooledList pooled = new(_snapshots.Count);
                for (int i = 0; i < _snapshots.Count; i++)
                {
                    _snapshots[i].AcquireLease();
                    pooled.Add(_snapshots[i]);
                }

                IPersistence.IPersistenceReader persistenceReader = persistence.CreateReader();
                ReadOnlySnapshotBundle roBundle = new(pooled, persistenceReader, recordDetailedMetrics: false);
                return new SnapshotBundle(roBundle, trieNodeCache, resourcePool, usage);
            }
        }

        public ReadOnlySnapshotBundle GatherReadOnlySnapshotBundle(in StateId baseBlock)
        {
            lock (_lock)
            {
                SnapshotPooledList pooled = new(_snapshots.Count);
                for (int i = 0; i < _snapshots.Count; i++)
                {
                    _snapshots[i].AcquireLease();
                    pooled.Add(_snapshots[i]);
                }

                IPersistence.IPersistenceReader persistenceReader = persistence.CreateReader();
                return new ReadOnlySnapshotBundle(pooled, persistenceReader, recordDetailedMetrics: false);
            }
        }

        public bool HasStateForBlock(in StateId stateId) => true;

        public void AddSnapshot(Nethermind.State.Flat.Snapshot snapshot, TransientResource transientResource)
        {
            if (_frozen)
            {
                snapshot.Dispose();
                transientResource.Dispose();
                return;
            }

            lock (_lock)
            {
                _snapshots.Add(snapshot);
            }

            // Populate the trie node cache so BlockCachePreWarmer can resolve state roots
            trieNodeCache.Add(transientResource);
            resourcePool.ReturnCachedResource(ResourcePool.Usage.MainBlockProcessing, transientResource);
        }

        public void FlushCache(CancellationToken cancellationToken) { }
    }

    internal sealed class BenchmarkFlatWorldStateManager(
        FlatScopeProvider flatScopeProvider,
        BenchmarkFlatDbManager flatDbManager,
        IDb codeDb) : IWorldStateManager
    {
        public IWorldStateScopeProvider GlobalWorldState => flatScopeProvider;

        public IStateReader GlobalStateReader => new FlatStateReader(codeDb, flatDbManager, LimboLogs.Instance);

        public ISnapServer? SnapServer => null;

        public IReadOnlyKeyValueStore? HashServer => null;

        public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
        {
            add { }
            remove { }
        }

        public IWorldStateScopeProvider CreateResettableWorldState()
        {
            return new FlatScopeProvider(
                codeDb,
                flatDbManager,
                new FlatDbConfig { TrieCacheMemoryBudget = 0 },
                new NoopTrieWarmer(),
                ResourcePool.Usage.ReadOnlyProcessingEnv,
                LimboLogs.Instance,
                isReadOnly: true);
        }

        public IOverridableWorldScope CreateOverridableWorldScope() => throw new NotSupportedException();

        public bool VerifyTrie(BlockHeader stateAtBlock, CancellationToken cancellationToken) => true;

        public void FlushCache(CancellationToken cancellationToken) { }
    }

    internal sealed class BenchmarkProcessExitSource : IProcessExitSource
    {
        private readonly CancellationTokenSource _cts = new();
        public CancellationToken Token => _cts.Token;
        public void Exit(int exitCode) => _cts.Cancel();
    }
}
