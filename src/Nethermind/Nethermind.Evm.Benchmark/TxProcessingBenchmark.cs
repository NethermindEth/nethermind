// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
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
/// Measures the per-call cost of full transaction processing via
/// <see cref="EthereumTransactionProcessor"/>. Uses <c>CallAndRestore</c> so
/// state is rolled back after each call, allowing 1 000 iterations per invocation.
///
/// Transaction types covered:
/// - Legacy ETH transfer (simple, data, zero-data, large-data)
/// - EIP-2930 access-list transaction (type 1)
/// - EIP-1559 type-2 transaction
/// - Contract deployment via CREATE (no recipient)
/// - Contract call into deployed code
///
/// All benchmarks run under <see cref="Osaka"/> rules (latest fork).
/// Based on PR #10514 (intrinsic-gas cache benchmark).
/// </summary>
[Config(typeof(TxProcessingConfig))]
[MemoryDiagnoser]
[JsonExporterAttribute.FullCompressed]
public class TxProcessingBenchmark
{
    private class TxProcessingConfig : ManualConfig
    {
        public TxProcessingConfig()
        {
            AddJob(Job.ShortRun.WithToolchain(InProcessNoEmitToolchain.Instance));
        }
    }

    private const int N = 1_000;

    private static readonly IReleaseSpec Spec = Osaka.Instance;
    private static readonly ISpecProvider SpecProvider = new TestSpecProvider(Osaka.Instance);

    private static readonly byte[] ContractCode = Prepare.EvmCode
        .PushData(0x01)
        .Op(Instruction.STOP)
        .Done;

    // 128 bytes: every 4th byte is zero, rest non-zero  (realistic calldata mix)
    private static readonly byte[] MixedData128 = CreateMixedData(128);

    // 128 bytes: all zeros (cheapest calldata per byte)
    private static readonly byte[] ZeroData128 = new byte[128];

    // 1024 bytes mixed (heavier calldata cost)
    private static readonly byte[] MixedData1024 = CreateMixedData(1024);

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

    // ── transaction instances ──────────────────────────────────────────────
    private Transaction _simpleTx = null!;  // 21 000 gas ETH transfer
    private Transaction _mixedDataTx = null!;  // transfer + 128 B mixed calldata
    private Transaction _zeroDataTx = null!;  // transfer + 128 B zero calldata
    private Transaction _largeDataTx = null!;  // transfer + 1024 B mixed calldata
    private Transaction _accessListTx = null!;  // EIP-2930: access-list tx
    private Transaction _eip1559Tx = null!;  // EIP-1559: type-2 tx
    private Transaction _contractCallTx = null!;  // call a deployed contract
    private Transaction _contractDeployTx = null!; // CREATE: deploy new contract

