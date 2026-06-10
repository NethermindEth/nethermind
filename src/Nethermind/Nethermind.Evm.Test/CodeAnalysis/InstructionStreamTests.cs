// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test.CodeAnalysis;

[TestFixture]
public class InstructionStreamTests
{
    [Test]
    public void TryBuild_StraightLineArithmetic_SumsTheBlockGasOnce()
    {
        byte[] code =
        [
            (byte)Instruction.PUSH1, 0x01,
            (byte)Instruction.PUSH1, 0x02,
            (byte)Instruction.ADD,
            (byte)Instruction.STOP,
        ];

        InstructionStream stream = InstructionStream.TryBuild(code)!;

        Assert.That(stream, Is.Not.Null);
        Assert.That(stream.BlockGas, Has.Length.EqualTo(1));
        Assert.That(stream.BlockGas[0], Is.EqualTo(3 * GasCostOf.VeryLow),
            "two pushes and an add are one block charged as a single sum");
        Assert.That(stream.Ops[0].Kind, Is.EqualTo(StreamOpKind.BlockStart));
        Assert.That(stream.Ops[^1].Kind, Is.EqualTo(StreamOpKind.Boundary),
            "STOP is not a static-cost op and must run the standard handler");
    }

    [Test]
    public void TryBuild_Jumpdest_GetsItsOwnSoloBlock()
    {
        byte[] code =
        [
            (byte)Instruction.JUMPDEST,
            (byte)Instruction.PUSH1, 0x01,
            (byte)Instruction.POP,
        ];

        InstructionStream stream = InstructionStream.TryBuild(code)!;

        Assert.That(stream.BlockGas, Has.Length.EqualTo(2),
            "a fused PUSH2+JUMP lands one past the JUMPDEST, so the ops after it need a separately charged block");
        Assert.That(stream.BlockGas[0], Is.EqualTo(GasCostOf.JumpDest));
        Assert.That(stream.BlockGas[1], Is.EqualTo(GasCostOf.VeryLow + GasCostOf.Base));
    }

    [Test]
    public void TryBuild_JumpAndPush2_AreJumpClass()
    {
        byte[] code =
        [
            (byte)Instruction.PUSH2, 0x00, 0x05,
            (byte)Instruction.JUMP,
            (byte)Instruction.STOP,
            (byte)Instruction.JUMPDEST,
        ];

        InstructionStream stream = InstructionStream.TryBuild(code)!;

        Assert.That(stream.Ops[0].Kind, Is.EqualTo(StreamOpKind.JumpClass), "PUSH2 keeps the fused PUSH2+JUMP handler");
        Assert.That(stream.Ops[1].Kind, Is.EqualTo(StreamOpKind.JumpClass), "JUMP recomputes the entry index from the landing pc");
    }

    [Test]
    public void TryBuild_PcToEntry_MapsInstructionStartsAndRejectsImmediates()
    {
        byte[] code =
        [
            (byte)Instruction.PUSH3, 0x01, 0x02, 0x03,
            (byte)Instruction.ADD,
        ];

        InstructionStream stream = InstructionStream.TryBuild(code)!;

        Assert.That(stream.PcToEntry[0], Is.EqualTo(0), "PUSH3 opens the block, so its pc maps to the BlockStart entry");
        Assert.That(stream.PcToEntry[1], Is.EqualTo(InstructionStream.InvalidEntry));
        Assert.That(stream.PcToEntry[2], Is.EqualTo(InstructionStream.InvalidEntry));
        Assert.That(stream.PcToEntry[3], Is.EqualTo(InstructionStream.InvalidEntry));
        Assert.That(stream.PcToEntry[4], Is.EqualTo(2), "ADD continues the open block as a plain entry");
        Assert.That(stream.PcToEntry[5], Is.EqualTo(stream.Ops.Length), "pc one past the end terminates the stream loop");
    }

    [Test]
    public void TryBuild_EmptyCode_ReturnsNull()
        => Assert.That(InstructionStream.TryBuild(ReadOnlySpan<byte>.Empty), Is.Null);

