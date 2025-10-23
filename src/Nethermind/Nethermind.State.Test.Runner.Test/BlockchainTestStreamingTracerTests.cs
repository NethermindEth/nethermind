// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Text;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Test.Runner;
using NUnit.Framework;

namespace Nethermind.State.Test.Runner.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class BlockchainTestStreamingTracerTests
{
    [Test]
    public void Tracer_writes_to_provided_output()
    {
        // Arrange
        using var output = new MemoryStream();
        var options = new GethTraceOptions();
        var tracer = new BlockchainTestStreamingTracer(options, output);

        Block block = Build.A.Block.WithNumber(1).TestObject;
        Transaction tx = Build.A.Transaction.WithValue(1).TestObject;

        // Act
        tracer.StartNewBlockTrace(block);
        tracer.StartNewTxTrace(tx);
        tracer.EndTxTrace();
        tracer.EndBlockTrace();

        // Assert
        var result = Encoding.UTF8.GetString(output.ToArray());
        Assert.That(result, Does.Contain("\"output\""));
        Assert.That(result, Does.Contain("\"gasUsed\""));
    }

    [Test]
    public void Tracer_handles_multiple_transactions()
    {
        // Arrange
        using var output = new MemoryStream();
        var options = new GethTraceOptions();
        var tracer = new BlockchainTestStreamingTracer(options, output);

        Block block = Build.A.Block.WithNumber(1).TestObject;
        Transaction tx1 = Build.A.Transaction.WithValue(1).WithNonce(0).TestObject;
        Transaction tx2 = Build.A.Transaction.WithValue(2).WithNonce(1).TestObject;

        // Act
        tracer.StartNewBlockTrace(block);

        tracer.StartNewTxTrace(tx1);
        tracer.EndTxTrace();

        tracer.StartNewTxTrace(tx2);
        tracer.EndTxTrace();

        tracer.EndBlockTrace();

        // Assert
        var result = Encoding.UTF8.GetString(output.ToArray());
        var lines = result.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        // Should have at least 2 summary lines (one per transaction)
        Assert.That(lines.Count(l => l.Contains("\"gasUsed\"")), Is.EqualTo(2), "Should have 2 transaction summary lines");
    }

    [Test]
    public void Tracer_handles_multiple_blocks()
    {
        // Arrange
        using var output = new MemoryStream();
        var options = new GethTraceOptions();
        var tracer = new BlockchainTestStreamingTracer(options, output);

        Block block1 = Build.A.Block.WithNumber(1).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).TestObject;
        Transaction tx1 = Build.A.Transaction.WithValue(1).TestObject;
        Transaction tx2 = Build.A.Transaction.WithValue(2).TestObject;

        // Act
        tracer.StartNewBlockTrace(block1);
        tracer.StartNewTxTrace(tx1);
        tracer.EndTxTrace();
        tracer.EndBlockTrace();

        tracer.StartNewBlockTrace(block2);
        tracer.StartNewTxTrace(tx2);
        tracer.EndTxTrace();
        tracer.EndBlockTrace();

        // Assert
        var result = Encoding.UTF8.GetString(output.ToArray());
        var lines = result.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        // Should have 2 summary lines (one per transaction across both blocks)
        Assert.That(lines.Count(l => l.Contains("\"gasUsed\"")), Is.EqualTo(2), "Should have 2 transaction summary lines across both blocks");
    }

    [Test]
    public void Tracer_disposes_cleanly()
    {
        // Arrange
        using var output = new MemoryStream();
        var options = new GethTraceOptions();
        var tracer = new BlockchainTestStreamingTracer(options, output);

        // Act & Assert - should not throw
        Assert.DoesNotThrow(() => tracer.Dispose());
        Assert.DoesNotThrow(() => tracer.Dispose()); // Double dispose should be safe
    }
}
