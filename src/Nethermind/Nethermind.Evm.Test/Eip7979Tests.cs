// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Tests for EIP-7979: call and return opcodes (CALLSUB, CALLDEST, RETURNSUB).
/// </summary>
/// <remarks>Runs untraced and traced, since only the untraced table fuses the landed-on marker into the jump.</remarks>
[TestFixture(false)]
[TestFixture(true)]
public class Eip7979Tests(bool traceInstructions) : VirtualMachineTestsBase
{
    private const ulong GasLimit = 100_000;

    private const ulong DisabledTimestamp = 1;

    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => DisabledTimestamp + 1;

    private ForkActivation Disabled => (BlockNumber, DisabledTimestamp);

    protected override ISpecProvider SpecProvider => new CustomSpecProvider(
        ((ForkActivation)0, Bogota.Instance),
        (Activation, new Bogota { IsEip7979Enabled = true }));

    /// <summary>Maps the EIP's placeholder opcodes 0xb0-0xb2 in <paramref name="hex"/> to this implementation's values.</summary>
    private static byte[] FromEipVector(string hex)
    {
        byte[] code = Bytes.FromHexString(hex);
        for (int pc = 0; pc < code.Length; pc++)
        {
            byte op = code[pc];
            if (op is >= (byte)Instruction.PUSH1 and <= (byte)Instruction.PUSH32)
            {
                pc += op - (byte)Instruction.PUSH1 + 1;
                continue;
            }

            code[pc] = op switch
            {
                0xb0 => (byte)Instruction.CALLSUB,
                0xb1 => (byte)Instruction.CALLDEST,
                0xb2 => (byte)Instruction.RETURNSUB,
                _ => op,
            };
        }

        return code;
    }

    private TestAllTracerWithOutput Run(byte[] code, ForkActivation? activation = null, ulong gasLimit = GasLimit)
    {
        (Block block, Transaction transaction) = PrepareTx(activation ?? Activation, gasLimit, code);
        TestAllTracerWithOutput tracer = traceInstructions ? new TestAllTracerWithOutput() : new UntracedInstructionsTracer();
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
        return tracer;
    }

