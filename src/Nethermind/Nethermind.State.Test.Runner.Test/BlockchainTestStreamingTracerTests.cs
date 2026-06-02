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
        using MemoryStream output = new();
        BlockchainTestStreamingTracer tracer = new(new GethTraceOptions(), output);

        Block block = Build.A.Block.WithNumber(1).TestObject;
        Transaction tx = Build.A.Transaction.WithValue(1).TestObject;

        tracer.StartNewBlockTrace(block);
        tracer.StartNewTxTrace(tx);
        tracer.EndTxTrace();
        tracer.EndBlockTrace();

        string result = Encoding.UTF8.GetString(output.ToArray());
        Assert.That(result, Does.Contain("\"output\""));
        Assert.That(result, Does.Contain("\"gasUsed\""));
    }

    [TestCase(1, 2, TestName = "Multiple_transactions_in_one_block")]
    [TestCase(2, 1, TestName = "Multiple_blocks_with_one_transaction_each")]
    public void Tracer_handles_blocks_and_transactions(int blockCount, int txPerBlock)
    {
        using MemoryStream output = new();
        BlockchainTestStreamingTracer tracer = new(new GethTraceOptions(), output);

        for (int b = 0; b < blockCount; b++)
        {
            tracer.StartNewBlockTrace(Build.A.Block.WithNumber(b + 1).TestObject);
            for (int t = 0; t < txPerBlock; t++)
            {
                tracer.StartNewTxTrace(Build.A.Transaction.WithValue(t + 1).WithNonce((ulong)t).TestObject);
                tracer.EndTxTrace();
            }
            tracer.EndBlockTrace();
        }

        string[] lines = Encoding.UTF8.GetString(output.ToArray()).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        int expectedTxCount = blockCount * txPerBlock;
        Assert.That(lines.Count(l => l.Contains("\"gasUsed\"")), Is.EqualTo(expectedTxCount), $"Should have {expectedTxCount} transaction summary lines");
    }

    [Test]
    public void Tracer_disposes_cleanly()
    {
        using MemoryStream output = new();
        BlockchainTestStreamingTracer tracer = new(new GethTraceOptions(), output);

        Assert.DoesNotThrow(tracer.Dispose);
        Assert.DoesNotThrow(tracer.Dispose); // Double dispose should be safe
    }
}
