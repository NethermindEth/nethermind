// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;

namespace Nethermind.Evm.Benchmark;

/// <summary>
/// Measures one call frame cycle (call setup, child frame entry, return and resume) per operation, over 1024
/// calls in one transaction, so the per-transaction harness cost is spread thin enough to resolve small changes.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(launchCount: 3, warmupCount: 10, iterationCount: 30)]
public class CallFrameCycleBenchmarks
{
    private const int CallsPerTransaction = 1024;
    private const int WorkloadWarmupTransactions = 20_000;
    private const int OpcodeRefreshTransactions = 500_000;

    private static readonly Address _returningCallee = new("0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
    private static readonly Address _stoppingCallee = new("0xcccccccccccccccccccccccccccccccccccccccc");

    private static readonly byte[] StoppingCalleeCode = [(byte)Instruction.PUSH0, (byte)Instruction.POP, (byte)Instruction.STOP];
    private static readonly byte[] StopCode = [(byte)Instruction.STOP];

    private readonly IReleaseSpec _spec = MainnetSpecProvider.Instance.GetSpec(MainnetSpecProvider.OsakaActivation);
    private readonly BlockHeader _header = new(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.One, MainnetSpecProvider.IstanbulBlockNumber, Int64.MaxValue, 1UL, Bytes.Empty);

    private IVirtualMachine _virtualMachine = null!;
    private IWorldState _stateProvider = null!;
    private IDisposable _stateScope = null!;
    private CodeInfo _code = null!;
    private CodeInfo _stopCode = null!;

    [Params("StaticCallReturn", "CallStop", "DelegateCallReturn")]
    public string Shape { get; set; } = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _stateProvider = TestWorldStateFactory.CreateForTest(logManager: NullLogManager.Instance);
        _stateScope = _stateProvider.BeginScope(IWorldState.PreGenesis);
        _stateProvider.CreateAccount(Address.Zero, 1000.Ether);
        _stateProvider.CreateAccount(_returningCallee, 1.Ether);
        _stateProvider.InsertCode(_returningCallee, BuildReturningCalleeCode(), _spec);
        _stateProvider.CreateAccount(_stoppingCallee, 1.Ether);
        _stateProvider.InsertCode(_stoppingCallee, StoppingCalleeCode, _spec);
        _stateProvider.Commit(_spec);

        EthereumCodeInfoRepository codeInfoRepository = new(_stateProvider);
        _virtualMachine = new EthereumVirtualMachine(new TestBlockhashProvider(), MainnetSpecProvider.Instance, new OneLoggerLogManager(NullLogger.Instance));
        _virtualMachine.SetBlockExecutionContext(new BlockExecutionContext(_header, _spec));
        _virtualMachine.SetTxExecutionContext(new TxExecutionContext(Address.Zero, codeInfoRepository, null, 0));

        _code = new CodeInfo(BuildCallerCode(Shape));
        _stopCode = new CodeInfo(StopCode);

        for (int i = 0; i < WorkloadWarmupTransactions; i++)
        {
            Execute(_code);
        }

        // Advance the periodic opcode-table refreshes so none lands in the measurement.
        for (int i = WorkloadWarmupTransactions; i < OpcodeRefreshTransactions; i++)
        {
            Execute(_stopCode);
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup() => _stateScope.Dispose();

    [Benchmark(OperationsPerInvoke = CallsPerTransaction)]
    public void FrameCycle() => Execute(_code);

    private void Execute(CodeInfo codeInfo)
    {
        using ExecutionEnvironment environment = ExecutionEnvironment.Rent(
            executingAccount: Address.Zero,
            codeSource: Address.Zero,
            caller: Address.Zero,
            codeInfo: codeInfo,
            callDepth: 0,
            value: 0,
            inputData: default);

        using (VmState<EthereumGasPolicy> vmState = VmState<EthereumGasPolicy>.RentTopLevel(
            EthereumGasPolicy.FromULong(100_000_000),
            ExecutionType.TRANSACTION,
            environment,
            new StackAccessTracker(),
            _stateProvider.TakeSnapshot()))
        {
            _virtualMachine.ExecuteTransaction<OffFlag>(vmState, _stateProvider, NullTxTracer.Instance);
        }

        _stateProvider.Reset();
    }

    private static byte[] BuildReturningCalleeCode() => Prepare.EvmCode
        .PushData(42)
        .PushData(0)
        .Op(Instruction.MSTORE)
        .PushData(32)
        .PushData(0)
        .Op(Instruction.RETURN)
        .Done;

    private static byte[] BuildCallerCode(string shape)
    {
        Prepare code = Prepare.EvmCode;
        for (int i = 0; i < CallsPerTransaction; i++)
        {
            code = shape switch
            {
                "StaticCallReturn" => code.StaticCall(_returningCallee, 50_000),
                "CallStop" => code.Call(_stoppingCallee, 50_000),
                "DelegateCallReturn" => code.DelegateCall(_returningCallee, 50_000),
                _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
            };
            code = code.Op(Instruction.POP);
        }

        return code.Op(Instruction.STOP).Done;
    }
}