    private static void AssertSuccess(TestAllTracerWithOutput result, ulong executionGas)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success), result.Error);
            Assert.That(result.GasSpent, Is.EqualTo(GasCostOf.Transaction + executionGas), "gas");
        }
    }

    private static void AssertHalt(TestAllTracerWithOutput result, EvmExceptionType error, ulong gasLimit = GasLimit)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Failure));
            Assert.That(result.ReportedActionErrors, Is.EqualTo(new[] { error }));
            Assert.That(result.GasSpent, Is.EqualTo(gasLimit), "an exceptional halt consumes all gas");
        }
    }

    [TestCase("6004B000B1B2", 17UL, TestName = "EIP vector: simple routine")]
    [TestCase("6004B000B16009B0B2B1B2", 34UL, TestName = "EIP vector: two levels of subroutines")]
    [TestCase("600556B1B25B6003B0", 29UL, TestName = "EIP vector: subroutine at end of code")]
    [TestCase("60045600B100", 12UL, TestName = "JUMP lands on CALLDEST")]
    [TestCase("600160065700B100", 17UL, TestName = "JUMPI lands on CALLDEST")]
    [TestCase("6004B000B1600856B1B2", 29UL, TestName = "Tail call by JUMP returns to the original caller")]
    [TestCase("610005B000B1B2", 17UL, TestName = "PUSH2 CALLSUB: simple routine")]
    [TestCase("610005B000B161000BB0B2B1B2", 34UL, TestName = "PUSH2 CALLSUB: two levels of subroutines")]
    [TestCase("61000656B1B25B610004B0", 29UL, TestName = "PUSH2 CALLSUB: subroutine at end of code")]
    [TestCase("6100055600B100", 12UL, TestName = "PUSH2 JUMP lands on CALLDEST")]
    [TestCase("610005B000B161000A56B1B2", 29UL, TestName = "PUSH2 CALLSUB: tail call by PUSH2 JUMP")]
    public void Executes_with_exact_gas(string hex, ulong executionGas) =>
        AssertSuccess(Run(FromEipVector(hex)), executionGas);

    [TestCase("60FFB000B1B2", EvmExceptionType.InvalidJumpDestination, TestName = "EIP vector: destination out of range")]
    [TestCase("B2", EvmExceptionType.ReturnStackUnderflow, TestName = "EIP vector: empty return stack")]
    [TestCase("6004B060BB00", EvmExceptionType.InvalidJumpDestination, TestName = "CALLSUB to CALLDEST inside PUSH data")]
    [TestCase("60045660BB00", EvmExceptionType.InvalidJumpDestination, TestName = "JUMP to CALLDEST inside PUSH data")]
    [TestCase("6003B05B00", EvmExceptionType.InvalidJumpDestination, TestName = "CALLSUB to JUMPDEST")]
    [TestCase("6004B0E6B100", EvmExceptionType.InvalidJumpDestination, TestName = "CALLSUB to CALLDEST inside an EIP-8024 immediate")]
    [TestCase("640100000004B000B1", EvmExceptionType.InvalidJumpDestination, TestName = "CALLSUB destination above uint32")]
    [TestCase("60045600B1B2", EvmExceptionType.ReturnStackUnderflow, TestName = "JUMP to CALLDEST pushes no return address")]
    [TestCase("B0", EvmExceptionType.StackUnderflow, TestName = "CALLSUB with an empty data stack")]
    [TestCase("6100FFB000B1B2", EvmExceptionType.InvalidJumpDestination, TestName = "PUSH2 CALLSUB: destination out of range")]
    [TestCase("610004B05B00", EvmExceptionType.InvalidJumpDestination, TestName = "PUSH2 CALLSUB to JUMPDEST")]
    [TestCase("610005B060BB00", EvmExceptionType.InvalidJumpDestination, TestName = "PUSH2 CALLSUB to CALLDEST inside PUSH data")]
    [TestCase("610005B0E6B100", EvmExceptionType.InvalidJumpDestination, TestName = "PUSH2 CALLSUB to CALLDEST inside an EIP-8024 immediate")]
    [TestCase("610004B0", EvmExceptionType.InvalidJumpDestination, TestName = "PUSH2 CALLSUB to the end of code")]
    public void Halts_exceptionally(string hex, EvmExceptionType error) =>
        AssertHalt(Run(FromEipVector(hex)), error);

    [TestCase(EvmStack.ReturnStackLimit - 1, true, false, TestName = "Return stack fills to its limit")]
    [TestCase(EvmStack.ReturnStackLimit, false, false, TestName = "Return stack overflows past its limit")]
    [TestCase(EvmStack.ReturnStackLimit - 1, true, true, TestName = "PUSH2 CALLSUB: return stack fills to its limit")]
    [TestCase(EvmStack.ReturnStackLimit, false, true, TestName = "PUSH2 CALLSUB: return stack overflows past its limit")]
    public void Return_stack_limit(int recursions, bool succeeds, bool push2Destinations)
    {
        // Entry calls sub, which calls itself `recursions` more times: 1 + recursions return addresses at the deepest point.
        int destinationWidth = push2Destinations ? 2 : 1;
        byte sub = (byte)(6 + destinationWidth);
        byte done = (byte)(sub + 12 + 2 * destinationWidth);
        byte[] code =
        [
            (byte)Instruction.PUSH2, (byte)(recursions >> 8), (byte)recursions,
            .. PushDestination(sub), (byte)Instruction.CALLSUB, (byte)Instruction.STOP,
            (byte)Instruction.CALLDEST, (byte)Instruction.DUP1, (byte)Instruction.ISZERO,
            .. PushDestination(done), (byte)Instruction.JUMPI,
            (byte)Instruction.PUSH1, 1, (byte)Instruction.SWAP1, (byte)Instruction.SUB,
            .. PushDestination(sub), (byte)Instruction.CALLSUB, (byte)Instruction.RETURNSUB,
            (byte)Instruction.JUMPDEST, (byte)Instruction.RETURNSUB,
        ];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(code[sub], Is.EqualTo((byte)Instruction.CALLDEST));
            Assert.That(code[done], Is.EqualTo((byte)Instruction.JUMPDEST));
        }

        const ulong gasLimit = 1_000_000;
        TestAllTracerWithOutput result = Run(code, gasLimit: gasLimit);

        if (succeeds)
        {
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success), result.Error);
        }
        else
        {
            AssertHalt(result, EvmExceptionType.ReturnStackOverflow, gasLimit);
        }

        byte[] PushDestination(byte destination) => push2Destinations
            ? [(byte)Instruction.PUSH2, 0, destination]
            : [(byte)Instruction.PUSH1, destination];
    }

    [TestCase("B1", GasCostOf.JumpDest, EvmExceptionType.None, TestName = "CALLDEST costs 1")]
    [TestCase("6000B0", GasCostOf.VeryLow + GasCostOf.CallSub, EvmExceptionType.InvalidJumpDestination, TestName = "CALLSUB costs 8 before its destination check")]
    [TestCase("6004B000B1", GasCostOf.VeryLow + GasCostOf.CallSub + GasCostOf.JumpDest, EvmExceptionType.None, TestName = "CALLSUB charges the landed-on CALLDEST")]
    [TestCase("B2", GasCostOf.ReturnSub, EvmExceptionType.ReturnStackUnderflow, TestName = "RETURNSUB costs 5 before its return stack check")]
    [TestCase("610000B0", GasCostOf.VeryLow + GasCostOf.CallSub, EvmExceptionType.InvalidJumpDestination, TestName = "PUSH2 CALLSUB costs 8 before its destination check")]
    [TestCase("610005B000B1", GasCostOf.VeryLow + GasCostOf.CallSub + GasCostOf.JumpDest, EvmExceptionType.None, TestName = "PUSH2 CALLSUB charges the landed-on CALLDEST")]
    public void Charges_gas_before_halting(string hex, ulong cost, EvmExceptionType errorWithEnoughGas)
    {
        byte[] code = FromEipVector(hex);
        foreach (bool sufficientGas in new[] { false, true })
        {
            ulong gasLimit = GasCostOf.Transaction + cost - (sufficientGas ? 0UL : 1UL);
            TestAllTracerWithOutput result = Run(code, gasLimit: gasLimit);

            EvmExceptionType expected = sufficientGas ? errorWithEnoughGas : EvmExceptionType.OutOfGas;
            if (expected == EvmExceptionType.None)
                AssertSuccess(result, cost);
            else
                AssertHalt(result, expected, gasLimit);
        }
    }

    [TestCase("B0", EvmExceptionType.BadInstruction, TestName = "CALLSUB is undefined")]
    [TestCase("B1", EvmExceptionType.BadInstruction, TestName = "CALLDEST is undefined")]
    [TestCase("B2", EvmExceptionType.BadInstruction, TestName = "RETURNSUB is undefined")]
    [TestCase("60045600B100", EvmExceptionType.InvalidJumpDestination, TestName = "CALLDEST is no jump destination")]
    [TestCase("610005B000B1B2", EvmExceptionType.BadInstruction, TestName = "PUSH2 CALLSUB is not fused")]
    public void Disabled_spec(string hex, EvmExceptionType error) =>
        AssertHalt(Run(FromEipVector(hex), Disabled), error);

    [Test]
    public void Push2_callsub_keeps_the_push_stack_limit()
    {
        // 1024 words leave no room for PUSH2, even though a fused CALLSUB would pop its destination straight away.
        byte[] code = [.. Enumerable.Repeat((byte)Instruction.PUSH0, EvmStack.MaxStackSize - 1), (byte)Instruction.PUSH2, 0x04, 0x05, (byte)Instruction.CALLSUB, (byte)Instruction.STOP, (byte)Instruction.CALLDEST];
        Assert.That(code[0x405], Is.EqualTo((byte)Instruction.CALLDEST));

        AssertHalt(Run(code), EvmExceptionType.StackOverflow);
    }

    [Test]
    public void Return_stack_is_per_call_frame()
    {
        Address child = TestItem.AddressC;
        TestState.CreateAccount(child, UInt256.Zero);
        TestState.InsertCode(child, new[] { (byte)Instruction.RETURNSUB }, Spec);

        // The child's RETURNSUB must not see the address the caller's CALLSUB pushed.
        byte[] returnWord = Prepare.EvmCode.Return(32, 0).Done;
        byte sub = (byte)(3 + returnWord.Length);
        byte[] code = Prepare.EvmCode
            .PushData(sub)
            .Op(Instruction.CALLSUB)
            .FromCode(returnWord.ToHexString())
            .Op(Instruction.CALLDEST)
            .Call(child, 50_000)
            .PushData(0)
            .Op(Instruction.MSTORE)
            .Op(Instruction.RETURNSUB)
            .Done;
        Assert.That(code[sub], Is.EqualTo((byte)Instruction.CALLDEST));

        TestAllTracerWithOutput result = Run(code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success), result.Error);
            Assert.That(result.ReportedActionErrors, Is.EqualTo(new[] { EvmExceptionType.ReturnStackUnderflow }));
            Assert.That(result.ReturnValue, Is.EqualTo(new byte[32]), "the failed child call pushes 0");
        }
    }

    [Test]
    public void Pooled_frame_starts_with_an_empty_return_stack()
    {
        // Halts inside the subroutine, leaving a return address behind in the pooled frame state.
        AssertSuccess(Run(FromEipVector("6004B000B100")), GasCostOf.VeryLow + GasCostOf.CallSub + GasCostOf.JumpDest);
        AssertHalt(Run(FromEipVector("B2")), EvmExceptionType.ReturnStackUnderflow);
    }

    [Test]
    public void Cancellation_is_polled_during_callsub_recursion()
    {
        // CALLDEST PUSH1 0 CALLSUB recurses with no JUMP, passing the poll interval before the return stack overflows.
        byte[] code = [(byte)Instruction.CALLDEST, (byte)Instruction.PUSH1, 0, (byte)Instruction.CALLSUB];
        (Block block, Transaction transaction) = PrepareTx(Activation, GasLimit, code);
        CancellingTracer tracer = new(traceInstructions);

        Assert.Throws<OperationCanceledException>(() =>
            _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer));
        Assert.That(tracer.PollCount, Is.EqualTo(2), "the first poll is at frame entry and the second at a CALLSUB");
    }

    private const string SimpleRoutine = "6004B000B1B2";
    private static readonly string[] SimpleRoutineOpcodes = ["PUSH1", "CALLSUB", "CALLDEST", "RETURNSUB", "STOP"];
    private static readonly int[] SimpleRoutinePcs = [0, 2, 4, 5, 3];
    private static readonly ulong[] SimpleRoutineCosts = [GasCostOf.VeryLow, GasCostOf.CallSub, GasCostOf.JumpDest, GasCostOf.ReturnSub, 0];

    [Test]
    public void Struct_log_trace_follows_the_subroutine()
    {
        GethLikeTxTrace trace = ExecuteAndTrace(FromEipVector(SimpleRoutine));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(trace.Entries.Select(static e => e.Opcode), Is.EqualTo(SimpleRoutineOpcodes));
            Assert.That(trace.Entries.Select(static e => (int)e.ProgramCounter), Is.EqualTo(SimpleRoutinePcs));
            Assert.That(trace.Entries.Select(static e => e.GasCost), Is.EqualTo(SimpleRoutineCosts));
            Assert.That(trace.Entries.Select(static e => e.Depth), Is.All.EqualTo(1));
        }
    }

    [Test]
    public void JavaScript_tracer_follows_the_subroutine()
    {
        const string userTracer = """
            {
                retVal: [],
                step: function(log, db) { this.retVal.push(log.op.toString() + ':' + log.getPC() + ':' + log.getDepth() + ':' + log.op.isPush() + ':' + log.getGas()) },
                fault: function(log, db) { this.retVal.push('FAULT') },
                result: function(ctx, db) { return this.retVal }
            }
            """;
        using GethLikeBlockJavaScriptTracer tracer = ExecuteBlock(
            new GethLikeBlockJavaScriptTracer(TestState, Spec, GethTraceOptions.Default with { Tracer = userTracer }),
            FromEipVector(SimpleRoutine));
        using GethLikeTxTrace trace = tracer.BuildResult().Single();

        ulong gas = GasLimit - GasCostOf.Transaction;
        string[] expected = new string[SimpleRoutineOpcodes.Length];
        for (int i = 0; i < expected.Length; i++)
        {
            expected[i] = $"{SimpleRoutineOpcodes[i]}:{SimpleRoutinePcs[i]}:1:{(i == 0 ? "true" : "false")}:{gas}";
            gas -= SimpleRoutineCosts[i];
        }

        Assert.That(JsonSerializer.Serialize(trace.CustomTracerResult, EthereumJsonSerializer.JsonOptions), Is.EqualTo(JsonSerializer.Serialize(expected)));
    }

    [Test]
    public void Parity_vm_trace_follows_the_subroutine()
    {
        (Block block, Transaction transaction) = PrepareTx(Activation, GasLimit, FromEipVector(SimpleRoutine));
        ParityLikeTxTracer tracer = new(block, transaction, ParityTraceTypes.Trace | ParityTraceTypes.VmTrace);
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, Spec), tracer);

        ParityLikeTxTrace trace = tracer.BuildResult();
        IReadOnlyList<ParityVmOperationTrace> operations = trace.VmTrace!.Operations;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(trace.Action!.Error, Is.Null);
            Assert.That(trace.Action.Subtraces, Is.Empty, "a subroutine is not a call");
            Assert.That(operations.Select(static op => op.Pc), Is.EqualTo(SimpleRoutinePcs));
            Assert.That(operations.Select(static op => op.Cost), Is.EqualTo(SimpleRoutineCosts));
            Assert.That(operations.Select(static op => op.Push?.Length ?? 0), Is.EqualTo(new[] { 1, 0, 0, 0, 0 }));
            Assert.That(operations.Select(static op => op.Sub), Is.All.Null);
        }
    }

    [Test]
    public void Call_tracer_opens_no_frame_for_a_subroutine()
    {
        (Block block, Transaction transaction) = PrepareTx(Activation, GasLimit, FromEipVector(SimpleRoutine));
        NativeCallTracer tracer = new(transaction, Spec, GethTraceOptions.Default with { Tracer = NativeCallTracer.CallTracer });
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, Spec), tracer);

        using GethLikeTxTrace trace = tracer.BuildResult();
        NativeCallTracerCallFrame root = (NativeCallTracerCallFrame)trace.CustomTracerResult!.Value!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root.Error, Is.Null);
            Assert.That(root.Calls, Is.Empty);
        }
    }

    [TestCase(EvmExceptionType.ReturnStackOverflow, "return stack limit reached")]
    [TestCase(EvmExceptionType.ReturnStackUnderflow, "return stack underflow")]
    public void Rpc_failure_text_is_the_evm_description(EvmExceptionType failure, string description)
    {
        (Block block, Transaction transaction) = PrepareTx(Activation, GasLimit, FromEipVector("B2"));

        bool described = ExecutionFailureText.TryDescribe(_processor, transaction, new BlockExecutionContext(block.Header, Spec),
            failure, failure.GetEvmExceptionDescription(), CancellationToken.None, out string text);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(described, Is.False, "no rerun is needed to describe it");
            Assert.That(text, Is.EqualTo(description));
        }
    }

    private sealed class UntracedInstructionsTracer : TestAllTracerWithOutput
    {
        public override bool IsTracingInstructions => false;
    }

    private sealed class CancellingTracer(bool traceInstructions) : TestAllTracerWithOutput, ITxTracer
    {
        public int PollCount { get; private set; }

        public override bool IsTracingInstructions => traceInstructions;

        bool ITxTracer.IsCancelable => true;

        bool ITxTracer.IsCancelled => ++PollCount >= 2;
    }
}