    private static byte[] CreateMixedData(int length)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < data.Length; i++)
            data[i] = i % 4 == 0 ? (byte)0 : (byte)(i & 0xFF);
        return data;
    }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _stateProvider = TestWorldStateFactory.CreateForTest();
        _stateScope = _stateProvider.BeginScope(IWorldState.PreGenesis);

        // Fund sender; deploy target contract
        _stateProvider.CreateAccount(TestItem.AddressA, 10_000.Ether());
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

        // ── build transactions ─────────────────────────────────────────────
        _simpleTx = Build.A.Transaction
            .WithTo(TestItem.AddressC)
            .WithValue(1.Ether())
            .WithGasLimit(50_000)
            .WithGasPrice(20.GWei())
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        _mixedDataTx = Build.A.Transaction
            .WithTo(TestItem.AddressC)
            .WithData(MixedData128)
            .WithGasLimit(100_000)
            .WithGasPrice(20.GWei())
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        _zeroDataTx = Build.A.Transaction
            .WithTo(TestItem.AddressC)
            .WithData(ZeroData128)
            .WithGasLimit(100_000)
            .WithGasPrice(20.GWei())
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        _largeDataTx = Build.A.Transaction
            .WithTo(TestItem.AddressC)
            .WithData(MixedData1024)
            .WithGasLimit(200_000)
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

        // CREATE: no recipient, init code in data
        _contractDeployTx = Build.A.Transaction
            .WithTo(null)
            .WithData(ContractCode)
            .WithGasLimit(200_000)
            .WithGasPrice(20.GWei())
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        // Pre-warm all paths
        _processor.SetBlockExecutionContext(_header);
        _processor.CallAndRestore(_simpleTx, NullTxTracer.Instance);
        _processor.CallAndRestore(_mixedDataTx, NullTxTracer.Instance);
        _processor.CallAndRestore(_zeroDataTx, NullTxTracer.Instance);
        _processor.CallAndRestore(_largeDataTx, NullTxTracer.Instance);
        _processor.CallAndRestore(_accessListTx, NullTxTracer.Instance);
        _processor.CallAndRestore(_eip1559Tx, NullTxTracer.Instance);
        _processor.CallAndRestore(_contractCallTx, NullTxTracer.Instance);
        _processor.CallAndRestore(_contractDeployTx, NullTxTracer.Instance);
    }

    [GlobalCleanup]
    public void GlobalCleanup() => _stateScope.Dispose();

    // ── Legacy transactions ────────────────────────────────────────────────

    /// <summary>Simple ETH transfer — 21 000 intrinsic gas, baseline.</summary>
    [Benchmark(Baseline = true, OperationsPerInvoke = N)]
    public TransactionResult SimpleTx()
    {
        TransactionResult result = default;
        for (int i = 0; i < N; i++)
            result = _processor.CallAndRestore(_simpleTx, NullTxTracer.Instance);
        return result;
    }

    /// <summary>Transfer + 128 B mixed (non-zero / zero) calldata.</summary>
    [Benchmark(OperationsPerInvoke = N)]
    public TransactionResult MixedDataTx()
    {
        TransactionResult result = default;
        for (int i = 0; i < N; i++)
            result = _processor.CallAndRestore(_mixedDataTx, NullTxTracer.Instance);
        return result;
    }

    /// <summary>Transfer + 128 B all-zero calldata (cheapest calldata path).</summary>
    [Benchmark(OperationsPerInvoke = N)]
    public TransactionResult ZeroDataTx()
    {
        TransactionResult result = default;
        for (int i = 0; i < N; i++)
            result = _processor.CallAndRestore(_zeroDataTx, NullTxTracer.Instance);
        return result;
    }

    /// <summary>Transfer + 1024 B mixed calldata (heavier intrinsic gas).</summary>
    [Benchmark(OperationsPerInvoke = N)]
    public TransactionResult LargeDataTx()
    {
        TransactionResult result = default;
        for (int i = 0; i < N; i++)
            result = _processor.CallAndRestore(_largeDataTx, NullTxTracer.Instance);
        return result;
    }

    // ── Typed transactions ─────────────────────────────────────────────────

    /// <summary>EIP-2930 access-list transaction (type 1, Berlin+).</summary>
    [Benchmark(OperationsPerInvoke = N)]
    public TransactionResult AccessListTx()
    {
        TransactionResult result = default;
        for (int i = 0; i < N; i++)
            result = _processor.CallAndRestore(_accessListTx, NullTxTracer.Instance);
        return result;
    }

    /// <summary>EIP-1559 type-2 transaction with max-fee / priority-fee (London+).</summary>
    [Benchmark(OperationsPerInvoke = N)]
    public TransactionResult Eip1559Tx()
    {
        TransactionResult result = default;
        for (int i = 0; i < N; i++)
            result = _processor.CallAndRestore(_eip1559Tx, NullTxTracer.Instance);
        return result;
    }

    // ── EVM-executing transactions ─────────────────────────────────────────

    /// <summary>Call into a deployed contract (EVM execution path).</summary>
    [Benchmark(OperationsPerInvoke = N)]
    public TransactionResult ContractCall()
    {
        TransactionResult result = default;
        for (int i = 0; i < N; i++)
            result = _processor.CallAndRestore(_contractCallTx, NullTxTracer.Instance);
        return result;
    }

    /// <summary>Contract deployment via CREATE (init-code execution path).</summary>
    [Benchmark(OperationsPerInvoke = N)]
    public TransactionResult ContractDeploy()
    {
        TransactionResult result = default;
        for (int i = 0; i < N; i++)
            result = _processor.CallAndRestore(_contractDeployTx, NullTxTracer.Instance);
        return result;
    }
}
