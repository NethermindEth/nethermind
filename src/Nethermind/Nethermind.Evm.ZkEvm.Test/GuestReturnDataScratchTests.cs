// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.ZkEvm.Test;

/// <summary>Nested RETURN/REVERT outputs, and create outputs that are never deployed, are staged in one reused buffer.</summary>
/// <remarks>
/// The guest has no GC, so a copy per output would make heap use grow with the gas a block spends on outputs: a frame
/// returning just over 32 KiB pays about 5,300 gas for a copy that costs the guest heap about 64 KiB.
/// </remarks>
public class GuestReturnDataScratchTests
{
    // Past the host's 32 KiB scratch limit and Osaka's 24 KiB code size limit.
    private const int OutputSize = 33 * 1024;
    // Within the code size limit, but a reverted create output is never deployed either.
    private const int RevertedCreateOutputSize = 16 * 1024;
    private const int Outputs = 32;
    private const ulong Gas = 10_000_000;
    // A copy per output allocates Outputs * OutputSize; the rest is frame bookkeeping and the substitute world state.
    private const int AllowedAllocation = Outputs * OutputSize / 4;

    private static readonly Address Sender = new("0x00000000000000000000000000000000000000ca");
    private static readonly Address Contract = new("0x00000000000000000000000000000000000000c0");

    [Test]
    public void Nested_outputs_reuse_one_buffer([Values(Instruction.RETURN, Instruction.REVERT)] Instruction halt)
    {
        GuestTransactionVirtualMachine vm = new(Output(halt), Sender, UInt256.One);
        byte[] code = CallContract(Outputs);

        vm.Call(code, Gas, Sender, Sender, UInt256.Zero);
        long before = GC.GetAllocatedBytesForCurrentThread();
        vm.Call(code, Gas, Sender, Sender, UInt256.Zero);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(vm.ReturnDataBuffer.Length, Is.EqualTo(OutputSize));
            Assert.That(vm.RetainedReturnDataScratchLength, Is.GreaterThanOrEqualTo(OutputSize));
            Assert.That(allocated, Is.LessThan(AllowedAllocation));
        }
    }

    [TestCase(Instruction.RETURN, OutputSize)]
    [TestCase(Instruction.REVERT, RevertedCreateOutputSize)]
    public void Undeployable_create_outputs_reuse_one_buffer(Instruction halt, int outputSize)
    {
        GuestTransactionVirtualMachine vm = new([], Sender, UInt256.One);
        byte[] initCode = Output(halt, outputSize);

        vm.Create(initCode, Gas, Sender, Contract);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Outputs; i++)
            vm.Create(initCode, Gas, Sender, Contract);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(vm.RetainedReturnDataScratchLength, Is.GreaterThanOrEqualTo(outputSize));
            Assert.That(allocated, Is.LessThan(AllowedAllocation));
        }
    }

    /// <summary>Code that halts with <paramref name="halt"/> over the first <paramref name="size"/> bytes of memory.</summary>
    private static byte[] Output(Instruction halt, int size = OutputSize) =>
        [(byte)Instruction.PUSH3, 0, (byte)(size >> 8), (byte)size, (byte)Instruction.PUSH0, (byte)halt];

    /// <summary>Code that calls <see cref="Contract"/> <paramref name="calls"/> times, copying none of the output.</summary>
    private static byte[] CallContract(int calls)
    {
        byte[] call =
        [
            (byte)Instruction.PUSH0, (byte)Instruction.PUSH0, (byte)Instruction.PUSH0, (byte)Instruction.PUSH0, (byte)Instruction.PUSH0,
            (byte)Instruction.PUSH20, .. Contract.Bytes, (byte)Instruction.GAS, (byte)Instruction.CALL, (byte)Instruction.POP
        ];
        byte[] code = new byte[call.Length * calls];
        for (int i = 0; i < calls; i++)
            call.CopyTo(code, i * call.Length);
        return code;
    }
}