    [TestCase(Instruction.ADD, GasCostOf.VeryLow, TestName = "Add")]
    [TestCase(Instruction.MUL, GasCostOf.Low, TestName = "Mul")]
    [TestCase(Instruction.SDIV, GasCostOf.Low, TestName = "SDiv")]
    [TestCase(Instruction.POP, GasCostOf.Base, TestName = "Pop")]
    [TestCase(Instruction.PUSH0, GasCostOf.Base, TestName = "Push0")]
    [TestCase(Instruction.SHL, GasCostOf.VeryLow, TestName = "Shl")]
    [TestCase(Instruction.DUP8, GasCostOf.VeryLow, TestName = "Dup8")]
    [TestCase(Instruction.SWAP8, GasCostOf.VeryLow, TestName = "Swap8")]
    public void TryGetInBlockCost_ForStaticCostOp_MatchesGasCostOf(Instruction instruction, long expectedCost)
    {
        Assert.That(InstructionStream.TryGetInBlockCost(instruction, out long cost), Is.True);
        Assert.That(cost, Is.EqualTo(expectedCost), "block sums diverging from GasCostOf is a consensus bug");
    }

    [TestCase(Instruction.PUSH2, TestName = "Push2_IsJumpClassNotInBlock")]
    [TestCase(Instruction.DUP9, TestName = "Dup9_OutsideExecutorSwitch")]
    [TestCase(Instruction.SWAP9, TestName = "Swap9_OutsideExecutorSwitch")]
    [TestCase(Instruction.PUSH5, TestName = "Push5_OutsideExecutorSwitch")]
    [TestCase(Instruction.MLOAD, TestName = "MLoad_DynamicMemoryGas")]
    [TestCase(Instruction.SLOAD, TestName = "SLoad_DynamicAccessGas")]
    [TestCase(Instruction.GAS, TestName = "Gas_MustObserveExactRemaining")]
    [TestCase(Instruction.EXP, TestName = "Exp_DynamicGas")]
    public void TryGetInBlockCost_ForExcludedOp_ReturnsFalse(Instruction instruction)
        => Assert.That(InstructionStream.TryGetInBlockCost(instruction, out _), Is.False);
}

[TestFixture]
public class StreamInterpreterDifferentialTests : VirtualMachineTestsBase
{
    // The stream only engages on tip-fork dispatch fingerprints; the base default (Byzantium)
    // would silently compare the bytecode loop against itself.
    protected override ForkActivation Activation => MainnetSpecProvider.OsakaActivation;

    private static readonly byte[] s_arithmeticChain = Prepare.EvmCode
        .PushData(7).PushData(5).Op(Instruction.ADD)
        .PushData(3).Op(Instruction.MUL)
        .PushData(2).Op(Instruction.SWAP1).Op(Instruction.DIV)
        .Op(Instruction.DUP1).Op(Instruction.EQ)
        .Op(Instruction.ISZERO)
        .Op(Instruction.POP)
        .Op(Instruction.STOP)
        .Done;

    private static readonly byte[] s_jumpLoop = Prepare.EvmCode
        .PushData(5)                                  // counter
        .Op(Instruction.JUMPDEST)                     // pc 2: loop head
        .PushData(1).Op(Instruction.SWAP1).Op(Instruction.SUB)
        .Op(Instruction.DUP1)
        .PushData(2).Op(Instruction.JUMPI)            // loop while counter != 0
        .Op(Instruction.STOP)
        .Done;

    private static readonly byte[] s_storeAndReturn = Prepare.EvmCode
        .PushData(42).PushData(0).Op(Instruction.MSTORE)
        .PushData(32).PushData(0).Op(Instruction.RETURN)
        .Done;

    private static readonly byte[] s_stackUnderflow = Prepare.EvmCode
        .Op(Instruction.ADD)
        .Done;

    private static readonly byte[] s_nestedCallWithResume = Prepare.EvmCode
        .Call(CalleeAddress, 50000)
        .PushData(3).Op(Instruction.MUL)
        .PushData(0).Op(Instruction.MSTORE)
        .PushData(32).PushData(0).Op(Instruction.RETURN)
        .Done;

    private static readonly byte[] s_createWithInitCode = Prepare.EvmCode
        .Create(Prepare.EvmCode
            .PushData(1).PushData(2).Op(Instruction.ADD).Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done, 0)
        .Op(Instruction.POP)
        .Op(Instruction.STOP)
        .Done;

