// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Core;
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
/// Taken from PR #10514 (intrinsic-gas cache PR).
/// </summary>
[MemoryDiagnoser]
public class TxProcessingBenchmark
{
    private const int N = 1_000;

    private static readonly IReleaseSpec BerlinSpec = Berlin.Instance;
    private static readonly byte[] ContractCode = Prepare.EvmCode
        .PushData(0x01)
        .Op(Instruction.STOP)
        .Done;
    private static readonly byte[] MixedData = CreateMixedData();

    private IWorldState _stateProvider = null!;
    private IDisposable _stateScope = null!;
    private ITransactionProcessor _processor = null!;
    private BlockHeader _header = null!;
    private Transaction _simpleTx = null!;
    private Transaction _dataTx = null!;
    private Transaction _contractCallTx = null!;

    private static byte[] CreateMixedData()
    {
        byte[] data = new byte[128];
        for (int i = 0; i < data.Length; i++)
            data[i] = i % 4 == 0 ? (byte)0 : (byte)(i & 0xFF);
        return data;
    }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _stateProvider = TestWorldStateFactory.CreateForTest();
        _stateScope = _stateProvider.BeginScope(IWorldState.PreGenesis);

        _stateProvider.CreateAccount(TestItem.AddressA, 1000.Ether());
        _stateProvider.CreateAccount(TestItem.AddressB, UInt256.Zero);
        _stateProvider.InsertCode(TestItem.AddressB, ContractCode, BerlinSpec);
        _stateProvider.Commit(BerlinSpec);
        _stateProvider.CommitTree(0);

        _header = Build.A.BlockHeader
            .WithNumber(10_000_000)
            .WithGasLimit(10_000_000)
            .WithBaseFee(1.GWei())
            .WithStateRoot(_stateProvider.StateRoot)
            .TestObject;

        EthereumCodeInfoRepository codeInfo = new(_stateProvider);
        EthereumVirtualMachine vm = new(
            new TestBlockhashProvider(),
            MainnetSpecProvider.Instance,
            LimboLogs.Instance);
        _processor = new EthereumTransactionProcessor(
            BlobBaseFeeCalculator.Instance,
            MainnetSpecProvider.Instance,
            _stateProvider,
            vm,
            codeInfo,
            LimboLogs.Instance);

        // Simple ETH transfer — 21 000 intrinsic gas
        _simpleTx = Build.A.Transaction
            .WithTo(TestItem.AddressC)
            .WithValue(1.Ether())
            .WithGasLimit(50_000)
            .WithGasPrice(2.GWei())
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        // Transfer with 128 bytes of mixed data
        _dataTx = Build.A.Transaction
            .WithTo(TestItem.AddressC)
            .WithData(MixedData)
            .WithGasLimit(100_000)
            .WithGasPrice(2.GWei())
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        // Call to deployed contract
        _contractCallTx = Build.A.Transaction
            .WithTo(TestItem.AddressB)
            .WithGasLimit(100_000)
            .WithGasPrice(2.GWei())
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        _processor.SetBlockExecutionContext(_header);

        // Pre-warm
        _processor.CallAndRestore(_simpleTx, NullTxTracer.Instance);
        _processor.CallAndRestore(_dataTx, NullTxTracer.Instance);
        _processor.CallAndRestore(_contractCallTx, NullTxTracer.Instance);
    }

    [GlobalCleanup]
    public void GlobalCleanup() => _stateScope.Dispose();

    [Benchmark(Baseline = true, OperationsPerInvoke = N)]
    public TransactionResult SimpleTx()
    {
        TransactionResult result = default;
        for (int i = 0; i < N; i++)
            result = _processor.CallAndRestore(_simpleTx, NullTxTracer.Instance);
        return result;
    }

    [Benchmark(OperationsPerInvoke = N)]
    public TransactionResult DataTx()
    {
        TransactionResult result = default;
        for (int i = 0; i < N; i++)
            result = _processor.CallAndRestore(_dataTx, NullTxTracer.Instance);
        return result;
    }

    [Benchmark(OperationsPerInvoke = N)]
    public TransactionResult ContractCall()
    {
        TransactionResult result = default;
        for (int i = 0; i < N; i++)
            result = _processor.CallAndRestore(_contractCallTx, NullTxTracer.Instance);
        return result;
    }
}
