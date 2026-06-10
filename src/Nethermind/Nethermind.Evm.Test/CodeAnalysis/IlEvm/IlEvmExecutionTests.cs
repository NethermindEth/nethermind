// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.CodeAnalysis.IlEvm;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test.CodeAnalysis.IlEvm;

/// <summary>
/// End-to-end differential tests: the same bytecode executed through the real
/// <see cref="EthereumVirtualMachine"/> with IL-EVM off and on must produce identical output
/// and gas. Toggles the global <see cref="IlEvm"/> switches, hence non-parallelizable.
/// </summary>
[TestFixture]
[NonParallelizable]
public class IlEvmExecutionTests
{
    private bool _enabledBackup;
    private int _thresholdBackup;
    private bool _syncBackup;
    private int _minimumOpsBackup;
    private int _boundaryFactorBackup;

    [SetUp]
    public void SetUp()
    {
        _enabledBackup = Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.Enabled;
        _thresholdBackup = Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.CompileThreshold;
        _syncBackup = Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.SynchronousCompilation;
        _minimumOpsBackup = IlSegmentCompiler.MinimumPrefixOps;
        _boundaryFactorBackup = IlSegmentCompiler.BoundaryCostFactor;
        // Deterministic compiled-path coverage: compile on the executing thread, and let the
        // short test segments through the production profitability gate.
        Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.SynchronousCompilation = true;
        IlSegmentCompiler.MinimumPrefixOps = 1;
        IlSegmentCompiler.BoundaryCostFactor = 0;
    }

    [TearDown]
    public void TearDown()
    {
        Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.Enabled = _enabledBackup;
        Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.CompileThreshold = _thresholdBackup;
        Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.SynchronousCompilation = _syncBackup;
        IlSegmentCompiler.MinimumPrefixOps = _minimumOpsBackup;
        IlSegmentCompiler.BoundaryCostFactor = _boundaryFactorBackup;
    }

    [Test]
    public void Execute_LoopingArithmeticWithIlEvm_MatchesInterpreterExactly()
    {
        byte[] code = BuildSumLoopCode();

        Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.Enabled = false;
        ExecutionResult interpreted = RunCode(code, gasLimit: 100_000, out _);

        Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.Enabled = true;
        Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.CompileThreshold = 1;
        ExecutionResult compiled = RunCode(code, gasLimit: 100_000, out CodeInfo codeInfo);

        Assert.That(interpreted.IsError, Is.False, "precondition: the interpreter run must succeed");
        UInt256 expected = 15; // 5 + 4 + 3 + 2 + 1
        Assert.That(new UInt256(interpreted.Output, isBigEndian: true), Is.EqualTo(expected), "precondition: the loop sums 5..1");

        IlCompiledCode? artifact = Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.GetForExecution(codeInfo, IstanbulSpec);
        Assert.That(artifact, Is.Not.Null, "the compiled artifact must be published at threshold 1");
        Assert.That(artifact!.SegmentCount, Is.GreaterThanOrEqualTo(2), "the loop body and tail must both compile");

        Assert.That(compiled.IsError, Is.EqualTo(interpreted.IsError), "error outcome must match the interpreter");
        Assert.That(compiled.Output, Is.EqualTo(interpreted.Output), "output must match the interpreter");
        Assert.That(compiled.GasLeft, Is.EqualTo(interpreted.GasLeft), "gas accounting must match the interpreter exactly");
    }

    [Test]
    public void Execute_MemoryAndKeccakInsideSegment_MatchesInterpreterExactly()
    {
        // Stays one segment end to end: arithmetic, MSTORE, MLOAD, KECCAK256, arithmetic —
        // memory and keccak run as embedded handler calls, not segment cuts.
        byte[] code =
        [
            (byte)Instruction.PUSH1, 20,
            (byte)Instruction.PUSH1, 22,
            (byte)Instruction.ADD,            // 42
            (byte)Instruction.PUSH1, 0,
            (byte)Instruction.MSTORE,         // mem[0..32) = 42
            (byte)Instruction.PUSH1, 0,
            (byte)Instruction.MLOAD,          // 42 back
            (byte)Instruction.PUSH1, 32,
            (byte)Instruction.PUSH1, 0,
            (byte)Instruction.KECCAK256,      // keccak(mem[0..32))
            (byte)Instruction.ADD,            // 42 + hash (wraps)
            (byte)Instruction.PUSH1, 0,
            (byte)Instruction.MSTORE,
            (byte)Instruction.PUSH1, 32,
            (byte)Instruction.PUSH1, 0,
            (byte)Instruction.RETURN,
        ];

        Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.Enabled = false;
        ExecutionResult interpreted = RunCode(code, gasLimit: 100_000, out _);

        Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.Enabled = true;
        Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.CompileThreshold = 1;
        ExecutionResult compiled = RunCode(code, gasLimit: 100_000, out CodeInfo codeInfo);

        Assert.That(interpreted.IsError, Is.False, "precondition: the interpreter run must succeed");
        IlCompiledCode? artifact = Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.GetForExecution(codeInfo, IstanbulSpec);
        Assert.That(artifact, Is.Not.Null, "the code must compile");
        Assert.That(compiled.Output, Is.EqualTo(interpreted.Output), "memory/keccak handler calls must produce identical output");
        Assert.That(compiled.GasLeft, Is.EqualTo(interpreted.GasLeft), "chunked static charges plus handler dynamic gas must match per-op accounting");
    }

