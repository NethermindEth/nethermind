// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[Parallelizable(ParallelScope.Self)]
[TestFixture(false)]
[TestFixture(true)]
public class MemoryPositionTests(bool tracing) : VirtualMachineTestsBase
{
    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.CancunBlockTimestamp;

    protected override TestAllTracerWithOutput CreateTracer() => tracing ? new TestAllTracerWithOutput() : new NoInstructionTracer();

    private sealed class NoInstructionTracer : TestAllTracerWithOutput
    {
        public override bool IsTracingInstructions => false;
    }

    // A position is popped as its low 64 bits plus a marker for the other three limbs. Each case
    // sets one bit in one of those limbs and leaves the low limb zero, so dropping the marker
    // would address offset 0 and succeed instead of running out of gas.
    private const string HighLimb = "0x0000000000000001000000000000000000000000000000000000000000000000";
    private const string MiddleLimb = "0x0000000000000000000000000000000100000000000000000000000000000000";
    private const string LowerLimb = "0x0000000000000000000000000000000000000000000000010000000000000000";

    [Test]
    public void Empty_return_ignores_an_unaddressable_position(
        [Values(Instruction.RETURN, Instruction.REVERT)] Instruction instruction)
    {
        byte[] code = Prepare.EvmCode.PushData(0).PushData(HighLimb).Op(instruction).Done;

        TestAllTracerWithOutput tracer = Execute(code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.Error, Is.EqualTo(instruction == Instruction.REVERT ? TransactionSubstate.Revert : null));
            Assert.That(tracer.ReturnValue, Is.Empty);
        }
    }

    [Test]
    public void Memory_opcode_preserves_gas_and_stack_failure_order(
        [Values(Instruction.MLOAD, Instruction.MSTORE, Instruction.MSTORE8)] Instruction instruction,
        [Values(0, 1, 2)] int depth, [Values(2UL, 3UL, 5UL, 6UL)] ulong availableGas)
    {
        Prepare code = Prepare.EvmCode;
        for (int i = 0; i < depth; i++) code = code.PushData(0);
        int requiredDepth = instruction == Instruction.MLOAD ? 1 : 2;
        string? error = availableGas < 3 ? nameof(EvmExceptionType.OutOfGas)
            : depth < requiredDepth ? nameof(EvmExceptionType.StackUnderflow)
            : availableGas < 6 ? nameof(EvmExceptionType.OutOfGas) : null;
        ulong gasLimit = GasCostOf.Transaction + (ulong)depth * GasCostOf.VeryLow + availableGas;

        TestAllTracerWithOutput tracer = Execute(Activation, gasLimit, code.Op(instruction).Done);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.Error, Is.EqualTo(error));
            Assert.That(tracer.GasSpent, Is.EqualTo(gasLimit));
        }
    }

    [Test]
    public void Byte_store_followed_by_word_load_preserves_neighbours(
        [Values(0, 7, 8, 31, 32, 63, 64, 127, 128, 255, 256, 511, 512)] int position)
    {
        int wordPosition = position / EvmStack.WordSize * EvmStack.WordSize;
        byte[] expected = Bytes.FromHexString("0x0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20");
        byte[] code = Prepare.EvmCode
            .PushData(expected).PushData(wordPosition).Op(Instruction.MSTORE)
            .PushData(0x1234ab).PushData(position).Op(Instruction.MSTORE8)
            .PushData(wordPosition).Op(Instruction.MLOAD)
            .PushData(0).Op(Instruction.MSTORE)
            .PushData(EvmStack.WordSize).PushData(0).Op(Instruction.RETURN).Done;
        expected[position - wordPosition] = 0xab;

        TestAllTracerWithOutput tracer = Execute(code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.Error, Is.Null);
            Assert.That(tracer.ReturnValue, Is.EqualTo(expected));
        }
    }

    [TestCase(Instruction.MSTORE, HighLimb)]
    [TestCase(Instruction.MSTORE, MiddleLimb)]
    [TestCase(Instruction.MSTORE, LowerLimb)]
    [TestCase(Instruction.MSTORE8, HighLimb)]
    [TestCase(Instruction.MSTORE8, MiddleLimb)]
    [TestCase(Instruction.MSTORE8, LowerLimb)]
    [TestCase(Instruction.MLOAD, HighLimb)]
    [TestCase(Instruction.MLOAD, MiddleLimb)]
    [TestCase(Instruction.MLOAD, LowerLimb)]
    [TestCase(Instruction.KECCAK256, HighLimb)]
    [TestCase(Instruction.KECCAK256, MiddleLimb)]
    [TestCase(Instruction.KECCAK256, LowerLimb)]
    [TestCase(Instruction.RETURN, HighLimb)]
    [TestCase(Instruction.RETURN, MiddleLimb)]
    [TestCase(Instruction.RETURN, LowerLimb)]
    [TestCase(Instruction.REVERT, HighLimb)]
    [TestCase(Instruction.REVERT, MiddleLimb)]
    [TestCase(Instruction.REVERT, LowerLimb)]
    [TestCase(Instruction.LOG1, HighLimb)]
    [TestCase(Instruction.LOG1, MiddleLimb)]
    [TestCase(Instruction.LOG1, LowerLimb)]
    [TestCase(Instruction.CODECOPY, HighLimb)]
    [TestCase(Instruction.CODECOPY, MiddleLimb)]
    [TestCase(Instruction.CODECOPY, LowerLimb)]
    [TestCase(Instruction.CALLDATACOPY, HighLimb)]
    [TestCase(Instruction.CALLDATACOPY, MiddleLimb)]
    [TestCase(Instruction.CALLDATACOPY, LowerLimb)]
    [TestCase(Instruction.EXTCODECOPY, HighLimb)]
    [TestCase(Instruction.EXTCODECOPY, MiddleLimb)]
    [TestCase(Instruction.EXTCODECOPY, LowerLimb)]
    public void Position_above_ulong_max_is_out_of_gas(Instruction instruction, string positionHex)
    {
        TestAllTracerWithOutput tracer = Execute(BuildCode(instruction, Bytes.FromHexString(positionHex)));

        Assert.That(tracer.Error, Is.EqualTo(EvmExceptionType.OutOfGas.ToString()));
    }

    [TestCase(Instruction.MSTORE, null)]
    [TestCase(Instruction.MSTORE8, null)]
    [TestCase(Instruction.MLOAD, null)]
    [TestCase(Instruction.KECCAK256, null)]
    [TestCase(Instruction.RETURN, null)]
    [TestCase(Instruction.LOG1, null)]
    [TestCase(Instruction.CODECOPY, null)]
    [TestCase(Instruction.CALLDATACOPY, null)]
    [TestCase(Instruction.EXTCODECOPY, null)]
    [TestCase(Instruction.REVERT, TransactionSubstate.Revert)]
    public void Position_within_ulong_max_is_addressable(Instruction instruction, string? expectedError)
    {
        byte[] position = new byte[32];
        position[^1] = 32;

        TestAllTracerWithOutput tracer = Execute(BuildCode(instruction, position));

        Assert.That(tracer.Error, Is.EqualTo(expectedError));
    }

    // The operands beneath the position, pushed deepest first. A length must be non-zero: a zero
    // length is charged before the position is examined, so it would never reach the check.
    private static byte[] BuildCode(Instruction instruction, byte[] position)
    {
        Prepare code = Prepare.EvmCode;
        switch (instruction)
        {
            case Instruction.MLOAD:
                break;
            case Instruction.MSTORE or Instruction.MSTORE8:
                code = code.PushData(1);
                break;
            case Instruction.LOG1:
                code = code.PushData(0).PushData(EvmStack.WordSize);
                break;
            case Instruction.CODECOPY or Instruction.CALLDATACOPY or Instruction.EXTCODECOPY:
                code = code.PushData(EvmStack.WordSize).PushData(0);
                break;
            default:
                code = code.PushData(EvmStack.WordSize);
                break;
        }

        code = code.PushData(position);

        // EXTCODECOPY reads the account above the destination, so it is pushed last.
        if (instruction == Instruction.EXTCODECOPY)
        {
            code = code.PushData(TestItem.AddressC);
        }

        return code.Op(instruction).Done;
    }
}
