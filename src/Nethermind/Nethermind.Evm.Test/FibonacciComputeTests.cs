// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using FluentAssertions;
using Nethermind.Consensus.Tracing;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Evm.TransactionProcessing;
using NUnit.Framework;

namespace Nethermind.Evm.Test;



public class FibonacciComputeTests : VirtualMachineTestsBase
{
    [Test]
    [Ignore("Benchmarking only")]
    public void Run()
    {
        byte[] data = [9, 255];

        byte[] code = Prepare.EvmCode
            .PushData(data)
            .COMMENT("1st/2nd fib number")
            .PushData(0)
            .PushData(1)
            .COMMENT("MAINLOOP:")
            .JUMPDEST()
            .DUPx(3)
            .ISZERO()
            .PushData(26 + data.Length)
            .JUMPI()
            .COMMENT("fib step")
            .DUPx(2)
            .DUPx(2)
            .ADD()
            .SWAPx(2)
            .POP()
            .SWAPx(1)
            .COMMENT("decrement fib step counter")
            .SWAPx(2)
            .PushData(1)
            .SWAPx(1)
            .SUB()
            .SWAPx(2)
            .PushData(5 + data.Length).COMMENT("goto MAINLOOP")
            .JUMP()
            .COMMENT("CLEANUP:")
            .JUMPDEST()
            .SWAPx(2)
            .POP()
            .POP()
            .COMMENT("done: requested fib number is the only element on the stack!")
            .STOP()
            .Done;

        ITxTracer tracer = Debugger.IsAttached
            ? new GethLikeTxMemoryTracer(GethTraceOptions.Default)
            : NullTxTracer.Instance;

        TransactionResult result = ExecuteWithTracer(code, tracer);
        result.Success.Should().BeTrue();
    }
}
