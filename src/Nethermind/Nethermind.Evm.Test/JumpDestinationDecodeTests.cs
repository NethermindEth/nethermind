// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// JUMP and JUMPI read the destination straight out of its big-endian stack slot, testing every byte
/// above the low four for zero rather than decoding the word into a <see cref="UInt256"/> first. Each
/// case below sets a byte that only the zero test can catch while leaving the low four bytes naming a
/// real JUMPDEST, so a gap in the test would jump instead of faulting.
/// </summary>
[Parallelizable(ParallelScope.Self)]
public class JumpDestinationDecodeTests : VirtualMachineTestsBase
{
    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.CancunBlockTimestamp;

    private const ulong GasLimit = 100000;

    // PUSH32 <32 bytes> occupies 33 bytes, PUSH1 <byte> two. JUMPI takes the destination from the top
    // of the stack, so its condition is pushed first and its immediate sits two bytes further along.
    private const int JumpDestOffset = 33 + 2;
    private const int JumpIDestOffset = 2 + 33 + 2;

    private static readonly UInt256 HighLimbBit = UInt256.One << 192;
    private static readonly UInt256 LowLimbHighHalfBit = UInt256.One << 32;
    private static readonly UInt256 AboveIntMaxBit = UInt256.One << 31;

    private static UInt256[] OutOfRangeJumpDestinations(int reachableOffset) =>
    [
        HighLimbBit + (UInt256)reachableOffset,
        (UInt256.One << 128) + (UInt256)reachableOffset,
        (UInt256.One << 64) + (UInt256)reachableOffset,
        LowLimbHighHalfBit + (UInt256)reachableOffset,
        AboveIntMaxBit,
        UInt256.MaxValue,
    ];

    private static UInt256[] OutOfRangeJumpCases() => OutOfRangeJumpDestinations(JumpDestOffset);

    private static UInt256[] OutOfRangeJumpICases() => OutOfRangeJumpDestinations(JumpIDestOffset);

    [TestCaseSource(nameof(OutOfRangeJumpCases))]
    public void Jump_beyond_the_low_four_bytes_faults(UInt256 destination)
    {
        //   [0]  PUSH32 destination
        //   [33] JUMP
        //   [34] STOP
        //   [35] JUMPDEST      <- named by the destination's low four bytes
        //   [36] PUSH1 0x42 ... SSTORE
        byte[] code = Prepare.EvmCode
            .PushData(PadTo32(destination))
            .Op(Instruction.JUMP)
            .Op(Instruction.STOP)
            .Op(Instruction.JUMPDEST)
            .PushData((byte)0x42)
            .PushData((byte)0x00)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput result = Execute(Activation, GasLimit, code);

        AssertStorage(UInt256.Zero, UInt256.Zero);
        // An invalid jump destination consumes the whole remaining budget.
        AssertGas(result, GasLimit);
    }

    [TestCaseSource(nameof(OutOfRangeJumpICases))]
    public void JumpIf_beyond_the_low_four_bytes_faults(UInt256 destination)
    {
        //   [0]  PUSH1 0x01    (condition, truthy)
        //   [2]  PUSH32 destination
        //   [35] JUMPI
        //   [36] STOP
        //   [37] JUMPDEST      <- named by the destination's low four bytes
        byte[] code = Prepare.EvmCode
            .PushData((byte)0x01)
            .PushData(PadTo32(destination))
            .Op(Instruction.JUMPI)
            .Op(Instruction.STOP)
            .Op(Instruction.JUMPDEST)
            .PushData((byte)0x42)
            .PushData((byte)0x00)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput result = Execute(Activation, GasLimit, code);

        AssertStorage(UInt256.Zero, UInt256.Zero);
        AssertGas(result, GasLimit);
    }

    [Test]
    public void JumpIf_with_a_zero_condition_ignores_an_out_of_range_destination()
    {
        // An untaken JUMPI never reads the destination, so even an unusable one must fall through.
        byte[] code = Prepare.EvmCode
            .PushData((byte)0x00)
            .PushData(PadTo32(UInt256.MaxValue))
            .Op(Instruction.JUMPI)
            .PushData((byte)0x42)
            .PushData((byte)0x00)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        Execute(Activation, GasLimit, code);

        AssertStorage(UInt256.Zero, (UInt256)0x42);
    }

    [Test]
    public void Jump_to_the_largest_in_range_destination_still_resolves()
    {
        // The low four bytes alone name the marker, with every byte above them zero.
        byte[] code = Prepare.EvmCode
            .PushData(PadTo32((UInt256)JumpDestOffset))
            .Op(Instruction.JUMP)
            .Op(Instruction.STOP)
            .Op(Instruction.JUMPDEST)
            .PushData((byte)0x42)
            .PushData((byte)0x00)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        Execute(Activation, GasLimit, code);

        AssertStorage(UInt256.Zero, (UInt256)0x42);
    }

    /// <summary>A full-width immediate keeps every case at the same code offsets.</summary>
    private static byte[] PadTo32(in UInt256 value)
    {
        byte[] bytes = new byte[32];
        value.ToBigEndian(bytes);
        return bytes;
    }
}