    // PUSH2 0x0007; JUMPI loop counting down — exercises the fused PUSH2+JUMPI handler and
    // the per-iteration recharge of the jump-target block.
    private static readonly byte[] s_fusedPush2JumpiLoop =
    [
        (byte)Instruction.PUSH1, 200,                  // counter
        (byte)Instruction.JUMPDEST,                    // pc 2
        (byte)Instruction.PUSH1, 1,
        (byte)Instruction.SWAP1,
        (byte)Instruction.SUB,
        (byte)Instruction.DUP1,
        (byte)Instruction.PUSH2, 0x00, 0x02,
        (byte)Instruction.JUMPI,
        (byte)Instruction.STOP,
    ];

    // Code ends inside PUSH2's immediates; the program counter runs past the end of code and
    // the stream must exit as cleanly as the bytecode loop does.
    private static readonly byte[] s_truncatedTrailingPush2 =
    [
        (byte)Instruction.PUSH1, 1,
        (byte)Instruction.PUSH2, 0x00,
    ];

    private static readonly byte[] s_deepStackToTheLimit = BuildDeepStackCode();

    private static byte[] BuildDeepStackCode()
    {
        // 1024 pushes fill the stack exactly to the limit; the 1025th must overflow.
        byte[] code = new byte[1025 + 1];
        code.AsSpan(0, 1025).Fill((byte)Instruction.PUSH0);
        code[1025] = (byte)Instruction.STOP;
        return code;
    }

    private static Address CalleeAddress => TestItem.AddressC;
    private static Address SolidityExampleAddress => TestItem.AddressE;

    // Runtime code of the Legacy stExample.solidityExample contract — the smallest real-world
    // reproducer of the mainnet stream divergence (CREATE of a child contract, then a CALL into
    // it, with fused PUSH2+JUMPI dispatchers throughout).
    private const string SolidityExampleRuntime =
        "608060405234801561001057600080fd5b506004361061002b5760003560e01c8063b66176a714610030575b600080fd5b61004a6004803603810190610045919061018d565b61004c565b005b60405161005890610145565b604051809103906000f080158015610074573d6000803e3d6000fd5b506000806101000a81548173ffffffffffffffffffffffffffffffffffffffff021916908373ffffffffffffffffffffffffffffffffffffffff16021790555060008054906101000a900473ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff1663b66176a783836040518363ffffffff1660e01b815260040161010f9291906101dc565b600060405180830381600087803b15801561012957600080fd5b505af115801561013d573d6000803e3d6000fd5b505050505050565b6101238061020683390190565b600080fd5b6000819050919050565b61016a81610157565b811461017557600080fd5b50565b60008135905061018781610161565b92915050565b600080604083850312156101a4576101a3610152565b5b60006101b285828601610178565b92505060206101c385828601610178565b9150509250929050565b6101d681610157565b82525050565b60006040820190506101f160008301856101cd565b6101fe60208301846101cd565b939250505056fe608060405267ff00ff00ff00ff0060005534801561001c57600080fd5b5060f88061002b6000396000f3fe6080604052348015600f57600080fd5b506004361060285760003560e01c8063b66176a714602d575b600080fd5b60436004803603810190603f91906089565b6045565b005b806000819055508082555050565b600080fd5b6000819050919050565b6069816058565b8114607357600080fd5b50565b6000813590506083816062565b92915050565b60008060408385031215609d57609c6053565b5b600060a9858286016076565b925050602060b8858286016076565b915050925092905056fea26469706673582212209beb73a466a9b6fcce247e8e1ec0ac303febcb2192064276aa2188d57d06a98d64736f6c63430008150033a2646970667358221220223335c3b4079496a81c6cbdfc0adb8a4b8ed0637499a9301f31c89383d238e164736f6c63430008150033";

    private static readonly byte[] s_solidityExampleCall = Prepare.EvmCode
        .CallWithInput(SolidityExampleAddress, 4_000_000,
            Core.Extensions.Bytes.FromHexString(
                "b66176a700000000000000000000000000000000000000000000000000000000000000050000000000000000000000000000000000000000000000000000000000000045"))
        .Op(Instruction.STOP)
        .Done;

