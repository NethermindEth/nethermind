// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
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

    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.OsakaBlockTimestamp + 1;

    private ForkActivation Disabled => (BlockNumber, MainnetSpecProvider.OsakaBlockTimestamp);
    private ForkActivation WithEip8024 => (BlockNumber, MainnetSpecProvider.OsakaBlockTimestamp + 2);

    protected override ISpecProvider SpecProvider => new CustomSpecProvider(
        ((ForkActivation)0, Osaka.Instance),
        (Disabled, Osaka.Instance),
        (Activation, new Osaka { IsEip7979Enabled = true }),
        (WithEip8024, new Osaka { IsEip7979Enabled = true, IsEip8024Enabled = true }));

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
    public void Executes_with_exact_gas(string hex, ulong executionGas) =>
        AssertSuccess(Run(FromEipVector(hex)), executionGas);

    [TestCase("60FFB000B1B2", EvmExceptionType.InvalidJumpDestination, TestName = "EIP vector: destination out of range")]
    [TestCase("B2", EvmExceptionType.ReturnStackUnderflow, TestName = "EIP vector: empty return stack")]
    [TestCase("6004B060B100", EvmExceptionType.InvalidJumpDestination, TestName = "CALLSUB to CALLDEST inside PUSH data")]
    [TestCase("60045660B100", EvmExceptionType.InvalidJumpDestination, TestName = "JUMP to CALLDEST inside PUSH data")]
    [TestCase("6003B05B00", EvmExceptionType.InvalidJumpDestination, TestName = "CALLSUB to JUMPDEST")]
    [TestCase("640100000004B000B1", EvmExceptionType.InvalidJumpDestination, TestName = "CALLSUB destination above uint32")]
    [TestCase("60045600B1B2", EvmExceptionType.ReturnStackUnderflow, TestName = "JUMP to CALLDEST pushes no return address")]
    [TestCase("B0", EvmExceptionType.StackUnderflow, TestName = "CALLSUB with an empty data stack")]
    public void Halts_exceptionally(string hex, EvmExceptionType error) =>
        AssertHalt(Run(FromEipVector(hex)), error);

    [TestCase(EvmStack.ReturnStackLimit - 1, true, TestName = "Return stack fills to its limit")]
    [TestCase(EvmStack.ReturnStackLimit, false, TestName = "Return stack overflows past its limit")]
    public void Return_stack_limit(int recursions, bool succeeds)
    {
        // Entry calls sub, which calls itself `recursions` more times: 1 + recursions return addresses at the deepest point.
        const byte sub = 7;
        const byte done = 21;
        byte[] code =
        [
            (byte)Instruction.PUSH2, (byte)(recursions >> 8), (byte)recursions,
            (byte)Instruction.PUSH1, sub, (byte)Instruction.CALLSUB, (byte)Instruction.STOP,
            (byte)Instruction.CALLDEST, (byte)Instruction.DUP1, (byte)Instruction.ISZERO,
            (byte)Instruction.PUSH1, done, (byte)Instruction.JUMPI,
            (byte)Instruction.PUSH1, 1, (byte)Instruction.SWAP1, (byte)Instruction.SUB,
            (byte)Instruction.PUSH1, sub, (byte)Instruction.CALLSUB, (byte)Instruction.RETURNSUB,
            (byte)Instruction.JUMPDEST, (byte)Instruction.RETURNSUB,
        ];
        Assert.That(code[sub], Is.EqualTo((byte)Instruction.CALLDEST));
        Assert.That(code[done], Is.EqualTo((byte)Instruction.JUMPDEST));

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
    }

    [TestCase("B1", GasCostOf.JumpDest, EvmExceptionType.None, TestName = "CALLDEST costs 1")]
    [TestCase("6000B0", GasCostOf.VeryLow + GasCostOf.CallSub, EvmExceptionType.InvalidJumpDestination, TestName = "CALLSUB costs 8 before its destination check")]
    [TestCase("6004B000B1", GasCostOf.VeryLow + GasCostOf.CallSub + GasCostOf.JumpDest, EvmExceptionType.None, TestName = "CALLSUB charges the landed-on CALLDEST")]
    [TestCase("B2", GasCostOf.ReturnSub, EvmExceptionType.ReturnStackUnderflow, TestName = "RETURNSUB costs 5 before its return stack check")]
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

    [Test]
    public void Eip8024_immediate_is_not_a_call_destination([Values] bool eip8024)
    {
        // PUSH1 4, CALLSUB, DUPN, <CALLDEST byte>, STOP: the byte is DUPN's immediate only under EIP-8024.
        byte[] code = FromEipVector("6004B0E6B100");
        TestAllTracerWithOutput result = Run(code, eip8024 ? WithEip8024 : Activation);

        if (eip8024)
        {
            AssertHalt(result, EvmExceptionType.InvalidJumpDestination);
        }
        else
        {
            AssertSuccess(result, GasCostOf.VeryLow + GasCostOf.CallSub + GasCostOf.JumpDest);
        }
    }

    [TestCase("B0", EvmExceptionType.BadInstruction, TestName = "CALLSUB is undefined")]
    [TestCase("B1", EvmExceptionType.BadInstruction, TestName = "CALLDEST is undefined")]
    [TestCase("B2", EvmExceptionType.BadInstruction, TestName = "RETURNSUB is undefined")]
    [TestCase("60045600B100", EvmExceptionType.InvalidJumpDestination, TestName = "CALLDEST is no jump destination")]
    public void Disabled_spec(string hex, EvmExceptionType error) =>
        AssertHalt(Run(FromEipVector(hex), Disabled), error);

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
    public void Trace_names_the_opcodes()
    {
        GethLikeTxTrace trace = ExecuteAndTrace(FromEipVector("6004B000B1B2"));

        Assert.That(trace.Entries.Select(static e => e.Opcode),
            Is.EqualTo(new[] { "PUSH1", "CALLSUB", "CALLDEST", "RETURNSUB", "STOP" }));
    }

    private sealed class UntracedInstructionsTracer : TestAllTracerWithOutput
    {
        public override bool IsTracingInstructions => false;
    }
}
