// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Trace;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Trace;

public class StreamingParityLikeBlockTracerFlushTests
{
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

    [TestCase(true)]
    [TestCase(false)]
    public void Write_stops_at_the_limit_only_when_the_response_is_kept_in_memory(bool retainsOutput)
    {
        ArrayBufferWriter<byte> output = new();
        using Utf8JsonWriter writer = new(output, new JsonWriterOptions { SkipValidation = true });
        using StreamingParityLikeBlockTracer tracer = new(ParityTraceTypes.Trace, ParityTraceStreamMode.Store, includeTxHash: false, writer, null, CancellationToken.None)
        {
            MaxRetainedBytes = 4096,
            RetainsOutput = retainsOutput
        };

        void WriteTraces()
        {
            for (int i = 0; i < 64; i++)
            {
                tracer.Write([BuildRewardTrace()]);
            }
        }

        Assert.That(WriteTraces, retainsOutput ? Throws.TypeOf<TraceDataLimitExceededException>() : Throws.Nothing);
    }

    [Test]
    public void Buffered_output_is_checked_before_copying_a_serialized_element()
    {
        ArrayBufferWriter<byte> output = new();
        using Utf8JsonWriter writer = new(output);
        using StreamingParityLikeBlockTracer tracer = new(
            ParityTraceTypes.Trace, ParityTraceStreamMode.Store, includeTxHash: false, writer, null, CancellationToken.None)
        {
            MaxRetainedBytes = 8,
            RetainsOutput = true
        };
        writer.WriteStartArray();
        writer.WriteNullValue();
        long before = writer.BytesPending;

        Assert.That(() => tracer.WriteSerialized(new ReadOnlySequence<byte>("true"u8.ToArray())), Throws.TypeOf<TraceDataLimitExceededException>());
        Assert.That(writer.BytesPending, Is.EqualTo(before));
    }

    [Test]
    public void Buffered_store_output_checks_each_action_before_adding_it([Values(1, 16)] int actionCount)
    {
        int limit = actionCount == 1 ? 64 : 768;
        ParityLikeTxTrace trace = BuildRewardTrace();
        for (int i = 1; i < actionCount; i++) trace.Action!.Subtraces.Add(BuildRewardTrace().Action!);

        ArrayBufferWriter<byte> output = new();
        using Utf8JsonWriter writer = new(output);
        using StreamingParityLikeBlockTracer tracer = new(
            ParityTraceTypes.Trace, ParityTraceStreamMode.Store, includeTxHash: false, writer, null, CancellationToken.None)
        {
            MaxRetainedBytes = limit,
            RetainsOutput = true
        };
        writer.WriteStartArray();

        Assert.That(() => tracer.Write([trace]), Throws.TypeOf<TraceDataLimitExceededException>());

        writer.Flush();
        Assert.That(output.WrittenCount, actionCount == 1 ? Is.EqualTo(1) : Is.InRange(2, limit));
    }

    [Test]
    public void Retained_store_action_data_counts_a_shared_result_array_once([Values] bool shared)
    {
        byte[] deployed = new byte[8];
        ParityLikeTxTrace trace = new()
        {
            Action = new ParityTraceAction
            {
                Type = "create",
                Input = new byte[3],
                Result = new ParityTraceResult { Code = deployed, Output = shared ? deployed : new byte[8] },
            }
        };

        ParityTxTraceFromStore item = ParityTxTraceFromStore.FromTxTrace(trace).Single();

        Assert.That(item.GetRetainedDataSize(), Is.EqualTo(ParityLikeTxTracer.RetainedActionBytes + (shared ? 11 : 19)));
    }

