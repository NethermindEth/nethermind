// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Text;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Test.Runner.Test;

[TestFixture]
public class BlockchainTestStreamingTracerTests
{
    [Test]
    public void Tracer_writes_to_provided_output()
    {
        // Arrange
        var output = new StringWriter();
        var options = new GethTraceOptions();
        var tracer = new BlockchainTestStreamingTracer(options, output);

        var block = Build.A.Block.WithNumber(1).TestObject;
        var tx = Build.A.Transaction.WithValue(1).TestObject;

        // Act
        tracer.StartNewBlockTrace(block);
        var txTracer = tracer.StartNewTxTrace(tx);
        tracer.EndTxTrace();
        tracer.EndBlockTrace();

        // Assert
        var result = output.ToString();
        Assert.That(result, Does.Contain("\"output\""));
        Assert.That(result, Does.Contain("\"gasUsed\""));
    }

    [Test]
    public void Tracer_handles_multiple_transactions()
    {
        // Arrange
        var output = new StringWriter();
        var options = new GethTraceOptions();
        var tracer = new BlockchainTestStreamingTracer(options, output);

        var block = Build.A.Block.WithNumber(1).TestObject;
        var tx1 = Build.A.Transaction.WithValue(1).WithNonce(0).TestObject;
        var tx2 = Build.A.Transaction.WithValue(2).WithNonce(1).TestObject;

        // Act
        tracer.StartNewBlockTrace(block);

        tracer.StartNewTxTrace(tx1);
        tracer.EndTxTrace();

        tracer.StartNewTxTrace(tx2);
        tracer.EndTxTrace();

        tracer.EndBlockTrace();

        // Assert
        var result = output.ToString();
        var lines = result.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        // Should have at least 2 summary lines (one per transaction)
        int summaryLines = 0;
        foreach (var line in lines)
        {
            if (line.Contains("\"gasUsed\""))
                summaryLines++;
        }

        Assert.That(summaryLines, Is.EqualTo(2), "Should have 2 transaction summary lines");
    }

    [Test]
    public void Tracer_handles_multiple_blocks()
    {
        // Arrange
        var output = new StringWriter();
        var options = new GethTraceOptions();
        var tracer = new BlockchainTestStreamingTracer(options, output);

        var block1 = Build.A.Block.WithNumber(1).TestObject;
        var block2 = Build.A.Block.WithNumber(2).TestObject;
        var tx1 = Build.A.Transaction.WithValue(1).TestObject;
        var tx2 = Build.A.Transaction.WithValue(2).TestObject;

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
        var result = output.ToString();
        var lines = result.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        // Should have 2 summary lines (one per transaction across both blocks)
        int summaryLines = 0;
        foreach (var line in lines)
        {
            if (line.Contains("\"gasUsed\""))
                summaryLines++;
        }

        Assert.That(summaryLines, Is.EqualTo(2), "Should have 2 transaction summary lines across both blocks");
    }

    [Test]
    public void Tracer_disposes_cleanly()
    {
        // Arrange
        var output = new StringWriter();
        var options = new GethTraceOptions();
        var tracer = new BlockchainTestStreamingTracer(options, output);

        // Act & Assert - should not throw
        Assert.DoesNotThrow(() => tracer.Dispose());
        Assert.DoesNotThrow(() => tracer.Dispose()); // Double dispose should be safe
    }
}
