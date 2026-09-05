// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
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
    public void Position_above_ulong_max_is_out_of_gas(Instruction instruction, string positionHex)
    {
        TestAllTracerWithOutput tracer = Execute(BuildCode(instruction, Bytes.FromHexString(positionHex)));

        Assert.That(tracer.Error, Is.EqualTo(EvmExceptionType.OutOfGas.ToString()));
    }

    [TestCase(Instruction.MSTORE)]
    [TestCase(Instruction.MSTORE8)]
    [TestCase(Instruction.MLOAD)]
    public void Position_within_ulong_max_is_addressable(Instruction instruction)
    {
        byte[] position = new byte[32];
        position[^1] = 32;

        TestAllTracerWithOutput tracer = Execute(BuildCode(instruction, position));

        Assert.That(tracer.Error, Is.Null);
    }

    private static byte[] BuildCode(Instruction instruction, byte[] position)
    {
        Prepare code = Prepare.EvmCode;
        if (instruction != Instruction.MLOAD)
        {
            code = code.PushData(1);
        }

        return code.PushData(position).Op(instruction).Done;
    }
}