    [Test]
    public void Buffered_replay_checks_each_value_before_adding_it([Values] bool stateDiff)
    {
        const int limit = 2048;
        Transaction tx = Build.A.Transaction.TestObject;
        Block block = Build.A.Block.WithTransactions(tx).TestObject;
        ArrayBufferWriter<byte> output = new();
        using Utf8JsonWriter writer = new(output);
        using StreamingParityLikeBlockTracer tracer = new(
            stateDiff ? ParityTraceTypes.StateDiff : ParityTraceTypes.Trace,
            ParityTraceStreamMode.Replay, includeTxHash: false, writer, null, CancellationToken.None)
        {
            MaxRetainedBytes = limit,
            RetainsOutput = true
        };
        tracer.StartNewBlockTrace(block);
        ITxTracer txTracer = ((IBlockTracer)tracer).StartNewTxTrace(tx);
        if (stateDiff)
        {
            txTracer.ReportCodeChange(TestItem.AddressA, default, new byte[1200]);
        }
        else
        {
            txTracer.ReportAction(100000, UInt256.Zero, TestItem.AddressA, TestItem.AddressB, new byte[1000], ExecutionType.TRANSACTION);
            txTracer.ReportActionEnd(1000, ReadOnlyMemory<byte>.Empty);
        }
        txTracer.MarkAsSuccess(TestItem.AddressB, default, [], []);

        Assert.That(tracer.EndTxTrace, Throws.TypeOf<TraceDataLimitExceededException>());

        writer.Flush();
        Assert.That(output.WrittenCount, Is.LessThanOrEqualTo(limit));
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

    private const long OneCallLimit = ParityLikeTxTracer.RetainedActionBytes + 100;

    private static (Transaction[] Transactions, Block Block) CallsWithInputs(params int[] inputs)
    {
        Transaction[] transactions =
        [
            .. inputs.Select(static (input, i) => Build.A.Transaction.WithData(new byte[input]).WithNonce((ulong)i).WithHash(TestItem.Keccaks[i]).TestObject)
        ];
        return (transactions, Build.A.Block.WithTransactions(transactions).TestObject);
    }

    [Test, CancelAfter(30_000)]
    public void Parallel_tracer_that_fits_its_own_limit_waits_for_earlier_traces_rather_than_failing()
    {
        (Transaction[] transactions, Block block) = CallsWithInputs(60, 60, 60);
        ArrayBufferWriter<byte> sink = new();
        using Utf8JsonWriter writer = new(sink);
        using ParallelParityTxTracerPool pool = new(writer, CancellationToken.None, ParityTraceTypes.Trace, null, OneCallLimit, transactions.Length);
        pool.OnBlockStart(CancellationToken.None, waitForRoom: true);

        IBlockTracer<ParityLikeTxTrace> first = pool.For(transactions[0].Hash!);
        TraceCall(first, block, transactions[0]);
        IBlockTracer<ParityLikeTxTrace> second = pool.For(transactions[1].Hash!);
        Task tracing = Task.Run(() => TraceCall(second, block, transactions[1]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracing.Wait(TimeSpan.FromMilliseconds(200)), Is.False, "the second trace waits while the first is held");
            Assert.That(pool.RetainedBytes, Is.LessThanOrEqualTo(OneCallLimit), "a waiting trace keeps the tracers within the limit");
        }

        ((IDisposable)first).Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracing.Wait(TimeSpan.FromSeconds(10)), Is.True, "once the first trace is given back the second goes on");
            Assert.That(pool.RetainedBytes, Is.LessThanOrEqualTo(OneCallLimit));
        }

        ((IDisposable)second).Dispose();
        IBlockTracer<ParityLikeTxTrace> third = pool.For(transactions[2].Hash!);

