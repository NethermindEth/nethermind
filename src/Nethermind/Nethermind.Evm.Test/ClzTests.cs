// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>Covers the CLZ opcode (EIP-7939) across all four limbs of the word it counts.</summary>
[Parallelizable(ParallelScope.Self)]
public class ClzTests : VirtualMachineTestsBase
{
    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.OsakaBlockTimestamp;

    [Test]
    public void Counts_the_zero_bits_above_a_single_set_bit([Range(0, 255)] int bit)
        => AssertClz(UInt256.One << bit, 255 - bit);

    [Test]
    public void Counts_the_zero_bits_above_the_most_significant_set_bit([Range(0, 255)] int bit)
        => AssertClz((UInt256.One << bit) | ((UInt256.One << bit) - UInt256.One), 255 - bit);

    [Test]
    public void An_empty_word_counts_every_bit() => AssertClz(UInt256.Zero, 256);

    private void AssertClz(UInt256 value, int expected)
    {
        byte[] code = Prepare.EvmCode
            .PushData(value)
            .Op(Instruction.CLZ)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Done;

        _ = Execute(code);
        AssertStorage(UInt256.Zero, (UInt256)expected);
    }
}
