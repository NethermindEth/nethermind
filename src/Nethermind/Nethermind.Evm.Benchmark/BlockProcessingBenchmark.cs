// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
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
using Nethermind.Evm.Test;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;

namespace Nethermind.Evm.Benchmark;

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
    /// <summary>
    /// Repetitions per BDN invocation. Used for low per-op benchmarks
    /// (EmptyBlock ~21 us, SingleTransfer ~46 us, ContractDeploy_10 ~400 us)
    /// where higher rep counts amortize OS scheduling noise and prewarmer
    /// thread contention, keeping iteration time above BDN's 100 ms minimum.
    /// </summary>
    private const int N_LARGE = 5000;

    private const int N_SMALL = 200;

    private const int N_MEDIUM = 500;

    /// <summary>
    /// 20 data points per benchmark (2 launches x 10 iterations, 2 warmup each).
    /// GcForce ensures a GC collection between iterations to reduce allocation noise.
    /// </summary>
    private class BlockProcessingConfig : ManualConfig
    {
        public BlockProcessingConfig()
        {
            AddJob(Job.Default
                .WithToolchain(InProcessNoEmitToolchain.Instance)
                .WithInvocationCount(1)
                .WithUnrollFactor(1)
                .WithLaunchCount(2)
                .WithWarmupCount(2)
                .WithIterationCount(5)
                .WithGcForce(true));
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
            .WithBaseFee(1.GWei())
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

        // Build DI container using standard modules instead of hand-wiring.
        // TestNethermindModule wires PseudoNethermindModule + TestEnvironmentModule
        // with TestSpecProvider(Osaka.Instance) and in-memory databases.
        // Includes PrewarmerModule (via NethermindModule) for block cache pre-warming.
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(Osaka.Instance))
            .Build();

        // Single world state — BranchProcessor.Process() manages scope internally,
        // matching the live client's block processing path.
        IDbProvider dbProvider = TestMemDbProvider.Init();
        IWorldStateManager wsm = TestWorldStateFactory.CreateWorldStateManagerForTest(dbProvider, LimboLogs.Instance);
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
            stateProvider.CreateAccount(_sender, 1_000_000.Ether());

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
            stateProvider.Set(new StorageCell(Erc20Address, senderBalanceSlot), new StorageValue(senderBalance));

            // ── Swap contract: deploy code and pre-seed pool state ──
            stateProvider.CreateAccount(SwapAddress, UInt256.Zero);
            stateProvider.InsertCode(SwapAddress, StorageBenchmarkContracts.BuildSwapRuntimeCode(), Spec);

            // Pre-seed slots 0-7 with non-zero values so SSTOREs are non-zero→non-zero (2,900 gas, not 20,000)
            SeedSwapSlot(stateProvider, 0, 1_000_000_000);    // reserve0
            SeedSwapSlot(stateProvider, 1, 1_000_000_000);    // reserve1
            SeedSwapSlot(stateProvider, 2, 500_000);           // totalLiquidity
            SeedSwapSlot(stateProvider, 3, 30);                // feeNumerator (initial accumulator)
            SeedSwapSlot(stateProvider, 4, 1);                 // lastTimestamp
            SeedSwapSlot(stateProvider, 5, 1);                 // priceCumulative0
            SeedSwapSlot(stateProvider, 6, 1);                 // priceCumulative1
            SeedSwapSlot(stateProvider, 7, 1_000_000_000);    // kLast

            stateProvider.Commit(Spec);
            stateProvider.CommitTree(0);

            _parentHeader = Build.A.BlockHeader
                .WithNumber(0)
                .WithStateRoot(stateProvider.StateRoot)
                .WithGasLimit(30_000_000)
                .TestObject;
        }

        _branchProcessor = _processingScope.Resolve<IBranchProcessor>();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _processingScope.Dispose();
        _container.Dispose();
    }

    // ── Benchmarks ────────────────────────────────────────────────────────

    [Benchmark(OperationsPerInvoke = N_LARGE)]
    public Block[] EmptyBlock()
    {
        Block[] result = null!;
        for (int i = 0; i < N_LARGE; i++)
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

    [Benchmark(OperationsPerInvoke = N_LARGE)]
    public Block[] ContractDeploy_10()
    {
        Block[] result = null!;
        for (int i = 0; i < N_LARGE; i++)
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
                .WithValue(1.Wei())
                .WithGasLimit(21_000)
                .WithGasPrice(2.GWei())
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
                .WithValue(1.Wei())
                .WithGasLimit(21_000)
                .WithMaxFeePerGas(2.GWei())
                .WithMaxPriorityFeePerGas(1.GWei())
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
                .WithValue(1.Wei())
                .WithGasLimit(50_000)
                .WithGasPrice(2.GWei())
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
                .WithGasPrice(2.GWei())
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
                .WithGasPrice(2.GWei())
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
                .WithGasPrice(2.GWei())
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
                .WithGasPrice(2.GWei())
                .SignedAndResolved(_senderKey)
                .TestObject;
        }
        return txs;
    }

    private static void SeedSwapSlot(IWorldState stateProvider, UInt256 slot, UInt256 value)
    {
        byte[] bytes = new byte[32];
        value.ToBigEndian(bytes);
        stateProvider.Set(new StorageCell(SwapAddress, slot), new StorageValue(bytes));
    }
}
