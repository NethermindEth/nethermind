// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.Tracing;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// A run whose block context differs from the one it is replayed in holds only where it read none of the differing
/// fields, so every opcode that reads one must report it, traced or not.
/// </summary>
[Parallelizable(ParallelScope.Self)]
public class BlockContextReadsTests : VirtualMachineTestsBase
{
    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.OsakaBlockTimestamp;

    [TestCase(Instruction.COINBASE, BlockContextReads.Coinbase)]
    [TestCase(Instruction.TIMESTAMP, BlockContextReads.Timestamp)]
    [TestCase(Instruction.GASLIMIT, BlockContextReads.GasLimit)]
    [TestCase(Instruction.PREVRANDAO, BlockContextReads.PrevRandao)]
    [TestCase(Instruction.NUMBER, BlockContextReads.None)]
    [TestCase(Instruction.BASEFEE, BlockContextReads.None)]
    [TestCase(Instruction.BLOBBASEFEE, BlockContextReads.None)]
    [TestCase(Instruction.CHAINID, BlockContextReads.None)]
    public void Reads_of_the_block_context_are_reported(Instruction opcode, BlockContextReads expected)
    {
        byte[] code = [(byte)opcode, (byte)Instruction.POP, (byte)Instruction.STOP];

        Machine.BlockContextReads = BlockContextReads.None;
        Execute(code);
        BlockContextReads traced = Machine.BlockContextReads;

        Machine.BlockContextReads = BlockContextReads.None;
        Execute(NullTxTracer.Instance, code);
        BlockContextReads untraced = Machine.BlockContextReads;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(traced, Is.EqualTo(expected), "traced");
            Assert.That(untraced, Is.EqualTo(expected), "untraced");
        }
    }

    [TestCase(new byte[] { 0x5B, 0x5F, 0x56 }, BlockContextReads.OutOfGas, TestName = "A loop runs out of gas")]
    [TestCase(new byte[] { 0x5F, 0x5F, 0xFD }, BlockContextReads.None, TestName = "A revert does not")]
    public void Running_out_of_gas_is_reported(byte[] code, BlockContextReads expected)
    {
        Machine.BlockContextReads = BlockContextReads.None;
        Execute(NullTxTracer.Instance, code);

        Assert.That(Machine.BlockContextReads, Is.EqualTo(expected));
    }

    [Test]
    public void Reads_of_several_fields_are_all_reported()
    {
        Machine.BlockContextReads = BlockContextReads.None;
        Execute(NullTxTracer.Instance,
            [(byte)Instruction.TIMESTAMP, (byte)Instruction.GASLIMIT, (byte)Instruction.POP, (byte)Instruction.POP, (byte)Instruction.STOP]);

        Assert.That(Machine.BlockContextReads, Is.EqualTo(BlockContextReads.Timestamp | BlockContextReads.GasLimit));
    }
}