    [Test]
    public void Execute_MemoryExpansionOutOfGasInsideSegment_MatchesInterpreterHalt()
    {
        byte[] code =
        [
            (byte)Instruction.PUSH1, 1,
            (byte)Instruction.PUSH1, 2,
            (byte)Instruction.ADD,
            (byte)Instruction.PUSH4, 0x7F, 0xFF, 0xFF, 0xFF, // huge offset
            (byte)Instruction.MSTORE,                        // expansion cost far beyond gas
            (byte)Instruction.PUSH1, 1,
            (byte)Instruction.ADD,
            (byte)Instruction.STOP,
        ];

        Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.Enabled = false;
        ExecutionResult interpreted = RunCode(code, gasLimit: 50_000, out _);

        Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.Enabled = true;
        Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.CompileThreshold = 1;
        ExecutionResult compiled = RunCode(code, gasLimit: 50_000, out _);

        Assert.That(interpreted.IsError, Is.True, "precondition: the expansion must exhaust gas");
        Assert.That(compiled.IsError, Is.EqualTo(interpreted.IsError), "the mid-segment handler halt must match");
        Assert.That(compiled.GasLeft, Is.EqualTo(interpreted.GasLeft), "halt-time gas must match exactly: chunks before the handler equal the interpreter's cumulative charge");
    }

    [Test]
    public void Execute_OutOfGasMidLoop_MatchesInterpreterHaltExactly()
    {
        byte[] code = BuildSumLoopCode();

        Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.Enabled = false;
        ExecutionResult interpreted = RunCode(code, gasLimit: 60, out _); // dies inside the loop

        Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.Enabled = true;
        Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.CompileThreshold = 1;
        ExecutionResult compiled = RunCode(code, gasLimit: 60, out _);

        Assert.That(interpreted.IsError, Is.True, "precondition: 60 gas is not enough for the loop");
        Assert.That(compiled.IsError, Is.EqualTo(interpreted.IsError), "the out-of-gas outcome must match");
        Assert.That(compiled.GasLeft, Is.EqualTo(interpreted.GasLeft), "the gas-precondition fallback must reproduce exact per-opcode accounting");
        Assert.That(compiled.Output, Is.EqualTo(interpreted.Output), "output must match on failure too");
    }

    [Test]
    public void Execute_SecondRunOnSameCodeInfo_ReusesThePublishedArtifact()
    {
        byte[] code = BuildSumLoopCode();
        Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.Enabled = true;
        Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.CompileThreshold = 1;

        CodeInfo codeInfo = new(code);
        ExecutionResult first = RunCodeWith(codeInfo, code, gasLimit: 100_000);
        IlCompiledCode? artifactAfterFirst = Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.GetForExecution(codeInfo, IstanbulSpec);
        ExecutionResult second = RunCodeWith(codeInfo, code, gasLimit: 100_000);
        IlCompiledCode? artifactAfterSecond = Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.GetForExecution(codeInfo, IstanbulSpec);

        Assert.That(artifactAfterFirst, Is.Not.Null, "precondition: the first run compiles");
        Assert.That(artifactAfterSecond, Is.SameAs(artifactAfterFirst), "the artifact must be compiled once and reused");
        Assert.That(second.Output, Is.EqualTo(first.Output), "repeated runs must agree");
        Assert.That(second.GasLeft, Is.EqualTo(first.GasLeft), "repeated runs must consume identical gas");
    }

    private static IReleaseSpec IstanbulSpec => MainnetSpecProvider.Instance.GetSpec((ForkActivation)MainnetSpecProvider.IstanbulBlockNumber);

