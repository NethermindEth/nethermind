// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
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
/// Uses <c>[IterationSetup]</c> (with <c>InvocationCount=1/UnrollFactor=1</c>) to
/// recreate world state before each measured invocation.
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
    /// 500 data points per benchmark (5 launches x 100 iterations, 20 warmup each).
    /// [IterationSetup] rebuilds world state once per iteration.
    /// InvocationCount=1/UnrollFactor=1 ensures each invocation starts from a fresh state.
    /// </summary>
    private class BlockProcessingConfig : ManualConfig
    {
        public BlockProcessingConfig()
        {
            AddJob(Job.Default
                .WithToolchain(InProcessNoEmitToolchain.Instance)
                .WithInvocationCount(1)
                .WithUnrollFactor(1)
                .WithLaunchCount(5)
                .WithWarmupCount(20)
                .WithIterationCount(100));
            AddColumn(StatisticColumn.Min);
            AddColumn(StatisticColumn.Max);
            AddColumn(StatisticColumn.Median);
            AddColumn(StatisticColumn.P90);
            AddColumn(StatisticColumn.P95);
        }
    }

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

    // Per-iteration state
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
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _container.Dispose();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        // Fresh world state backed by a per-iteration db and trie store.
        // WorldStateManager is needed so the pre-warmer can create isolated
        // read-only snapshots via CreateResettableWorldState().
        IDbProvider dbProvider = TestMemDbProvider.Init();
        IWorldStateManager wsm = TestWorldStateFactory.CreateWorldStateManagerForTest(dbProvider, LimboLogs.Instance);
        IWorldStateScopeProvider scopeProvider = wsm.GlobalWorldState;

        // Create processing scope with per-iteration state infrastructure.
        // IWorldState is registered as scoped by BlockProcessingModule, so each
        // child scope (including pre-warmer envs) gets its own WorldState instance
        // backed by its own IWorldStateScopeProvider — avoiding nested scope errors.
        IBlockValidationModule[] validationModules = _container.Resolve<IBlockValidationModule[]>();
        IMainProcessingModule[] mainProcessingModules = _container.Resolve<IMainProcessingModule[]>();
        _processingScope = _container.BeginLifetimeScope(b =>
        {
            b.RegisterInstance(scopeProvider).As<IWorldStateScopeProvider>().ExternallyOwned();
            b.RegisterInstance(wsm).As<IWorldStateManager>().ExternallyOwned();
            b.AddModule(validationModules);
            b.AddModule(mainProcessingModules);
        });

        // Resolve scoped WorldState and set up initial accounts
        _stateProvider = _processingScope.Resolve<IWorldState>();

        using (_stateProvider.BeginScope(IWorldState.PreGenesis))
        {
            _stateProvider.CreateAccount(_sender, 1_000_000.Ether());

            // Deploy contract for ContractCall benchmarks
            _stateProvider.CreateAccount(TestItem.AddressB, UInt256.Zero);
            _stateProvider.InsertCode(TestItem.AddressB, ContractCode, Spec);

            // Deploy system contract stubs required by Osaka execution requests
            _stateProvider.CreateAccount(Eip7002Constants.WithdrawalRequestPredeployAddress, UInt256.Zero);
            _stateProvider.InsertCode(Eip7002Constants.WithdrawalRequestPredeployAddress, StopCode, Spec);
            _stateProvider.CreateAccount(Eip7251Constants.ConsolidationRequestPredeployAddress, UInt256.Zero);
            _stateProvider.InsertCode(Eip7251Constants.ConsolidationRequestPredeployAddress, StopCode, Spec);

            _stateProvider.Commit(Spec);
            _stateProvider.CommitTree(0);

            _parentHeader = Build.A.BlockHeader
                .WithNumber(0)
                .WithStateRoot(_stateProvider.StateRoot)
                .WithGasLimit(30_000_000)
                .TestObject;
        }

        _branchProcessor = _processingScope.Resolve<IBranchProcessor>();
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _processingScope?.Dispose();
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
