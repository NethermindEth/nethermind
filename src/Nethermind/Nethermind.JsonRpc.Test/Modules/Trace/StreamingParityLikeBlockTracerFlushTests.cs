// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Trace;

public class StreamingParityLikeBlockTracerFlushTests
{
    [Test]
    public void Replay_WhenTransactionPositionsAreUnused_WritesEquivalentOutputWithoutSearching(
        [Values] bool parallel, [Values] bool includeTxHash)
    {
        EqualityCountingTransaction untraced = new();
        Block block = Build.A.Block.WithTransactions(untraced).TestObject;
        Transaction[] transactions = [new() { Hash = TestItem.KeccakA }, new() { Hash = TestItem.KeccakB }];
        ParityTxTraceFromReplay[] expected = new ParityTxTraceFromReplay[transactions.Length];
        ArrayBufferWriter<byte> output = new();
        using Utf8JsonWriter writer = new(output);
        using StreamingParityLikeBlockTracer tracer = new(ParityTraceTypes.None, ParityTraceStreamMode.Replay,
            includeTxHash, writer, null, CancellationToken.None);
        using ParallelParityTxTracerPool pool = tracer.CreateParallelTxTracers(block, ParityTraceTypes.None);
        IBlockTracer callbacks = tracer;
        callbacks.StartNewBlockTrace(block);
        Assert.That(untraced.Comparisons, Is.Zero, "precondition: building the fixture did not search transaction equality");

        writer.WriteStartArray();
        for (int i = 0; i < transactions.Length; i++)
        {
            IBlockTracer<ParityLikeTxTrace> target = parallel ? pool.For(transactions[i].Hash!) : tracer;
            if (parallel) target.StartNewBlockTrace(block);
            ParityLikeTxTracer txTracer = (ParityLikeTxTracer)target.StartNewTxTrace(transactions[i]);
            byte[] returned = [(byte)i];
            txTracer.MarkAsSuccess(TestItem.AddressA, default, returned, []);
            target.EndTxTrace();
            if (parallel) tracer.Write(target.BuildResult());
            expected[i] = new ParityTxTraceFromReplay
            {
                Output = returned,
                TransactionHash = includeTxHash ? transactions[i].Hash : null
            };
        }
        callbacks.EndBlockTrace();
        writer.WriteEndArray();
        writer.Flush();

        using JsonDocument actual = JsonDocument.Parse(output.WrittenMemory);
        JsonElement buffered = JsonSerializer.SerializeToElement(expected, EthereumJsonSerializer.JsonOptions);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(untraced.Comparisons, Is.Zero, "replay envelopes do not need transaction positions");
            Assert.That(JsonElement.DeepEquals(actual.RootElement, buffered), Is.True, "replay fields must equal the buffered response");
        }
    }

    [Test]
    public void TransactionPositions_WhenStreamingOrReusingParallelTracers_KeepTheFirstOccurrence([Values] bool parallel)
    {
        Transaction[] transactions = new Transaction[256];
        for (int i = 0; i < transactions.Length; i++) transactions[i] = new Transaction { Nonce = (ulong)i };
        transactions[200] = transactions[0];
        transactions[201] = transactions[100];
        Block block = Build.A.Block.WithTransactions(transactions).TestObject;
        ArrayBufferWriter<byte> output = new();
        using Utf8JsonWriter writer = new(output, new JsonWriterOptions { SkipValidation = true });
        using StreamingParityLikeBlockTracer tracer = new(ParityTraceTypes.None, ParityTraceStreamMode.Store,
            includeTxHash: false, writer, null, CancellationToken.None);
        using ParallelParityTxTracerPool pool = tracer.CreateParallelTxTracers(block, ParityTraceTypes.None);
        IBlockTracer callbacks = tracer;
        callbacks.StartNewBlockTrace(block);

        for (int i = transactions.Length - 1; i >= 0; i--)
        {
            Transaction transaction = transactions[i];
            IBlockTracer<ParityLikeTxTrace> target = parallel ? pool.For(transaction.Hash!) : tracer;
            if (parallel) target.StartNewBlockTrace(block);
            ParityLikeTxTracer txTracer = (ParityLikeTxTracer)target.StartNewTxTrace(transaction);
            Assert.That(txTracer.BuildResult().TransactionPosition, Is.EqualTo(Array.IndexOf(transactions, transaction)),
                "streaming reuse and parallel worker order must not change transaction positions");
            target.EndTxTrace();
            if (parallel) target.BuildResult().TryDispose();
        }
        callbacks.EndBlockTrace();
    }

    [Test]
    public void Write_keeps_small_traces_buffered_until_the_threshold()
    {
        Pipe pipe = new(new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0));
        using Utf8JsonWriter writer = new(pipe.Writer, new JsonWriterOptions { SkipValidation = true });
        using StreamingParityLikeBlockTracer tracer = new(ParityTraceTypes.Trace, ParityTraceStreamMode.Store, includeTxHash: false, writer, pipe.Writer, CancellationToken.None);

        tracer.Write([BuildRewardTrace()]);
        tracer.Write([BuildRewardTrace()]);

        Assert.That(pipe.Reader.TryRead(out _), Is.False);

        while (writer.BytesCommitted < StreamingParityLikeBlockTracer.FlushThresholdBytes)
        {
            tracer.Write([BuildRewardTrace()]);
        }

