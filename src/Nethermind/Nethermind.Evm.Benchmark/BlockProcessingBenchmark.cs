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
/// Per-iteration state (DB, world state, DI scope) is pre-built in
/// <see cref="GlobalSetup"/> to keep allocation noise out of the measurement window.
/// <c>[IterationSetup]</c> only picks the next pre-built state from the pool.
///
/// Scenarios:
/// - EmptyBlock, SingleTransfer, Transfers_50, Transfers_200
/// - Eip1559_200, AccessList_50, ContractDeploy_10
/// - ContractCall_200, MixedBlock (100 legacy + 60 EIP-1559 + 30 AL + 10 calls)
/// </summary>
[Config(typeof(BlockProcessingConfig))]
[MemoryDiagnoser]
[JsonExporterAttribute.FullCompressed]
public class BlockProcessingBenchmark
{
    /// <summary>
    /// 1000 data points per benchmark (10 launches x 100 iterations, 20 warmup each).
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
                .WithLaunchCount(10)
                .WithWarmupCount(20)
                .WithIterationCount(100)
                .WithGcForce(true));
            AddColumn(StatisticColumn.Min);
            AddColumn(StatisticColumn.Max);
            AddColumn(StatisticColumn.Median);
            AddColumn(StatisticColumn.P90);
            AddColumn(StatisticColumn.P95);
        }
    }

    // 20 warmup + 100 iterations + safety margin
    private const int PoolSize = 150;

    private static readonly IReleaseSpec Spec = Osaka.Instance;

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

    // Pre-built state pool — avoids allocation noise in the measurement window
    private IterationState[] _statePool = null!;
    private int _stateIndex;

    // Per-iteration state (assigned from pool in IterationSetup)
    private IWorldState _stateProvider = null!;
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

        // Build DI container using standard modules instead of hand-wiring.
        // TestNethermindModule wires PseudoNethermindModule + TestEnvironmentModule
        // with TestSpecProvider(Osaka.Instance) and in-memory databases.
        // Includes PrewarmerModule (via NethermindModule) for block cache pre-warming.
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(Osaka.Instance))
            .Build();

        // Pre-build state pool so IterationSetup is allocation-free
        _statePool = new IterationState[PoolSize];
        _stateIndex = 0;
        for (int i = 0; i < PoolSize; i++)
        {
            _statePool[i] = BuildIterationState();
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        for (int i = 0; i < _statePool.Length; i++)
        {
            _statePool[i].Scope?.Dispose();
        }

        _container.Dispose();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        IterationState state = _statePool[_stateIndex++];
        _processingScope = state.Scope;
        _stateProvider = state.WorldState;
        _branchProcessor = state.BranchProcessor;
        _parentHeader = state.ParentHeader;
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        // Scope disposal is handled by GlobalCleanup to avoid
        // per-iteration teardown noise leaking into measurement.
        _processingScope = null!;
    }

    // ── Benchmarks ────────────────────────────────────────────────────────

    [Benchmark]
    public Block[] EmptyBlock()
        => _branchProcessor.Process(_parentHeader, [_emptyBlock],
            ProcessingOptions.NoValidation, NullBlockTracer.Instance);

    [Benchmark]
    public Block[] SingleTransfer()
        => _branchProcessor.Process(_parentHeader, [_singleTransferBlock],
            ProcessingOptions.NoValidation, NullBlockTracer.Instance);

    [Benchmark]
    public Block[] Transfers_50()
        => _branchProcessor.Process(_parentHeader, [_transfers50Block],
            ProcessingOptions.NoValidation, NullBlockTracer.Instance);

    [Benchmark(Baseline = true)]
    public Block[] Transfers_200()
        => _branchProcessor.Process(_parentHeader, [_transfers200Block],
            ProcessingOptions.NoValidation, NullBlockTracer.Instance);

    [Benchmark]
    public Block[] Eip1559_200()
        => _branchProcessor.Process(_parentHeader, [_eip1559_200Block],
            ProcessingOptions.NoValidation, NullBlockTracer.Instance);

    [Benchmark]
    public Block[] AccessList_50()
        => _branchProcessor.Process(_parentHeader, [_accessList50Block],
            ProcessingOptions.NoValidation, NullBlockTracer.Instance);

    [Benchmark]
    public Block[] ContractDeploy_10()
        => _branchProcessor.Process(_parentHeader, [_contractDeploy10Block],
            ProcessingOptions.NoValidation, NullBlockTracer.Instance);

    [Benchmark]
    public Block[] ContractCall_200()
        => _branchProcessor.Process(_parentHeader, [_contractCall200Block],
            ProcessingOptions.NoValidation, NullBlockTracer.Instance);

    [Benchmark]
    public Block[] MixedBlock()
        => _branchProcessor.Process(_parentHeader, [_mixedBlock],
            ProcessingOptions.NoValidation, NullBlockTracer.Instance);

    // ── State pool builder ──────────────────────────────────────────────

    private IterationState BuildIterationState()
    {
        IDbProvider dbProvider = TestMemDbProvider.Init();
        IWorldStateManager wsm = TestWorldStateFactory.CreateWorldStateManagerForTest(dbProvider, LimboLogs.Instance);
        IWorldStateScopeProvider scopeProvider = wsm.GlobalWorldState;

        IBlockValidationModule[] validationModules = _container.Resolve<IBlockValidationModule[]>();
        IMainProcessingModule[] mainProcessingModules = _container.Resolve<IMainProcessingModule[]>();
        ILifetimeScope processingScope = _container.BeginLifetimeScope(b =>
        {
            b.RegisterInstance(scopeProvider).As<IWorldStateScopeProvider>().ExternallyOwned();
            b.RegisterInstance(wsm).As<IWorldStateManager>().ExternallyOwned();
            b.AddModule(validationModules);
            b.AddModule(mainProcessingModules);
        });

        IWorldState stateProvider = processingScope.Resolve<IWorldState>();

        using (stateProvider.BeginScope(IWorldState.PreGenesis))
        {
            stateProvider.CreateAccount(_sender, 1_000_000.Ether());

            stateProvider.CreateAccount(TestItem.AddressB, UInt256.Zero);
            stateProvider.InsertCode(TestItem.AddressB, ContractCode, Spec);

            stateProvider.CreateAccount(Eip7002Constants.WithdrawalRequestPredeployAddress, UInt256.Zero);
            stateProvider.InsertCode(Eip7002Constants.WithdrawalRequestPredeployAddress, StopCode, Spec);
            stateProvider.CreateAccount(Eip7251Constants.ConsolidationRequestPredeployAddress, UInt256.Zero);
            stateProvider.InsertCode(Eip7251Constants.ConsolidationRequestPredeployAddress, StopCode, Spec);

            stateProvider.Commit(Spec);
            stateProvider.CommitTree(0);
        }

        BlockHeader parentHeader = Build.A.BlockHeader
            .WithNumber(0)
            .WithStateRoot(stateProvider.StateRoot)
            .WithGasLimit(30_000_000)
            .TestObject;

        IBranchProcessor branchProcessor = processingScope.Resolve<IBranchProcessor>();

        return new IterationState(processingScope, stateProvider, branchProcessor, parentHeader);
    }

    private readonly record struct IterationState(
        ILifetimeScope Scope,
        IWorldState WorldState,
        IBranchProcessor BranchProcessor,
        BlockHeader ParentHeader);

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
}
