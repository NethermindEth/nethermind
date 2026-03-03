// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.Evm.Benchmark;

/// <summary>
/// Measures the per-call cost of the <c>Warmup()</c> path used by the
/// block cache pre-warmer. Compares against <c>CallAndRestore()</c> as a
/// reference baseline.
///
/// The <c>Warmup()</c> path uses <c>ExecutionOptions.Warmup | SkipValidation</c>
/// and should route through <see cref="SystemTransactionProcessor{TGasPolicy}"/>
/// which skips BuyGas, IncrementNonce, PayFees, PayValue, and PayRefund.
///
/// All benchmarks run under <see cref="Osaka"/> rules (latest fork).
/// </summary>
[Config(typeof(WarmupConfig))]
[MemoryDiagnoser]
[JsonExporterAttribute.FullCompressed]
public class WarmupBenchmark
{
    private class WarmupConfig : ManualConfig
    {
        public WarmupConfig()
        {
            AddJob(Job.MediumRun.WithToolchain(InProcessNoEmitToolchain.Instance));
            AddColumn(StatisticColumn.Min);
            AddColumn(StatisticColumn.Max);
            AddColumn(StatisticColumn.Median);
            AddColumn(StatisticColumn.P90);
            AddColumn(StatisticColumn.P95);
        }
    }

    private const int N = 1_000;

    private static readonly IReleaseSpec Spec = Osaka.Instance;
    private static readonly ISpecProvider SpecProvider = new TestSpecProvider(Osaka.Instance);

    private static readonly byte[] ContractCode = Prepare.EvmCode
        .PushData(0x01)
        .Op(Instruction.STOP)
        .Done;

    private static readonly AccessList SampleAccessList = new AccessList.Builder()
        .AddAddress(TestItem.AddressA)
        .AddStorage(UInt256.Zero)
        .AddStorage(UInt256.One)
        .AddAddress(TestItem.AddressB)
        .AddStorage(new UInt256(42))
        .Build();

    private IWorldState _stateProvider = null!;
    private IDisposable _stateScope = null!;
    private ITransactionProcessor _processor = null!;
    private BlockHeader _header = null!;

    private Transaction _simpleTx = null!;
    private Transaction _accessListTx = null!;
    private Transaction _eip1559Tx = null!;
    private Transaction _contractCallTx = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _stateProvider = TestWorldStateFactory.CreateForTest();
        _stateScope = _stateProvider.BeginScope(IWorldState.PreGenesis);

        _stateProvider.CreateAccount(TestItem.AddressA, 1_000_000_000.Ether());
        _stateProvider.CreateAccount(TestItem.AddressB, UInt256.Zero);
        _stateProvider.InsertCode(TestItem.AddressB, ContractCode, Spec);
        _stateProvider.Commit(Spec);
        _stateProvider.CommitTree(0);

        _header = Build.A.BlockHeader
            .WithNumber(1)
            .WithGasLimit(30_000_000)
            .WithBaseFee(10.GWei())
            .WithStateRoot(_stateProvider.StateRoot)
            .TestObject;

        EthereumCodeInfoRepository codeInfo = new(_stateProvider);
        EthereumVirtualMachine vm = new(
            new TestBlockhashProvider(),
            SpecProvider,
            LimboLogs.Instance);
        _processor = new EthereumTransactionProcessor(
            BlobBaseFeeCalculator.Instance,
            SpecProvider,
            _stateProvider,
            vm,
            codeInfo,
            LimboLogs.Instance);

