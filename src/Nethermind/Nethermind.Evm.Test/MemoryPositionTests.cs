// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[Parallelizable(ParallelScope.Self)]
public class MemoryPositionTests : VirtualMachineTestsBase
{
    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.CancunBlockTimestamp;

    // A position is popped as its low 64 bits plus a marker for the other three limbs. Each case
    // sets one bit in one of those limbs and leaves the low limb zero, so dropping the marker
    // would address offset 0 and succeed instead of running out of gas.
    private const string HighLimb = "0x0000000000000001000000000000000000000000000000000000000000000000";
    private const string MiddleLimb = "0x0000000000000000000000000000000100000000000000000000000000000000";
    private const string LowerLimb = "0x0000000000000000000000000000000000000000000000010000000000000000";

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
