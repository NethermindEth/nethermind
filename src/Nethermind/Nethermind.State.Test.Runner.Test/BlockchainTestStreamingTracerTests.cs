// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.Test.Runner;
using NUnit.Framework;

namespace Nethermind.State.Test.Runner.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class BlockchainTestStreamingTracerTests
{
    private const long BeforeTransitionDestroyRefund = (long)RefundOf.DestroyBeforeEip3529;
    private const long AfterTransitionDestroyRefund = 0;

    [Test]
    public void Tracer_writes_to_provided_output()
    {
        using MemoryStream output = new();
        BlockchainTestStreamingTracer tracer = new(
            new GethTraceOptions(),
            new TestSingleReleaseSpecProvider(London.Instance),
            output);

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
        BlockchainTestStreamingTracer tracer = new(
            new GethTraceOptions(),
            new TestSingleReleaseSpecProvider(London.Instance),
            output);

        for (int b = 0; b < blockCount; b++)
        {
            tracer.StartNewBlockTrace(Build.A.Block.WithNumber(b + 1).TestObject);
            for (uint t = 0; t < txPerBlock; t++)
            {
                tracer.StartNewTxTrace(Build.A.Transaction.WithValue(t + 1).WithNonce(t).TestObject);
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
        BlockchainTestStreamingTracer tracer = new(
            new GethTraceOptions(),
            new TestSingleReleaseSpecProvider(London.Instance),
            output);

        Assert.DoesNotThrow(tracer.Dispose);
        Assert.DoesNotThrow(tracer.Dispose); // Double dispose should be safe
    }

    [TestCase(0UL, BeforeTransitionDestroyRefund)]
    [TestCase(9UL, BeforeTransitionDestroyRefund)]
    [TestCase(10UL, AfterTransitionDestroyRefund)]
    [TestCase(11UL, AfterTransitionDestroyRefund)]
    public void Tracer_selects_destroy_refund_at_block_transition(ulong blockNumber, long expectedRefund)
    {
        long refund = TraceDestroyRefund(new ForkActivation(10), blockNumber, timestamp: 0);

        Assert.That(refund, Is.EqualTo(expectedRefund));
    }

    [TestCase(9UL, BeforeTransitionDestroyRefund)]
    [TestCase(10UL, AfterTransitionDestroyRefund)]
    [TestCase(11UL, AfterTransitionDestroyRefund)]
    public void Tracer_selects_destroy_refund_at_timestamp_transition(ulong timestamp, long expectedRefund)
    {
        long refund = TraceDestroyRefund(ForkActivation.TimestampOnly(10), blockNumber: 1, timestamp: timestamp);

        Assert.That(refund, Is.EqualTo(expectedRefund));
    }

    private static long TraceDestroyRefund(ForkActivation transition, ulong blockNumber, ulong timestamp)
    {
        using MemoryStream output = new();
        ISpecProvider specProvider = new CustomSpecProvider(
            ((ForkActivation)0, Frontier.Instance),
            (transition, London.Instance));
        using BlockchainTestStreamingTracer tracer = new(
            new GethTraceOptions(),
            specProvider,
            output);
        Block block = Build.A.Block.WithNumber(blockNumber).WithTimestamp(timestamp).TestObject;

        tracer.StartNewBlockTrace(block);
        GethLikeTxFileTracer txTracer = (GethLikeTxFileTracer)tracer.StartNewTxTrace(null);
        txTracer.ReportSelfDestruct(Address.Zero, default, Address.Zero);
        using ExecutionEnvironment environment = ExecutionEnvironment.Rent(
            null!, Address.Zero, Address.Zero, null, callDepth: 0, value: default, inputData: ReadOnlyMemory<byte>.Empty);
        txTracer.StartOperation(0, Instruction.STOP, 100, in environment);
        txTracer.ReportOperationRemainingGas(100);
        tracer.EndTxTrace();

        string firstLine = Encoding.UTF8.GetString(output.ToArray()).Split(Environment.NewLine)[0];
        using JsonDocument operation = JsonDocument.Parse(firstLine);
        return operation.RootElement.GetProperty("refund").GetInt64();
    }
}