        _simpleTx = Build.A.Transaction
            .WithTo(TestItem.AddressC)
            .WithValue(1.Ether())
            .WithGasLimit(50_000)
            .WithGasPrice(20.GWei())
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        _accessListTx = Build.A.Transaction
            .WithType(TxType.AccessList)
            .WithTo(TestItem.AddressC)
            .WithValue(1.Ether())
            .WithGasLimit(100_000)
            .WithGasPrice(20.GWei())
            .WithAccessList(SampleAccessList)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        _eip1559Tx = Build.A.Transaction
            .WithType(TxType.EIP1559)
            .WithTo(TestItem.AddressC)
            .WithValue(1.Ether())
            .WithGasLimit(100_000)
            .WithMaxFeePerGas(20.GWei())
            .WithMaxPriorityFeePerGas(2.GWei())
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        _contractCallTx = Build.A.Transaction
            .WithTo(TestItem.AddressB)
            .WithGasLimit(100_000)
            .WithGasPrice(20.GWei())
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        // Pre-warm all code paths
        _processor.SetBlockExecutionContext(_header);
        _processor.Warmup(_simpleTx, NullTxTracer.Instance);
        _processor.Warmup(_accessListTx, NullTxTracer.Instance);
        _processor.Warmup(_eip1559Tx, NullTxTracer.Instance);
        _processor.Warmup(_contractCallTx, NullTxTracer.Instance);
        _processor.CallAndRestore(_simpleTx, NullTxTracer.Instance);
        _processor.CallAndRestore(_accessListTx, NullTxTracer.Instance);
        _processor.CallAndRestore(_eip1559Tx, NullTxTracer.Instance);
        _processor.CallAndRestore(_contractCallTx, NullTxTracer.Instance);
    }

    [GlobalCleanup]
    public void GlobalCleanup() => _stateScope.Dispose();

    // ── Warmup path ────────────────────────────────────────────────────────

    /// <summary>Warmup: simple ETH transfer — baseline for warmup path.</summary>
    [Benchmark(Baseline = true, OperationsPerInvoke = N)]
    public TransactionResult Warmup_SimpleTx()
    {
        TransactionResult result = default;
        for (int i = 0; i < N; i++)
            result = _processor.Warmup(_simpleTx, NullTxTracer.Instance);
        return result;
    }

    /// <summary>Warmup: EIP-2930 access-list transaction.</summary>
    [Benchmark(OperationsPerInvoke = N)]
    public TransactionResult Warmup_AccessListTx()
    {
        TransactionResult result = default;
        for (int i = 0; i < N; i++)
            result = _processor.Warmup(_accessListTx, NullTxTracer.Instance);
        return result;
    }

    /// <summary>Warmup: EIP-1559 type-2 transaction.</summary>
    [Benchmark(OperationsPerInvoke = N)]
    public TransactionResult Warmup_Eip1559Tx()
    {
        TransactionResult result = default;
        for (int i = 0; i < N; i++)
            result = _processor.Warmup(_eip1559Tx, NullTxTracer.Instance);
        return result;
    }

    /// <summary>Warmup: contract call into deployed code.</summary>
    [Benchmark(OperationsPerInvoke = N)]
    public TransactionResult Warmup_ContractCall()
    {
        TransactionResult result = default;
        for (int i = 0; i < N; i++)
            result = _processor.Warmup(_contractCallTx, NullTxTracer.Instance);
        return result;
    }

    // ── CallAndRestore path (reference — unaffected by warmup routing) ──

    /// <summary>CallAndRestore: simple ETH transfer — reference comparison.</summary>
    [Benchmark(OperationsPerInvoke = N)]
    public TransactionResult CallAndRestore_SimpleTx()
    {
        TransactionResult result = default;
        for (int i = 0; i < N; i++)
            result = _processor.CallAndRestore(_simpleTx, NullTxTracer.Instance);
        return result;
    }

    /// <summary>CallAndRestore: contract call — reference comparison.</summary>
    [Benchmark(OperationsPerInvoke = N)]
    public TransactionResult CallAndRestore_ContractCall()
    {
        TransactionResult result = default;
        for (int i = 0; i < N; i++)
            result = _processor.CallAndRestore(_contractCallTx, NullTxTracer.Instance);
        return result;
    }
}
