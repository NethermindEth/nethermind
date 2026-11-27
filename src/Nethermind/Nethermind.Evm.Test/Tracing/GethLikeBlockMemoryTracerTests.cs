// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing;

public class GethLikeBlockMemoryTracerTests
{
    [Test]
    public void Starts_with_trace_set_to_null()
    {
        Hash256 txHash = TestItem.KeccakA;
        GethLikeBlockMemoryTracer blockTracer = new(GethTraceOptions.Default with { TxHash = txHash });
        Assert.That(blockTracer.BuildResult().SingleOrDefault(), Is.Null, $"starts with trace set to null");
    }

    [Test]
    public void Number_of_tx_traces_equals_number_of_txs_in_a_block()
    {
        Block block = Build.A.Block.TestObject;
        block = block.WithReplacedBody(new BlockBody(new Transaction[3], []));

        GethLikeBlockMemoryTracer blockTracer = new(GethTraceOptions.Default);

        for (int i = 0; i < block.Transactions.Length; i++)
        {
            ((IBlockTracer)blockTracer).StartNewTxTrace(Build.A.Transaction.TestObject);
            ((IBlockTracer)blockTracer).EndTxTrace();
        }

        Assert.That(blockTracer.BuildResult().Count, Is.EqualTo(3));
    }

    [Test]
    public void Records_trace_properly()
    {
        Block block = Build.A.Block.TestObject;
        _ = block.WithReplacedBody(new BlockBody(new Transaction[3], []));

        GethLikeBlockMemoryTracer blockTracer = new(GethTraceOptions.Default);
        ((IBlockTracer)blockTracer).StartNewTxTrace(Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).TestObject);
        ((IBlockTracer)blockTracer).EndTxTrace();

        ((IBlockTracer)blockTracer).StartNewTxTrace(Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyB).TestObject);
        ((IBlockTracer)blockTracer).EndTxTrace();

        ((IBlockTracer)blockTracer).StartNewTxTrace(Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyC).TestObject);
        ((IBlockTracer)blockTracer).EndTxTrace();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blockTracer.BuildResult().First(), Is.Not.Null, "0");
            Assert.That(blockTracer.BuildResult().Skip(1).First(), Is.Not.Null, "1");
            Assert.That(blockTracer.BuildResult().Last(), Is.Not.Null, "2");
        }
    }

    [TestCase(0UL, 24_000L)]
    [TestCase(1UL, null)]
    public void Uses_destroy_refund_from_active_block_spec(ulong blockNumber, long? expectedRefund)
    {
        ISpecProvider specProvider = new CustomSpecProvider(
            ((ForkActivation)0, Frontier.Instance),
            ((ForkActivation)1, London.Instance));
        GethLikeBlockMemoryTracer blockTracer = new(GethTraceOptions.Default, specProvider);
        Block block = Build.A.Block.WithNumber(blockNumber).WithTimestamp(0).TestObject;

        blockTracer.StartNewBlockTrace(block);
        ITxTracer txTracer = ((IBlockTracer)blockTracer).StartNewTxTrace(Build.A.Transaction.TestObject);
        txTracer.ReportAction(100, UInt256.Zero, Address.Zero, Address.Zero, default, ExecutionType.TRANSACTION);
        txTracer.ReportSelfDestruct(TestItem.AddressA, UInt256.Zero, Address.Zero);
        using ExecutionEnvironment environment = ExecutionEnvironment.Rent(
            null!, Address.Zero, Address.Zero, null, callDepth: 0, value: UInt256.Zero, inputData: ReadOnlyMemory<byte>.Empty);
        txTracer.StartOperation(0, Instruction.STOP, 100, in environment);
        txTracer.ReportActionEnd(100, default);
        blockTracer.EndTxTrace();

        Assert.That(blockTracer.BuildResult().Single().Entries.Single().Refund, Is.EqualTo(expectedRefund));
    }

    [Test]
    [Ignore("It is not the actual behaviour at the moment")]
    public void Throws_when_ending_without_starting()
    {
        Block block = Build.A.Block.TestObject;
        block = block.WithReplacedBody(new BlockBody(new Transaction[3], []));
        block.Transactions[0] = Build.A.Transaction.TestObject;
        block.Transactions[1] = Build.A.Transaction.TestObject;
        block.Transactions[2] = Build.A.Transaction.TestObject;

        GethLikeBlockMemoryTracer blockTracer1 = new(GethTraceOptions.Default);
        Assert.Throws<InvalidOperationException>(() => ((IBlockTracer)blockTracer1).EndTxTrace());

        GethLikeBlockMemoryTracer blockTracer2 = new(GethTraceOptions.Default);
        ((IBlockTracer)blockTracer2).StartNewTxTrace(block.Transactions[0]);
        Assert.DoesNotThrow(() => ((IBlockTracer)blockTracer2).EndTxTrace());
        Assert.Throws<InvalidOperationException>(() => ((IBlockTracer)blockTracer2).EndTxTrace());
    }

    /// <remarks>EIP-4399: from the merge, opcode 0x44 is PREVRANDAO.</remarks>
    [Test]
    public void Names_opcode_0x44_by_the_traced_block([Values] bool postMerge, [Values("memory", "streaming", "envelope")] string tracer)
    {
        Block block = Build.A.Block.WithNumber(1).TestObject;
        block.Header.IsPostMerge = postMerge;
        using MemoryStream stream = new();
        using Utf8JsonWriter writer = new(stream);
        using ExecutionEnvironment environment = ExecutionEnvironment.Rent(
            null!, Address.Zero, Address.Zero, null, callDepth: 0, value: UInt256.Zero, inputData: ReadOnlyMemory<byte>.Empty);
        GethLikeBlockMemoryTracer? memoryTracer = tracer == "memory" ? new(GethTraceOptions.Default) : null;
        IBlockTracer blockTracer = tracer switch
        {
            "memory" => memoryTracer!,
            "streaming" => new GethLikeBlockStreamingMemoryTracer(GethTraceOptions.Default, writer, null, CancellationToken.None),
            _ => new GethLikeBlockEnvelopeStreamingTracer(GethTraceOptions.Default, writer, null, CancellationToken.None)
        };

        writer.WriteStartArray();
        blockTracer.StartNewBlockTrace(block);
        ITxTracer txTracer = blockTracer.StartNewTxTrace(Build.A.Transaction.TestObject);
        txTracer.StartOperation(0, Instruction.PREVRANDAO, 100, in environment);
        txTracer.ReportOperationRemainingGas(98);
        blockTracer.EndTxTrace();
        blockTracer.EndBlockTrace();
        writer.WriteEndArray();
        writer.Flush();

        string expected = postMerge ? "PREVRANDAO" : "DIFFICULTY";
        if (memoryTracer is not null)
            Assert.That(memoryTracer.BuildResult().Single().Entries.Single().Opcode, Is.EqualTo(expected));
        else
            Assert.That(Encoding.UTF8.GetString(stream.ToArray()), Does.Contain($"\"op\":\"{expected}\""));
    }
}
