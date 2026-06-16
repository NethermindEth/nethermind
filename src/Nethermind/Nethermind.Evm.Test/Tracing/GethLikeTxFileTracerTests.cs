// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Nethermind.Blockchain.Tracing.GethStyle;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing;

public class GethLikeTxFileTracerTests : VirtualMachineTestsBase
{
    [Test]
    public void Should_have_expected_file_tracing_flags()
    {
        GethLikeTxFileTracer tracer = new(static e => { }, GethTraceOptions.Default);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.IsTracingMemory, Is.True);
            Assert.That(tracer.IsTracingOpLevelStorage, Is.False);
            Assert.That(tracer.IsTracingRefunds, Is.True);
        }
    }

    [Test]
    public void Should_return_gas_and_return_value_as_expected()
    {
        GethLikeTxTrace trace = ExecuteAndTraceToFile(static e => { }, GetBytecode(), GethTraceOptions.Default);

        Assert.That(trace.Gas, Is.EqualTo(24));
        Assert.That(trace.ReturnValue.Length, Is.EqualTo(0));
    }

    [Test]
    public void Should_return_memory_size_with_memory_disabled()
    {
        List<GethTxFileTraceEntry> entries = [];
        GethLikeTxTrace trace = ExecuteAndTraceToFile(e => entries.Add(CloneTraceEntry(e)), GetBytecode(), GethTraceOptions.Default);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries[0].MemorySize, Is.EqualTo(0));
            Assert.That(entries[1].MemorySize, Is.EqualTo(0));
            Assert.That(entries[2].MemorySize, Is.EqualTo(0));
            Assert.That(entries[3].MemorySize, Is.EqualTo(32));
            Assert.That(entries[4].MemorySize, Is.EqualTo(32));
            Assert.That(entries[5].MemorySize, Is.EqualTo(32));
            Assert.That(entries[6].MemorySize, Is.EqualTo(64));

            Assert.That(entries.All(e => e.Memory is null), Is.True);
        }
    }

    [Test]
    public void Should_return_memory_when_enabled()
    {
        List<GethTxFileTraceEntry> entries = [];
        GethLikeTxTrace trace = ExecuteAndTraceToFile(e => entries.Add(CloneTraceEntry(e)), GetBytecode(), GethTraceOptions.Default with { EnableMemory = true });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries[0].Memory.Length, Is.EqualTo(0));
            Assert.That(entries[1].Memory.Length, Is.EqualTo(0));
            Assert.That(entries[2].Memory.Length, Is.EqualTo(0));
            Assert.That(entries[3].Memory.Length, Is.EqualTo(1));
            Assert.That(entries[4].Memory.Length, Is.EqualTo(1));
            Assert.That(entries[5].Memory.Length, Is.EqualTo(1));
            Assert.That(entries[6].Memory.Length, Is.EqualTo(2));
        }
    }

    [Test]
    public void Should_return_stack_when_enabled()
    {
        List<GethTxFileTraceEntry> entries = [];
        GethLikeTxTrace trace = ExecuteAndTraceToFile(e => entries.Add(CloneTraceEntry(e)), GetBytecode(), GethTraceOptions.Default);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries[0].Stack.Length, Is.EqualTo(0));
            Assert.That(entries[1].Stack.Length, Is.EqualTo(1));
            Assert.That(entries[2].Stack.Length, Is.EqualTo(2));
            Assert.That(entries[3].Stack.Length, Is.EqualTo(0));
            Assert.That(entries[4].Stack.Length, Is.EqualTo(1));
            Assert.That(entries[5].Stack.Length, Is.EqualTo(2));
            Assert.That(entries[6].Stack.Length, Is.EqualTo(0));
        }
    }

    [Test]
    public void Should_not_return_stack_when_disabled()
    {
        List<GethTxFileTraceEntry> entries = [];
        GethLikeTxTrace trace = ExecuteAndTraceToFile(e => entries.Add(CloneTraceEntry(e)), GetBytecode(), GethTraceOptions.Default with { DisableStack = true });

        Assert.That(entries.All(e => e.Stack is null), Is.True);
    }

    /// <summary>
    /// Clones the specified trace entry as <see cref="GethLikeTxFileTracer"/>
    /// reuses the same instance for all entries.
    /// </summary>
    private static GethTxFileTraceEntry CloneTraceEntry(GethTxFileTraceEntry entry) =>
        JsonSerializer.Deserialize<GethTxFileTraceEntry>(JsonSerializer.Serialize(entry));

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