        Assert.That(() => TraceCall(third, block, transactions[2]), Throws.Nothing);
        ((IDisposable)third).Dispose();
    }

    [Test]
    public void Earliest_parallel_tracer_never_waits_but_fails_past_its_own_limit()
    {
        (Transaction[] transactions, Block block) = CallsWithInputs(60, 60, 200);
        ArrayBufferWriter<byte> sink = new();
        using Utf8JsonWriter writer = new(sink);
        using ParallelParityTxTracerPool pool = new(writer, CancellationToken.None, ParityTraceTypes.Trace, null, OneCallLimit, transactions.Length);

        IBlockTracer<ParityLikeTxTrace> later = pool.For(transactions[1].Hash!);
        TraceCall(later, block, transactions[1]);
        IBlockTracer<ParityLikeTxTrace> earliest = pool.For(transactions[0].Hash!);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => TraceCall(earliest, block, transactions[0]), Throws.Nothing, "nothing before the earliest transaction can make room for it");
            Assert.That(pool.RetainedBytes, Is.GreaterThan(OneCallLimit).And.LessThanOrEqualTo(2 * OneCallLimit));
        }

        ((IDisposable)earliest).Dispose();
        ((IDisposable)later).Dispose();
        IBlockTracer<ParityLikeTxTrace> tooLarge = pool.For(transactions[2].Hash!);

        Assert.That(() => TraceCall(tooLarge, block, transactions[2]), Throws.TypeOf<TraceDataLimitExceededException>(),
            "a transaction over the limit fails as it does traced on its own");
        ((IDisposable)tooLarge).Dispose();
        pool.Dispose();
        Assert.That(pool.RetainedBytes, Is.Zero, "every byte charged, also by the failed charge, is given back");
    }

    [Test, CancelAfter(30_000)]
    public void Waiting_parallel_tracer_stops_when_the_workers_stop()
    {
        (Transaction[] transactions, Block block) = CallsWithInputs(60, 60);
        ArrayBufferWriter<byte> sink = new();
        using Utf8JsonWriter writer = new(sink);
        using CancellationTokenSource stop = new();
        using StreamingParityLikeBlockTracer stream = new(ParityTraceTypes.Trace, ParityTraceStreamMode.Replay, includeTxHash: true, writer, null, CancellationToken.None)
        {
            MaxRetainedBytes = OneCallLimit
        };
        using ParallelParityTxTracerPool pool = stream.CreateParallelTxTracers(block, ParityTraceTypes.Trace);
        stream.OnBlockStart(stop.Token, serializedAhead: true);

        IBlockTracer<ParityLikeTxTrace> first = pool.For(transactions[0].Hash!);
        TraceCall(first, block, transactions[0]);
        IBlockTracer<ParityLikeTxTrace> second = pool.For(transactions[1].Hash!);
        Task tracing = Task.Run(() => TraceCall(second, block, transactions[1]));
        Assert.That(tracing.Wait(TimeSpan.FromMilliseconds(200)), Is.False, "precondition: the second trace waits while the first is held");

        stop.Cancel();

        Assert.That(() => tracing.Wait(TimeSpan.FromSeconds(10)), Throws.InnerException.InstanceOf<OperationCanceledException>(),
            "the block's stop token reaches the pool through the stream");
        ((IDisposable)second).Dispose();
        ((IDisposable)first).Dispose();
        pool.Dispose();
        Assert.That(pool.RetainedBytes, Is.Zero, "every byte charged, also by the cancelled charge, is given back");
    }

    /// <summary>Traces written in turn are released only as the response is written, which a client that stops reading
    /// can hold indefinitely: a charge that waited for them would hold a processing permit for as long.</summary>
    [Test, CancelAfter(30_000)]
    public void Parallel_tracer_of_a_block_written_in_turn_fails_rather_than_waits_for_the_response()
    {
        (Transaction[] transactions, Block block) = CallsWithInputs(60, 60);
        ArrayBufferWriter<byte> sink = new();
        using Utf8JsonWriter writer = new(sink);
        using StreamingParityLikeBlockTracer stream = new(ParityTraceTypes.Trace, ParityTraceStreamMode.Store, includeTxHash: false, writer, null, CancellationToken.None)
        {
            MaxRetainedBytes = OneCallLimit
        };
        using ParallelParityTxTracerPool pool = stream.CreateParallelTxTracers(block, ParityTraceTypes.Trace);
        stream.OnBlockStart(CancellationToken.None, serializedAhead: false);

        IBlockTracer<ParityLikeTxTrace> first = pool.For(transactions[0].Hash!);
        TraceCall(first, block, transactions[0]);
        IBlockTracer<ParityLikeTxTrace> second = pool.For(transactions[1].Hash!);
        Task tracing = Task.Run(() => TraceCall(second, block, transactions[1]));

        Assert.That(() => tracing.Wait(TimeSpan.FromSeconds(10)), Throws.InnerException.TypeOf<TraceDataLimitExceededException>());
        ((IDisposable)second).Dispose();
        ((IDisposable)first).Dispose();
        pool.Dispose();
        Assert.That(pool.RetainedBytes, Is.Zero, "every byte charged, also by the failed charge, is given back");
    }

    /// <summary>A tracer given back while a charge waits drops the inputs it would keep for reuse, rather than charge them
    /// again and keep the room the charge waits for.</summary>
    [Test, CancelAfter(30_000)]
    public void Tracer_given_back_while_a_charge_waits_drops_its_pooled_inputs()
    {
        // The third trace's action fits beside the first two traces; its input fits once the second trace is given
        // back, but not beside the second trace's input kept for reuse.
        (Transaction[] transactions, Block block) = CallsWithInputs(64, 128, 256);
        ArrayBufferWriter<byte> sink = new();
        using Utf8JsonWriter writer = new(sink);
        const long limit = 800;
        using ParallelParityTxTracerPool pool = new(writer, CancellationToken.None, ParityTraceTypes.Trace, null, limit, transactions.Length);
        pool.OnBlockStart(CancellationToken.None, waitForRoom: true);

        IBlockTracer<ParityLikeTxTrace> earliest = pool.For(transactions[0].Hash!);
        TraceCall(earliest, block, transactions[0]);
        IBlockTracer<ParityLikeTxTrace> second = pool.For(transactions[1].Hash!);
        TraceCall(second, block, transactions[1]);
        IBlockTracer<ParityLikeTxTrace> third = pool.For(transactions[2].Hash!);
        Task tracing = Task.Run(() => TraceCall(third, block, transactions[2]));
        Assert.That(tracing.Wait(TimeSpan.FromMilliseconds(200)), Is.False, "precondition: the third trace waits while the first two are held");

        ((IDisposable)second).Dispose();

        Assert.That(tracing.Wait(TimeSpan.FromSeconds(10)), Is.True, "the second trace's room goes to the waiting charge while the earliest is still held");
        ((IDisposable)third).Dispose();
        ((IDisposable)earliest).Dispose();
    }

    private static void TraceCall(IBlockTracer<ParityLikeTxTrace> tracer, Block block, Transaction tx)
    {
        tracer.StartNewBlockTrace(block);
        ITxTracer txTracer = tracer.StartNewTxTrace(tx);
        txTracer.ReportAction(100000, UInt256.Zero, TestItem.AddressA, TestItem.AddressB, tx.Data, ExecutionType.TRANSACTION);
        txTracer.ReportActionEnd(1000, ReadOnlyMemory<byte>.Empty);
        txTracer.MarkAsSuccess(TestItem.AddressB, default, [], []);
        tracer.EndTxTrace();
        tracer.EndBlockTrace();
    }

    [Test]
    public void Idle_parallel_tracers_drop_their_pooled_inputs_before_a_charge_waits()
    {
        (Transaction[] transactions, Block block) = CallsWithInputs(60, 60, 60, 512);
        ArrayBufferWriter<byte> sink = new();
        using Utf8JsonWriter writer = new(sink);
        const long limit = 3 * (ParityLikeTxTracer.RetainedActionBytes + 64);
        using ParallelParityTxTracerPool pool = new(writer, CancellationToken.None, ParityTraceTypes.Trace, null, limit, transactions.Length);

        IBlockTracer<ParityLikeTxTrace>[] first = [.. transactions.Take(3).Select(tx => pool.For(tx.Hash!))];
        for (int i = 0; i < first.Length; i++) TraceCall(first[i], block, transactions[i]);
        foreach (IBlockTracer<ParityLikeTxTrace> tracer in first) ((IDisposable)tracer).Dispose();
        long keptByIdleTracers = pool.RetainedBytes;
        IBlockTracer<ParityLikeTxTrace> last = pool.For(transactions[3].Hash!);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(keptByIdleTracers, Is.EqualTo(3 * 64), "guards the premise: idle tracers keep their inputs charged");
            Assert.That(() => TraceCall(last, block, transactions[3]), Throws.Nothing);
            Assert.That(pool.RetainedBytes, Is.LessThanOrEqualTo(limit), "the idle tracers' inputs made room");
        }

        ((IDisposable)last).Dispose();
    }
}