    /// <summary>
    /// Sums 5+4+3+2+1 in a loop and returns the 32-byte result. The loop head and body become
    /// compiled segments; JUMP/JUMPI/MSTORE/RETURN stay interpreted, exercising re-entry and
    /// segment-to-interpreter bailouts on every iteration.
    /// </summary>
    private static byte[] BuildSumLoopCode() =>
    [
        (byte)Instruction.PUSH1, 0,     // pc 0:  acc = 0
        (byte)Instruction.PUSH1, 5,     // pc 2:  i = 5
        (byte)Instruction.JUMPDEST,     // pc 4:  loop: [acc, i]
        (byte)Instruction.DUP1,         // pc 5:  [acc, i, i]
        (byte)Instruction.ISZERO,       // pc 6:  [acc, i, i==0]
        (byte)Instruction.PUSH1, 21,    // pc 7:  [acc, i, i==0, end]
        (byte)Instruction.JUMPI,        // pc 9:  [acc, i]
        (byte)Instruction.DUP1,         // pc 10: [acc, i, i]
        (byte)Instruction.SWAP2,        // pc 11: [i, i, acc]
        (byte)Instruction.ADD,          // pc 12: [i, acc+i]
        (byte)Instruction.SWAP1,        // pc 13: [acc', i]
        (byte)Instruction.PUSH1, 1,     // pc 14: [acc', i, 1]
        (byte)Instruction.SWAP1,        // pc 16: [acc', 1, i]
        (byte)Instruction.SUB,          // pc 17: [acc', i-1]
        (byte)Instruction.PUSH1, 4,     // pc 18: [acc', i-1, loop]
        (byte)Instruction.JUMP,         // pc 20
        (byte)Instruction.JUMPDEST,     // pc 21: end: [acc, 0]
        (byte)Instruction.POP,          // pc 22: [acc]
        (byte)Instruction.PUSH1, 0,     // pc 23: [acc, 0]
        (byte)Instruction.MSTORE,       // pc 25: mem[0..32) = acc
        (byte)Instruction.PUSH1, 32,    // pc 26
        (byte)Instruction.PUSH1, 0,     // pc 28
        (byte)Instruction.RETURN,       // pc 30
    ];

    private static ExecutionResult RunCode(byte[] code, long gasLimit, out CodeInfo codeInfo)
    {
        codeInfo = new CodeInfo(code);
        return RunCodeWith(codeInfo, code, gasLimit);
    }

    private static ExecutionResult RunCodeWith(CodeInfo codeInfo, byte[] code, long gasLimit)
    {
        IReleaseSpec spec = IstanbulSpec;
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using IDisposable worldStateScope = stateProvider.BeginScope(IWorldState.PreGenesis);
        stateProvider.CreateAccount(Address.Zero, 1000.Ether);
        stateProvider.Commit(spec);

        EthereumCodeInfoRepository codeInfoRepository = new(stateProvider);
        EthereumVirtualMachine virtualMachine = new(new TestBlockhashProvider(MainnetSpecProvider.Instance), MainnetSpecProvider.Instance, LimboLogs.Instance);
        BlockHeader header = new(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.One, MainnetSpecProvider.IstanbulBlockNumber, long.MaxValue, 1UL, Bytes.Empty);
        virtualMachine.SetBlockExecutionContext(new BlockExecutionContext(header, spec));
        virtualMachine.SetTxExecutionContext(new TxExecutionContext(Address.Zero, codeInfoRepository, null, 0));

        using ExecutionEnvironment environment = ExecutionEnvironment.Rent(
            executingAccount: Address.Zero,
            codeSource: Address.Zero,
            caller: Address.Zero,
            codeInfo: codeInfo,
            callDepth: 0,
            value: 0,
            inputData: default);

        using VmState<EthereumGasPolicy> evmState = VmState<EthereumGasPolicy>.RentTopLevel(
            EthereumGasPolicy.FromLong(gasLimit), ExecutionType.TRANSACTION, environment, new StackAccessTracker(), stateProvider.TakeSnapshot());

        TransactionSubstate substate = virtualMachine.ExecuteTransaction<OffFlag>(evmState, stateProvider, NullTxTracer.Instance);
        EthereumGasPolicy gasState = evmState.Gas;
        return new ExecutionResult(substate.Output.ToArray(), EthereumGasPolicy.GetRemainingGas(in gasState), substate.IsError);
    }

    private sealed record ExecutionResult(byte[] Output, long GasLeft, bool IsError);
}
