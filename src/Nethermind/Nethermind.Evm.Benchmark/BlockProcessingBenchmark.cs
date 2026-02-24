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
using Nethermind.Core.Test.Modules;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Evm.Benchmark;

/// <summary>
/// Block-level processing benchmark measuring <see cref="BlockProcessor.ProcessOne"/>
/// with mainnet-like block scenarios under <see cref="Osaka"/> rules.
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
    /// [IterationSetup] rebuilds world state once per iteration.
    /// InvocationCount=1/UnrollFactor=1 ensures each invocation starts from a fresh state.
    /// </summary>
    private class BlockProcessingConfig : ManualConfig
    {
        public BlockProcessingConfig()
        {
            AddJob(Job.MediumRun
                .WithToolchain(InProcessNoEmitToolchain.Instance)
                .WithInvocationCount(1)
                .WithUnrollFactor(1));
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
    private IDisposable? _stateScope;
    private ILifetimeScope _processingScope = null!;
    private IBlockProcessor _processor = null!;

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
        // IWorldState is overridden per iteration in IterationSetup.
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
        // Dispose previous iteration's state
        _processingScope?.Dispose();
        _stateScope?.Dispose();

        // Fresh world state for this iteration
        _stateProvider = TestWorldStateFactory.CreateForTest();
        _stateScope = _stateProvider.BeginScope(IWorldState.PreGenesis);

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

        // Create a new lifetime scope with the fresh IWorldState injected.
        // All scoped components (EthereumTransactionProcessor, BlockProcessor, etc.)
        // are resolved fresh within this scope, matching the BlockProcessingModule wiring.
        // IBlockValidationModule[] (StandardBlockValidationModule) provides the
        // IBlockTransactionsExecutor and ITransactionProcessorAdapter registrations
        // that BlockProcessor requires — mirroring MainProcessingContext.
        IBlockValidationModule[] validationModules = _container.Resolve<IBlockValidationModule[]>();
        _processingScope = _container.BeginLifetimeScope(b =>
        {
            b.RegisterInstance(_stateProvider).As<IWorldState>().ExternallyOwned();
            b.AddModule(validationModules);
        });

        _processor = _processingScope.Resolve<IBlockProcessor>();
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _processingScope?.Dispose();
        _stateScope?.Dispose();
        _processingScope = null!;
        _stateScope = null;
    }

    // ── Benchmarks ────────────────────────────────────────────────────────

    [Benchmark]
    public (Block, TxReceipt[]) EmptyBlock()
        => _processor.ProcessOne(_emptyBlock, ProcessingOptions.NoValidation,
            NullBlockTracer.Instance, Spec, CancellationToken.None);

    [Benchmark]
    public (Block, TxReceipt[]) SingleTransfer()
        => _processor.ProcessOne(_singleTransferBlock, ProcessingOptions.NoValidation,
            NullBlockTracer.Instance, Spec, CancellationToken.None);

    [Benchmark]
    public (Block, TxReceipt[]) Transfers_50()
        => _processor.ProcessOne(_transfers50Block, ProcessingOptions.NoValidation,
            NullBlockTracer.Instance, Spec, CancellationToken.None);

    [Benchmark(Baseline = true)]
    public (Block, TxReceipt[]) Transfers_200()
        => _processor.ProcessOne(_transfers200Block, ProcessingOptions.NoValidation,
            NullBlockTracer.Instance, Spec, CancellationToken.None);

    [Benchmark]
    public (Block, TxReceipt[]) Eip1559_200()
        => _processor.ProcessOne(_eip1559_200Block, ProcessingOptions.NoValidation,
            NullBlockTracer.Instance, Spec, CancellationToken.None);

    [Benchmark]
    public (Block, TxReceipt[]) AccessList_50()
        => _processor.ProcessOne(_accessList50Block, ProcessingOptions.NoValidation,
            NullBlockTracer.Instance, Spec, CancellationToken.None);

    [Benchmark]
    public (Block, TxReceipt[]) ContractDeploy_10()
        => _processor.ProcessOne(_contractDeploy10Block, ProcessingOptions.NoValidation,
            NullBlockTracer.Instance, Spec, CancellationToken.None);

    [Benchmark]
    public (Block, TxReceipt[]) ContractCall_200()
        => _processor.ProcessOne(_contractCall200Block, ProcessingOptions.NoValidation,
            NullBlockTracer.Instance, Spec, CancellationToken.None);

    [Benchmark]
    public (Block, TxReceipt[]) MixedBlock()
        => _processor.ProcessOne(_mixedBlock, ProcessingOptions.NoValidation,
            NullBlockTracer.Instance, Spec, CancellationToken.None);

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
