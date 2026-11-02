// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Tracing;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Tracing;

public class OrderedTraceEmitterTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test]
    public void Emitter_WhenWorkersFinishInAnyOrder_WritesEveryTransactionOnceInBlockOrder([Values] bool serializeAhead, [Range(0, 9)] int seed)
    {
        const int transactions = 300;
        const int workers = 8;
        RecordingStream stream = new(serializeAhead);
        using ParallelTraceResults<int> emitter = Create(stream, transactions, window: 2 * workers);
        int cursor = -1;

        Task[] tasks = new Task[workers];
        for (int w = 0; w < workers; w++)
        {
            Random random = new(seed * workers + w);
            tasks[w] = Task.Factory.StartNew(() =>
            {
                while (true)
                {
                    emitter.Reserve(CancellationToken.None);
                    int index = Interlocked.Increment(ref cursor);
                    if (index >= transactions)
                    {
                        emitter.Unreserve();
                        return;
                    }

                    if (random.Next(4) == 0) Thread.Sleep(random.Next(3));
                    else Thread.SpinWait(random.Next(2_000));
                    emitter.Add(index, new Traces(index));
                }
            }, TaskCreationOptions.LongRunning);
        }

        Assert.That(Task.WaitAll(tasks, Timeout), Is.True, "every worker returns: none is left waiting for a place in the window");
        string written = stream.Json();
        emitter.Complete();
        emitter.Add(transactions, new Traces(-1));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(written, Is.EqualTo($"[{string.Join(',', Enumerable.Range(0, transactions))}]"), "each transaction is written once, in block order, before the workers return");
            Assert.That(stream.Json(), Is.EqualTo($"[{string.Join(',', Enumerable.Range(0, transactions))},-1]"), "the rewards come last");
            Assert.That(stream.OverlappingWrites, Is.Zero, "the stream is written by one thread at a time");
        }
    }

    /// <summary>Two workers store the only two slots at once, again and again: whichever order their stores and their
    /// attempts to take the emission interleave in, both slots are written before they return.</summary>
    [Test]
    public void Emitter_WhenTwoWorkersStoreAtOnce_LeavesNoSlotUnwritten([Values] bool serializeAhead)
    {
        using Barrier start = new(2);
        for (int round = 0; round < 5_000; round++)
        {
            RecordingStream stream = new(serializeAhead);
            using ParallelTraceResults<int> emitter = Create(stream, transactions: 2, window: 2);
            Reserve(emitter, 2);

            Task second = Task.Factory.StartNew(() =>
            {
                start.SignalAndWait(Timeout);
                emitter.Add(1, new Traces(1));
            }, TaskCreationOptions.LongRunning);
            start.SignalAndWait(Timeout);
            emitter.Add(0, new Traces(0));
            Assert.That(second.Wait(Timeout), Is.True);

            emitter.Complete();
            Assert.That(stream.Json(), Is.EqualTo("[0,1]"), $"round {round}");
        }
    }

    [Test]
    public void Emitter_WhileOneWorkerIsBlockedWriting_LetsTheOthersFinishWithoutWaiting([Values] bool serializeAhead)
    {
        using ManualResetEventSlim writing = new(false);
        using ManualResetEventSlim release = new(false);
        RecordingStream stream = new(serializeAhead, beforeWrite: index =>
        {
            if (index != 0) return;
            writing.Set();
            release.Wait(Timeout);
        });
        using ParallelTraceResults<int> emitter = Create(stream, transactions: 4, window: 4);

        Reserve(emitter, 4);
        Task blocked = Task.Factory.StartNew(() => emitter.Add(0, new Traces(0)), TaskCreationOptions.LongRunning);
        try
        {
            Assert.That(writing.Wait(Timeout), Is.True, "precondition: the first transaction holds the emission while its write blocks");
            Task others = Task.Run(() =>
            {
                emitter.Add(2, new Traces(2));
                emitter.Add(1, new Traces(1));
                emitter.Add(3, new Traces(3));
            });
            Assert.That(others.Wait(Timeout), Is.True, "workers that finish while the emission is held store their traces and go back to the cursor");
            using CancellationTokenSource full = new(TimeSpan.FromMilliseconds(200));
            Assert.Throws<OperationCanceledException>(() => emitter.Reserve(full.Token),
                "a transaction whose write has not finished still holds its place, so a client that stops reading bounds the window");
        }
        finally
        {
            release.Set();
        }

        Assert.That(blocked.Wait(Timeout), Is.True);
        emitter.Complete();
        Assert.That(stream.Json(), Is.EqualTo("[0,1,2,3]"), "the holder writes what the others left before it lets go");
    }

    [Test]
    public void Emitter_SerializesEachTransactionOnTheWorkerThatTracedIt_ConcurrentlyWithTheOthers()
    {
        using Barrier bothSerializing = new(2);
        int met = 0;
        RecordingStream stream = new(serializeAhead: true, beforeSerialize: _ =>
        {
            if (bothSerializing.SignalAndWait(Timeout)) Interlocked.Increment(ref met);
        });
        using ParallelTraceResults<int> emitter = Create(stream, transactions: 2, window: 4);
        Reserve(emitter, 2);

        Task first = Task.Factory.StartNew(() => emitter.Add(0, new Traces(0)), TaskCreationOptions.LongRunning);
        Task second = Task.Factory.StartNew(() => emitter.Add(1, new Traces(1)), TaskCreationOptions.LongRunning);

        Assert.That(Task.WaitAll([first, second], Timeout), Is.True);
        emitter.Complete();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(met, Is.EqualTo(2), "two workers serialize their traces at the same time, outside any lock");
            Assert.That(stream.Json(), Is.EqualTo("[0,1]"));
        }
    }

    [Test]
    public void Emitter_WhenTheOldestTransactionIsSlow_BoundsTheTransactionsTracedAhead([Values] bool serializeAhead)
    {
        const int window = 4;
        RecordingStream stream = new(serializeAhead);
        using ParallelTraceResults<int> emitter = Create(stream, transactions: 16, window);

        Reserve(emitter, window);
        for (int i = 1; i < window; i++) emitter.Add(i, new Traces(i));

        using (CancellationTokenSource blocked = new(TimeSpan.FromMilliseconds(200)))
        {
            Assert.Throws<OperationCanceledException>(() => emitter.Reserve(blocked.Token),
                "with the oldest transaction unwritten, the window is full and no further transaction is taken");
        }

        Assert.That(stream.Json(), Is.EqualTo("[]"), "nothing is written ahead of the oldest transaction");
        emitter.Add(0, new Traces(0));

        using CancellationTokenSource reserved = new(Timeout);
        for (int i = 0; i < window; i++) emitter.Reserve(reserved.Token);
        Assert.That(stream.Json(), Is.EqualTo("[0,1,2,3]"), "writing the oldest transaction frees a place for each transaction written");
    }

    [Test]
    public void Emitter_WhenAWriteFails_WritesNothingMoreAndLeavesTheRestToBeReleased()
    {
        int disposed = 0;
        RecordingStream stream = new(serializeAhead: false, beforeWrite: index =>
        {
            if (index == 1) throw new IOException("client went away");
        });
        using ParallelTraceResults<int> emitter = Create(stream, transactions: 4, window: 8);
        Reserve(emitter, 4);

        emitter.Add(0, new Traces(0) { OnDispose = () => Interlocked.Increment(ref disposed) });
        emitter.Add(2, new Traces(2) { OnDispose = () => Interlocked.Increment(ref disposed) });
        Assert.Throws<IOException>(() => emitter.Add(1, new Traces(1) { OnDispose = () => Interlocked.Increment(ref disposed) }));
        emitter.Add(3, new Traces(3) { OnDispose = () => Interlocked.Increment(ref disposed) });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stream.Json(), Is.EqualTo("[0]"), "nothing is written to a response after a write to it failed");
            Assert.That(disposed, Is.EqualTo(2), "the written traces and the one whose write failed are let go of by the stream");
            Assert.Throws<InvalidOperationException>(emitter.Complete, "a run that did not write every transaction is not complete");
        }

        emitter.DisposeUnpublished();
        Assert.That(disposed, Is.EqualTo(4), "what was traced and never written is released once");
        emitter.DisposeUnpublished();
        Assert.That(disposed, Is.EqualTo(4), "and only once");
    }

    [Test]
    public void Emitter_WhenSerializingFails_LeavesNothingInTheSlot()
    {
        RecordingStream stream = new(serializeAhead: true, beforeSerialize: index =>
        {
            if (index == 0) throw new OperationCanceledException();
        });
        using ParallelTraceResults<int> emitter = Create(stream, transactions: 2, window: 4);
        Reserve(emitter, 2);

        Assert.Throws<OperationCanceledException>(() => emitter.Add(0, new Traces(0)));
        emitter.Add(1, new Traces(1));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stream.Json(), Is.EqualTo("[]"), "a transaction after one that was never stored waits for it");
            Assert.Throws<InvalidOperationException>(emitter.Complete);
        }

        emitter.DisposeUnpublished();
    }

    [Test]
    public void Emitter_WhenATransactionHasNoTraces_WritesNoElementForIt([Values] bool serializeAhead)
    {
        RecordingStream stream = new(serializeAhead);
        using ParallelTraceResults<int> emitter = Create(stream, transactions: 3, window: 3);
        Reserve(emitter, 3);

        emitter.Add(0, new Traces(0));
        emitter.Add(1, new Traces());
        emitter.Add(2, new Traces(2));
        emitter.Complete();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stream.Json(), Is.EqualTo("[0,2]"), "an empty transaction leaves no separator behind");
            Assert.That(stream.EmptyWrites, Is.Zero, "the stream is never handed an empty span");
        }
    }

    [TestCase(false, 0, 64, 64, TestName = "SerializeAheadOptions_AtTheTopLevel_KeepsTheWholeDepth")]
    [TestCase(false, 2, 64, 62, TestName = "SerializeAheadOptions_InsideTheResponse_KeepsTheDepthLeft")]
    [TestCase(false, 64, 64, null, TestName = "SerializeAheadOptions_AtTheMaximumDepth_SerializesInTurn")]
    [TestCase(true, 1, 64, null, TestName = "SerializeAheadOptions_WhenIndented_SerializesInTurn")]
    public void SerializeAheadOptions_MatchTheResponseWriterAtItsDepth(bool indented, int depth, int maxDepth, int? expectedMaxDepth)
    {
        ArrayBufferWriter<byte> output = new();
        using Utf8JsonWriter writer = new(output, new JsonWriterOptions { Indented = indented, MaxDepth = maxDepth, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        for (int i = 0; i < depth; i++) writer.WriteStartArray();

        JsonWriterOptions? options = ParallelTraceStream.SerializeAheadOptions(writer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(options?.MaxDepth, Is.EqualTo(expectedMaxDepth));
            if (options is { } ahead)
            {
                Assert.That(ahead.Encoder, Is.SameAs(JavaScriptEncoder.UnsafeRelaxedJsonEscaping), "escaped as the response escapes");
                Assert.That(ahead.SkipValidation, Is.True, "a transaction may hold several elements");
                Assert.That(ahead.Indented, Is.False);
            }
        }
    }

    [Test]
    public void SerializedTraces_ReusedAfterALargerTrace_HoldsOnlyItsOwnBytes()
    {
        SerializedTraces.Pool pool = new(capacity: 1, retainedBytes: SerializedTraces.ChunkSize);
        string large = new('x', 100_000);

        SerializedTraces first = pool.Rent(Options);
        Utf8JsonWriter writer = first.Start(Options);
        writer.WriteStringValue(large);
        writer.Flush();
        long largeLength = first.Length;
        pool.Return(first);

        SerializedTraces second = pool.Rent(Options);
        Utf8JsonWriter reused = second.Start(Options);
        reused.WriteNumberValue(7);
        reused.WriteNumberValue(8);
        reused.Flush();
        second.Seal();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(largeLength, Is.EqualTo(large.Length + 2), "precondition: the large trace outgrew a chunk");
            Assert.That(second, Is.SameAs(first), "the instance is reused");
            Assert.That(reused, Is.SameAs(writer), "with its writer, when the options are the same");
            Assert.That(Encoding.UTF8.GetString(second.Written.ToArray()), Is.EqualTo("7,8"), "a reused instance starts empty, and separates its elements as the response writer would");
        }
    }

    [Test]
    public void SerializedTraces_ALargeTrace_IsHeldInChunksAndReadsBackWhole()
    {
        SerializedTraces.Pool pool = new(capacity: 1, retainedBytes: SerializedTraces.ChunkSize);
        SerializedTraces serialized = pool.Rent(Options);
        Utf8JsonWriter writer = serialized.Start(Options);
        StringBuilder expected = new();
        for (int i = 0; i < 20_000; i++)
        {
            writer.WriteNumberValue(i);
            expected.Append(i == 0 ? "" : ",").Append(i);
        }

        writer.Flush();
        serialized.Seal();
        ReadOnlySequence<byte> written = serialized.Written;
        int segments = 0;
        bool bounded = true;
        foreach (ReadOnlyMemory<byte> segment in written)
        {
            segments++;
            bounded &= segment.Length <= SerializedTraces.ChunkSize;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(segments, Is.GreaterThan(1), "precondition: the trace spans several chunks");
            Assert.That(bounded, Is.True, "no chunk is larger than the chunk size when no single token needs more");
            Assert.That(Encoding.UTF8.GetString(written.ToArray()), Is.EqualTo(expected.ToString()));
        }
    }

    [Test]
    public void SerializedTracesPool_KeepsChunksOnlyUpToItsCap_AndReusesThem()
    {
        SerializedTraces.Pool pool = new(capacity: 2, retainedBytes: 2 * SerializedTraces.ChunkSize);
        SerializedTraces large = Filled(pool, 5 * SerializedTraces.ChunkSize);
        large.Seal();
        int chunks = 0;
        foreach (ReadOnlyMemory<byte> _ in large.Written) chunks++;
        pool.Return(large);
        int stored = pool.StoredChunks;

        SerializedTraces small = Filled(pool, 100);
        int afterReuse = pool.StoredChunks;
        pool.Return(small);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chunks, Is.GreaterThan(2), "precondition: the trace used more chunks than the cap keeps");
            Assert.That(stored, Is.EqualTo(2), "only the capped bytes of chunks stay after the request");
            Assert.That(afterReuse, Is.EqualTo(1), "the next trace takes a kept chunk instead of renting one");
            Assert.That(pool.StoredChunks, Is.EqualTo(2));
        }

        static SerializedTraces Filled(SerializedTraces.Pool pool, int bytes)
        {
            SerializedTraces serialized = pool.Rent(Options);
            Utf8JsonWriter writer = serialized.Start(Options);
            for (int written = 0; written < bytes; written += 6) writer.WriteNumberValue(99_999);
            writer.Flush();
            return serialized;
        }
    }

    [Test]
    public void SerializedTracesPool_RentsTheInstanceWhoseWriterHasTheOptions()
    {
        SerializedTraces.Pool pool = new(capacity: 2, retainedBytes: SerializedTraces.ChunkSize);
        JsonWriterOptions deeper = new() { SkipValidation = true, MaxDepth = 32 };
        SerializedTraces shallow = pool.Rent(Options);
        Utf8JsonWriter shallowWriter = shallow.Start(Options);
        SerializedTraces deep = pool.Rent(deeper);
        deep.Start(deeper);
        pool.Return(shallow);
        pool.Return(deep);

        SerializedTraces rented = pool.Rent(Options);
        Utf8JsonWriter writer = rented.Start(Options);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rented, Is.SameAs(shallow), "streams that serialize at different depths share the pool");
            Assert.That(writer, Is.SameAs(shallowWriter), "and each keeps the writer it already has");
        }
    }

    [Test]
    public void GethLikeTraceStream_ReturnedOnAThread_IsReusedByTheNextRequestOnIt()
    {
        ArrayBufferWriter<byte> output = new();
        using Utf8JsonWriter writer = new(output);
        GethLikeTraceStream first = GethLikeTraceStream.Rent(writer, null, CancellationToken.None);
        first.Return();
        GethLikeTraceStream second = GethLikeTraceStream.Rent(writer, null, CancellationToken.None);
        second.Return();

        Assert.That(second, Is.SameAs(first));
    }

    [Test]
    public void TraceStreamOutput_WhenCancelled_WritesNothingMore()
    {
        ArrayBufferWriter<byte> output = new();
        using Utf8JsonWriter writer = new(output, new JsonWriterOptions { SkipValidation = true });
        using CancellationTokenSource cancellation = new();
        TraceStreamOutput stream = new(writer, null, cancellation.Token, int.MaxValue);
        writer.WriteStartArray();
        stream.WriteSerialized(new ReadOnlySequence<byte>("1"u8.ToArray()));
        cancellation.Cancel();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => stream.WriteSerialized(new ReadOnlySequence<byte>("2"u8.ToArray())), Throws.InstanceOf<OperationCanceledException>());
            writer.Flush();
            Assert.That(Encoding.UTF8.GetString(output.WrittenSpan), Is.EqualTo("[1"));
        }
    }

    [Test]
    public void TraceStreamOutput_WithElementsOfAnySize_WritesTheBytesTheWriterWouldWrite([Values] bool flushed)
    {
        string[] elements = ["small", new('l', 3 * TraceStreamOutput.ContiguousLimitBytes), "after", new('m', TraceStreamOutput.ContiguousLimitBytes)];
        int[] numbers = new int[5_000];
        for (int i = 0; i < numbers.Length; i++) numbers[i] = i;

        ArrayBufferWriter<byte> expected = new();
        using (Utf8JsonWriter direct = new(expected, new JsonWriterOptions { SkipValidation = true }))
        {
            direct.WriteStartArray();
            foreach (string element in elements) direct.WriteStringValue(element);
            foreach (int number in numbers) direct.WriteNumberValue(number);
            direct.WriteEndArray();
        }

        SerializedTraces.Pool pool = new(capacity: 1, retainedBytes: SerializedTraces.ChunkSize);
        Pipe pipe = new(new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0));
        using (Utf8JsonWriter writer = new(pipe.Writer, new JsonWriterOptions { SkipValidation = true }))
        {
            writer.WriteStartArray();
            TraceStreamOutput output = new(writer, pipe.Writer, CancellationToken.None, flushed ? 1 : int.MaxValue);
            foreach (string element in elements)
            {
                SerializedTraces serialized = pool.Rent(Options);
                Utf8JsonWriter alone = serialized.Start(Options);
                alone.WriteStringValue(element);
                alone.Flush();
                serialized.Seal();
                output.WriteSerialized(serialized.Written);
                pool.Return(serialized);
            }

            SerializedTraces many = pool.Rent(Options);
            Utf8JsonWriter several = many.Start(Options);
            foreach (int number in numbers) several.WriteNumberValue(number);
            several.Flush();
            many.Seal();
            Assert.That(many.Written.IsSingleSegment, Is.False, "precondition: one transaction's elements span several chunks");
            output.WriteSerialized(many.Written);
            pool.Return(many);

            writer.WriteEndArray();
        }

        pipe.Writer.Complete();
        pipe.Reader.TryRead(out ReadResult read);
        Assert.That(read.Buffer.ToArray(), Is.EqualTo(expected.WrittenSpan.ToArray()), "chunks and large elements pass the writer by and still land between the same separators");
        pipe.Reader.AdvanceTo(read.Buffer.End);
    }

    [Test]
    public void Emitter_WhenTheByteBudgetIsSpent_WaitsForTheWriteBeforeTakingAnotherTransaction()
    {
        RecordingStream stream = new(serializeAhead: true);
        using SerializingTraceEmitter<int> emitter = new();
        emitter.Start(stream, stream.SerializeAheadOptions!.Value, new SerializedTraces.Pool(8, SerializedTraces.ChunkSize), transactions: 4, window: 8, budgetBytes: 1);
        Reserve(emitter, 2);

        emitter.Add(1, new Traces(11, 12));
        long pending = emitter.PendingBytes;
        using (CancellationTokenSource blocked = new(TimeSpan.FromMilliseconds(200)))
        {
            Assert.Throws<OperationCanceledException>(() => emitter.Reserve(blocked.Token),
                "with more bytes waiting than the budget, no further transaction is taken although the window has room");
        }

        emitter.Add(0, new Traces(0));
        using CancellationTokenSource reserved = new(Timeout);
        emitter.Reserve(reserved.Token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pending, Is.EqualTo("11,12".Length), "precondition: the bytes of the transaction waiting behind the first one");
            Assert.That(emitter.PendingBytes, Is.Zero, "writing gives the bytes back");
            Assert.That(stream.Json(), Is.EqualTo("[0,11,12]"));
        }
    }

    [Test]
    public void Emitter_ReusedForTheNextBlock_HasTheWindowOfThatBlock([Values] bool serializeAhead)
    {
        RecordingStream stream = new(serializeAhead);
        SerializedTraces.Pool pool = new(8, SerializedTraces.ChunkSize);
        using SerializingTraceEmitter<int> ahead = new();
        using InTurnTraceEmitter<int> inTurn = new();
        ParallelTraceResults<int> emitter = serializeAhead ? ahead : inTurn;

        Start(window: 4);
        Reserve(emitter, 2);
        emitter.Add(0, new Traces(0));
        emitter.Add(1, new Traces(1));
        emitter.Complete();
        emitter.Clear();

        Start(window: 3);
        Reserve(emitter, 3);
        using CancellationTokenSource full = new(TimeSpan.FromMilliseconds(200));
        Assert.Throws<OperationCanceledException>(() => emitter.Reserve(full.Token), "the second block has three places, not what the first left");
        emitter.Clear();

        void Start(int window)
        {
            if (serializeAhead) ahead.Start(stream, stream.SerializeAheadOptions!.Value, pool, transactions: 2, window, budgetBytes: long.MaxValue);
            else inTurn.Start(stream, transactions: 2, window);
        }
    }

    /// <summary>The emission of warm blocks allocates nothing: the slots, the window, the serialized chunks and their
    /// writers are all reused, and the response is written through <see cref="TraceStreamOutput"/>.</summary>
    [Test]
    public void Emitter_ForWarmBlocks_AllocatesNothing()
    {
        const int transactions = 64;
        RecordingStream stream = new(serializeAhead: true, throughOutput: true);
        SerializedTraces.Pool pool = new(2 * transactions, 64 * SerializedTraces.ChunkSize);
        using SerializingTraceEmitter<int> emitter = new();
        int[][] traces = new int[transactions][];
        for (int i = 0; i < transactions; i++) traces[i] = [i, i + 1];
        JsonWriterOptions options = stream.SerializeAheadOptions!.Value;

        for (int warm = 0; warm < 3; warm++) TraceBlock();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int block = 0; block < 100; block++) TraceBlock();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.That(allocated, Is.Zero);

        void TraceBlock()
        {
            stream.Restart();
            emitter.Start(stream, options, pool, transactions, window: 8, budgetBytes: 1024 * 1024);
            for (int i = 0; i < transactions; i++)
            {
                emitter.Reserve(CancellationToken.None);
                // Out of order within the window: each odd transaction waits for the even one before it.
                int index = i % 2 == 0 && i + 1 < transactions ? i + 1 : i % 2 == 1 ? i - 1 : i;
                emitter.Add(index, traces[index]);
            }

            emitter.Complete();
            emitter.Clear();
        }
    }

    /// <summary>Takes the places in the window that the transactions a test adds would have taken off the cursor.</summary>
    private static void Reserve(ParallelTraceResults<int> emitter, int transactions)
    {
        for (int i = 0; i < transactions; i++) emitter.Reserve(CancellationToken.None);
    }

    private static readonly JsonWriterOptions Options = new() { SkipValidation = true, MaxDepth = 64 };

    private static ParallelTraceResults<int> Create(RecordingStream stream, int transactions, int window)
    {
        if (stream.SerializeAheadOptions is { } options)
        {
            SerializingTraceEmitter<int> ahead = new();
            ahead.Start(stream, options, new SerializedTraces.Pool(window, window * SerializedTraces.ChunkSize), transactions, window, budgetBytes: long.MaxValue);
            return ahead;
        }

        InTurnTraceEmitter<int> inTurn = new();
        inTurn.Start(stream, transactions, window);
        return inTurn;
    }

    private sealed class Traces(params int[] values) : List<int>(values), IDisposable
    {
        private int _disposed;

        public Action OnDispose { get; init; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) OnDispose?.Invoke();
        }
    }

    /// <summary>Writes each trace as a number into one JSON array, and counts any two writes that overlap.</summary>
    private sealed class RecordingStream : IParallelTraceStream<int>
    {
        private readonly ArrayBufferWriter<byte> _output = new(1024 * 1024);
        private readonly Utf8JsonWriter _writer;
        private readonly bool _serializeAhead;
        private readonly bool _throughOutput;
        private TraceStreamOutput _streamOutput;
        private readonly Action<int> _beforeWrite;
        private readonly Action<int> _beforeSerialize;
        private int _writing;
        private int _overlapping;
        private int _emptyWrites;

        public RecordingStream(bool serializeAhead, Action<int> beforeWrite = null, Action<int> beforeSerialize = null, bool throughOutput = false)
        {
            _serializeAhead = serializeAhead;
            _beforeWrite = beforeWrite;
            _beforeSerialize = beforeSerialize;
            _throughOutput = throughOutput;
            _writer = new Utf8JsonWriter(_output, new JsonWriterOptions { SkipValidation = true });
            Restart();
        }

        /// <summary>Starts a new response in the same buffers.</summary>
        public void Restart()
        {
            _output.ResetWrittenCount();
            _writer.Reset();
            _writer.WriteStartArray();
            _streamOutput = new TraceStreamOutput(_writer, null, CancellationToken.None, int.MaxValue);
        }

        public int OverlappingWrites => Volatile.Read(ref _overlapping);

        public int EmptyWrites => Volatile.Read(ref _emptyWrites);

        public JsonWriterOptions? SerializeAheadOptions => _serializeAhead ? ParallelTraceStream.SerializeAheadOptions(_writer) : null;

        public void Serialize(IReadOnlyCollection<int> traces, Utf8JsonWriter writer)
        {
            try
            {
                IReadOnlyList<int> list = (IReadOnlyList<int>)traces;
                for (int i = 0; i < list.Count; i++)
                {
                    _beforeSerialize?.Invoke(list[i]);
                    writer.WriteNumberValue(list[i]);
                }
            }
            finally
            {
                traces.TryDispose();
            }
        }

        public void WriteSerialized(in ReadOnlySequence<byte> elements)
        {
            Enter();
            try
            {
                if (elements.IsEmpty) Interlocked.Increment(ref _emptyWrites);
                Utf8Parser.TryParse(elements.FirstSpan, out int first, out _);
                _beforeWrite?.Invoke(first);
                if (_throughOutput) _streamOutput.WriteSerialized(elements);
                else _writer.WriteRawValue(elements, skipInputValidation: true);
            }
            finally
            {
                Interlocked.Decrement(ref _writing);
            }
        }

        public void Write(IReadOnlyCollection<int> traces)
        {
            Enter();
            try
            {
                IReadOnlyList<int> list = (IReadOnlyList<int>)traces;
                for (int i = 0; i < list.Count; i++) _beforeWrite?.Invoke(list[i]);
                Serialize(traces, _writer);
            }
            finally
            {
                traces.TryDispose();
                Interlocked.Decrement(ref _writing);
            }
        }

        public string Json()
        {
            _writer.Flush();
            return Encoding.UTF8.GetString(_output.WrittenSpan) + "]";
        }

        private void Enter()
        {
            if (Interlocked.Increment(ref _writing) != 1) Interlocked.Increment(ref _overlapping);
        }
    }
}