    public static IEnumerable<TestCaseData> DifferentialCases()
    {
        yield return new TestCaseData(s_arithmeticChain) { TestName = "ArithmeticChain" };
        yield return new TestCaseData(s_jumpLoop) { TestName = "JumpLoopWithFusedPush" };
        yield return new TestCaseData(s_storeAndReturn) { TestName = "MemoryBoundaryOpsAndReturn" };
        yield return new TestCaseData(s_stackUnderflow) { TestName = "StackUnderflowFailure" };
        yield return new TestCaseData(s_nestedCallWithResume) { TestName = "NestedCallWithResume" };
        yield return new TestCaseData(s_createWithInitCode) { TestName = "CreateWithInitCode" };
        yield return new TestCaseData(s_fusedPush2JumpiLoop) { TestName = "FusedPush2JumpiLoop" };
        yield return new TestCaseData(s_truncatedTrailingPush2) { TestName = "TruncatedTrailingPush2" };
        yield return new TestCaseData(s_deepStackToTheLimit) { TestName = "DeepStackToTheLimit" };
        yield return new TestCaseData(s_solidityExampleCall) { TestName = "SolidityExampleCreateAndCall" };
    }

    [Test]
    public void Activation_WhenFingerprinted_SelectsASpecializedDispatch()
    {
        int fingerprint = EvmSpecFingerprint.Compute(SpecProvider.GetSpec(Activation));
        Assert.That(
            fingerprint == EvmSpecFingerprint.Compute<OsakaEvmSpec>()
            || fingerprint == EvmSpecFingerprint.Compute<CancunEvmSpec>(),
            "the differential fixture must run on a fork where the stream engages");
    }

    private static readonly byte[] s_calleeCode = Prepare.EvmCode
        .PushData(7).PushData(6).Op(Instruction.MUL)
        .PushData(0).Op(Instruction.MSTORE)
        .PushData(32).PushData(0).Op(Instruction.RETURN)
        .Done;

    [TestCaseSource(nameof(DifferentialCases))]
    public void StreamInterpreter_ComparedToByteCodeLoop_IsObservablyIdentical(byte[] code)
    {
        ReceiptCaptureTracer baseline = RunWithInterpreter(code, useStream: false);

        // Both runs commit state (storage writes, deployed contracts), so the stream run
        // gets a freshly built world or it would see the baseline's writes (SSET vs SRESET).
        Setup();

        long framesBefore = StreamInterpreter.FramesExecuted;
        ReceiptCaptureTracer streamed = RunWithInterpreter(code, useStream: true);

        Assert.That(StreamInterpreter.FramesExecuted, Is.GreaterThan(framesBefore),
            "the stream interpreter did not engage — this differential proved nothing");
        Assert.That(streamed.StatusCode, Is.EqualTo(baseline.StatusCode), "success/failure must match");
        Assert.That(streamed.GasSpent, Is.EqualTo(baseline.GasSpent), "gas must match to the unit");
        Assert.That(streamed.Output, Is.EqualTo(baseline.Output), "return data must match");
    }

    private ReceiptCaptureTracer RunWithInterpreter(byte[] code, bool useStream)
    {
        TestState.CreateAccount(CalleeAddress, 1000000);
        TestState.InsertCode(CalleeAddress, s_calleeCode, Spec);
        TestState.CreateAccount(SolidityExampleAddress, 1000000);
        TestState.InsertCode(SolidityExampleAddress, Core.Extensions.Bytes.FromHexString(SolidityExampleRuntime), Spec);

        bool enabledBefore = StreamInterpreter.Enabled;
        StreamInterpreter.Enabled = useStream;
        try
        {
            // The base Execute helper caps gas at 100k; the CREATE-heavy cases need more.
            (Block block, Transaction transaction) = PrepareTx(Activation, 8_000_000, code);
            ReceiptCaptureTracer tracer = new();
            _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
            return tracer;
        }
        finally
        {
            StreamInterpreter.Enabled = enabledBefore;
        }
    }

    private sealed class ReceiptCaptureTracer : Evm.Tracing.TxTracer
    {
        public byte StatusCode { get; private set; }
        public long GasSpent { get; private set; }
        public byte[] Output { get; private set; } = [];

        public override bool IsTracingReceipt => true;

        public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
        {
            StatusCode = Evm.StatusCode.Success;
            GasSpent = gasSpent.SpentGas;
            Output = output;
        }

        public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
        {
            StatusCode = Evm.StatusCode.Failure;
            GasSpent = gasSpent.SpentGas;
            Output = output;
        }
    }
}