#pragma warning disable NUnit2045 // Keep prerequisite acquisition outside a grouped scope; its output is used by later assertions or subsequent setup/disposal.
        Assert.That(pipe.Reader.TryRead(out ReadResult read), Is.True);
#pragma warning restore NUnit2045
        Assert.That(read.Buffer.Length, Is.GreaterThanOrEqualTo(StreamingParityLikeBlockTracer.FlushThresholdBytes));
        pipe.Reader.AdvanceTo(read.Buffer.End);
    }

    [Test]
    public void ParallelTxTracers_ReuseTheResultOfADisposedTransaction()
    {
        ArrayBufferWriter<byte> output = new();
        using Utf8JsonWriter writer = new(output, new JsonWriterOptions { SkipValidation = true });
        using StreamingParityLikeBlockTracer tracer = new(ParityTraceTypes.Trace, ParityTraceStreamMode.Store, includeTxHash: false, writer, null, CancellationToken.None);
        using ParallelParityTxTracerPool pool = tracer.CreateParallelTxTracers(Build.A.Block.TestObject, ParityTraceTypes.Trace);

        IBlockTracer<ParityLikeTxTrace> first = pool.For(TestItem.KeccakA);
        IReadOnlyCollection<ParityLikeTxTrace> result = first.BuildResult();
        result.TryDispose();
        result.TryDispose();
        IBlockTracer<ParityLikeTxTrace> second = pool.For(TestItem.KeccakB);
        IBlockTracer<ParityLikeTxTrace> third = pool.For(TestItem.KeccakC);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second, Is.SameAs(first), "a disposed result traces the next transaction");
            Assert.That(third, Is.Not.SameAs(first), "a result disposed twice is pooled once");
            Assert.That(result, Is.InstanceOf<IReadOnlyList<ParityLikeTxTrace>>(), "the streams read it by index, without an enumerator");
        }
    }

    private static ParityLikeTxTrace BuildRewardTrace() => new()
    {
        BlockHash = TestItem.KeccakA,
        BlockNumber = 7,
        Action = new ParityTraceAction
        {
            RewardType = "block",
            Value = 2,
            Author = TestItem.AddressE,
            CallType = "reward",
            TraceAddress = default,
            Type = "reward",
            Result = null
        }
    };

    private sealed class EqualityCountingTransaction : Transaction
    {
        public int Comparisons { get; private set; }

        public override bool Equals(object? other)
        {
            Comparisons++;
            return ReferenceEquals(this, other);
        }

        public override int GetHashCode() => base.GetHashCode();
    }
}
