// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Evm.Tracing.GethStyle;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing;

public class GethLikeTxFileTracerTests : VirtualMachineTestsBase
{
    [Test]
    public void Should_have_expected_file_tracing_flags()
    {
        var tracer = new GethLikeTxFileTracer(e => { }, GethTraceOptions.Default);

        tracer.IsTracingMemory.Should().BeTrue();
        tracer.IsTracingOpLevelStorage.Should().BeFalse();
        tracer.IsTracingRefunds.Should().BeTrue();
    }

    [Test]
    public void Should_return_gas_and_return_value_as_expected()
    {
        var trace = ExecuteAndTraceToFile(e => { }, GetBytecode(), GethTraceOptions.Default);

        trace.Gas.Should().Be(24);
        trace.ReturnValue.Length.Should().Be(0);
    }

    [Test]
    public void Should_return_memory_size_with_memory_disabled()
    {
        var entries = new List<GethTxFileTraceEntry>();
        var trace = ExecuteAndTraceToFile(entries.Add, GetBytecode(), GethTraceOptions.Default);

        entries[0].MemorySize.Should().Be(0);
        entries[1].MemorySize.Should().Be(0);
        entries[2].MemorySize.Should().Be(0);
        entries[3].MemorySize.Should().Be(32);
        entries[4].MemorySize.Should().Be(32);
        entries[5].MemorySize.Should().Be(32);
        entries[6].MemorySize.Should().Be(64);

        entries.All(e => e.Memory is null).Should().BeTrue();
    }

    [Test]
    public void Should_return_memory_when_enabled()
    {
        var entries = new List<GethTxFileTraceEntry>();
        var trace = ExecuteAndTraceToFile(entries.Add, GetBytecode(), GethTraceOptions.Default with { EnableMemory = true });

        entries[0].Memory.Count().Should().Be(0);
        entries[1].Memory.Count().Should().Be(0);
        entries[2].Memory.Count().Should().Be(0);
        entries[3].Memory.Count().Should().Be(1);
        entries[4].Memory.Count().Should().Be(1);
        entries[5].Memory.Count().Should().Be(1);
        entries[6].Memory.Count().Should().Be(2);
    }

    [Test]
    public void Should_return_stack_when_enabled()
    {
        var entries = new List<GethTxFileTraceEntry>();
        var trace = ExecuteAndTraceToFile(entries.Add, GetBytecode(), GethTraceOptions.Default);

        entries[0].Stack.Count().Should().Be(0);
        entries[1].Stack.Count().Should().Be(1);
        entries[2].Stack.Count().Should().Be(2);
        entries[3].Stack.Count().Should().Be(0);
        entries[4].Stack.Count().Should().Be(1);
        entries[5].Stack.Count().Should().Be(2);
        entries[6].Stack.Count().Should().Be(0);
    }

    [Test]
    public void Should_not_return_stack_when_disabled()
    {
        var entries = new List<GethTxFileTraceEntry>();
        var trace = ExecuteAndTraceToFile(entries.Add, GetBytecode(), GethTraceOptions.Default with { DisableStack = true });

        entries.All(e => e.Stack.Count == 0).Should().BeTrue();
    }

    private static byte[] GetBytecode() =>
        Prepare.EvmCode
            .PushData(SampleHexData1.PadLeft(64, '0'))
            .PushData(0)
            .Op(Instruction.MSTORE)
            .PushData(SampleHexData2.PadLeft(64, '0'))
            .PushData(32)
            .Op(Instruction.MSTORE)
            .Op(Instruction.STOP)
            .Done;
}
