// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Protocols;
using NSubstitute;

namespace Nethermind.BeaconChain.Test.P2P;

[CancelAfter(10_000)]
public class ChannelTransportTests
{
    public enum NegotiationFailure
    {
        UnsupportedProtocol,
        WrongHello,
        ResetBeforeHello,
        ResetAfterHello,
    }

    [Test]
    public async Task Rejected_selection_completes_the_pending_request([Values] NegotiationFailure failure,
        [Values] bool alreadyCompleted, CancellationToken token)
    {
        Channel channel = new();
        IChannel remote = channel.Reverse;
        IConnectionContext context = Substitute.For<IConnectionContext>();
        ISessionProtocol<int, int> selected = Substitute.For<ISessionProtocol<int, int>>();
        selected.Id.Returns("/test/multistream/rejected/1");
        TaskCompletionSource<object?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (alreadyCompleted) completion.SetResult(42);
        context.UpgradeOptions.Returns(new UpgradeOptions { SelectedProtocol = selected, Argument = 1, CompletionSource = completion });
        MultistreamProtocol protocol = new();
        Task negotiation = protocol.DialAsync(channel, context);
        try
        {
            Assert.That(await remote.ReadLineAsync().WaitAsync(token), Is.EqualTo(protocol.Id));
            Assert.That(await remote.ReadLineAsync().WaitAsync(token), Is.EqualTo(selected.Id));
            if (failure != NegotiationFailure.ResetBeforeHello)
                await remote.WriteLineAsync(failure == NegotiationFailure.WrongHello ? "/wrong/multistream" : protocol.Id);
            if (failure == NegotiationFailure.UnsupportedProtocol) await remote.WriteLineAsync("na");
            if (failure is NegotiationFailure.ResetBeforeHello or NegotiationFailure.ResetAfterHello) await remote.AbortAsync();
            await negotiation.WaitAsync(token);

            if (alreadyCompleted)
            {
                Assert.That(await completion.Task, Is.EqualTo(42), "rejection cannot overwrite an existing outcome");
            }
            else
            {
                Assert.That(completion.Task.IsFaulted, Is.True, "negotiation rejection must fail the pending request before its caller times out");
                Assert.That(completion.Task.Exception!.InnerException, Is.TypeOf<ChannelClosedException>());
            }
            await context.DidNotReceiveWithAnyArgs().Upgrade(default!, default(IProtocol)!);
        }
        finally
        {
            await remote.AbortAsync();
            await negotiation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    public async Task Successful_selection_does_not_fault_a_queued_completion([Values] bool typed,
        [Values] bool asynchronous, CancellationToken token)
    {
        Channel channel = new();
        IChannel remote = channel.Reverse;
        IConnectionContext context = Substitute.For<IConnectionContext>();
        IProtocol selected = typed ? Substitute.For<ISessionProtocol<int, int>>() : Substitute.For<ISessionProtocol>();
        selected.Id.Returns("/test/multistream/accepted/1");
        TaskCompletionSource<object?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource dispatched = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource handler = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.UpgradeOptions.Returns(new UpgradeOptions { SelectedProtocol = selected, Argument = typed ? 1 : null, CompletionSource = completion });
        context.Upgrade(channel, selected).Returns(_ =>
        {
            dispatched.TrySetResult();
            return asynchronous ? handler.Task : Task.CompletedTask;
        });
        MultistreamProtocol protocol = new();
        Task negotiation = protocol.DialAsync(channel, context);
        try
        {
            Assert.That(await remote.ReadLineAsync().WaitAsync(token), Is.EqualTo(protocol.Id));
            Assert.That(await remote.ReadLineAsync().WaitAsync(token), Is.EqualTo(selected.Id));
            await remote.WriteLineAsync(protocol.Id);
            await remote.WriteLineAsync(selected.Id);
            await dispatched.Task.WaitAsync(token);
            handler.TrySetResult();
            await negotiation.WaitAsync(token);

            Assert.That(completion.Task.IsCompleted, Is.False, "the Core success-mapping continuation may still be queued after its handler completes");
            object? expected = typed ? 42 : null;
            completion.SetResult(expected);
            Assert.That(await completion.Task, Is.EqualTo(expected));
        }
        finally
        {
            handler.TrySetResult();
            await remote.AbortAsync();
            await negotiation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    public async Task Rejection_does_not_overwrite_caller_cancellation(CancellationToken token)
    {
        Channel channel = new();
        IChannel remote = channel.Reverse;
        IConnectionContext context = Substitute.For<IConnectionContext>();
        IProtocol selected = Substitute.For<IProtocol>();
        selected.Id.Returns("/test/multistream/canceled/1");
        TaskCompletionSource<object?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.UpgradeOptions.Returns(new UpgradeOptions { SelectedProtocol = selected, CompletionSource = completion });
        MultistreamProtocol protocol = new();
        Task negotiation = protocol.DialAsync(channel, context);
        try
        {
            await remote.ReadLineAsync().WaitAsync(token);
            await remote.ReadLineAsync().WaitAsync(token);
            completion.SetCanceled();
            await remote.AbortAsync();
            await negotiation.WaitAsync(token);

            Assert.That(completion.Task.IsCanceled, Is.True);
            await context.DidNotReceiveWithAnyArgs().Upgrade(default!, default(IProtocol)!);
        }
        finally
        {
            await remote.AbortAsync();
            await negotiation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    // Each chunk is one yamux frame as it is handed up, given by its segment lengths: Noise frames of up to 65,535 bytes, and small frames.
    private static readonly int[][][] Chunkings =
    [
        [[65535, 65535, 65535, 65535], [20], [65535, 65535, 30000]],
        [[16], [7, 9, 11], [65535, 3], [1], [40000, 40000, 40000]],
    ];

    [Test]
    public Task An_exact_length_read_receives_every_segment_of_every_chunk([ValueSource(nameof(Chunkings))] int[][] chunks, CancellationToken token) =>
        AssertChannelAsync(chunks, token);

    [Test]
    public async Task A_full_close_above_closes_the_channel_below(CancellationToken token)
    {
        Channel channel = new();

        await channel.Reverse.CloseAsync();

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That((await channel.ReadAsync(1, ReadBlockingMode.WaitAny, token)).Result, Is.EqualTo(IOResult.Ended));
        Assert.That(channel.GetAwaiter().IsCompleted, Is.True);
    }

    // The write acknowledgement and close can race with the reader continuation, so the check repeats.
    [Test]
    [CancelAfter(30_000)]
    public async Task A_response_written_just_before_a_full_close_reaches_the_peer(CancellationToken token)
    {
        for (int attempt = 0; attempt < 5_000; attempt++)
        {
            Channel channel = new();
            Task<ReadResult> received = channel.ReadAsync(3, ReadBlockingMode.WaitAll, token).AsTask();

            Assert.That(await channel.Reverse.WriteAsync(new ReadOnlySequence<byte>([1, 2, 3]), token), Is.EqualTo(IOResult.Ok), $"attempt {attempt}: the response was acknowledged");
            await channel.Reverse.CloseAsync();

            ReadResult read = await received;
            Assert.That(read.Result == IOResult.Ok ? read.Data.ToArray() : [], Is.EqualTo(new byte[] { 1, 2, 3 }), $"attempt {attempt}");
        }
    }

    [Test]
    public async Task A_half_close_above_keeps_the_response_flowing(CancellationToken token)
    {
        Channel channel = new();

        Assert.That(await channel.Reverse.WriteEofAsync(token), Is.EqualTo(IOResult.Ok));
        Assert.That((await channel.ReadAsync(1, ReadBlockingMode.WaitAny, token)).Result, Is.EqualTo(IOResult.Ended), "the request end reaches the peer");
        Task<IOResult> response = channel.WriteAsync(new ReadOnlySequence<byte>([1, 2, 3]), token).AsTask();
        ReadResult received = await channel.Reverse.ReadAsync(3, ReadBlockingMode.WaitAll, token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await response, Is.EqualTo(IOResult.Ok));
            Assert.That(received.Data.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(channel.GetAwaiter().IsCompleted, Is.False, "a half-close leaves the channel open");
        }

        await channel.CloseAsync();
    }

    /// <summary>Multi-segment frames retain their backing arrays: a copy of each megabyte frame would land on the large object heap.</summary>
    [Test]
    public async Task Segments_are_passed_upward_without_a_copy(CancellationToken token)
    {
        byte[] frame = new byte[140_003];
        Channel channel = new();
        Task<IOResult> written = channel.WriteAsync(Segmented(frame, 0, [70_000, 3, 70_000]), token).AsTask();

        long received = 0;
        bool sameArray = true;
        while (received < frame.Length)
        {
            ReadResult read = await channel.Reverse.ReadAsync(0, ReadBlockingMode.WaitAny, token);
            foreach (ReadOnlyMemory<byte> segment in read.Data)
            {
                sameArray &= MemoryMarshal.TryGetArray(segment, out ArraySegment<byte> array) && ReferenceEquals(array.Array, frame);
                received += segment.Length;
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await written, Is.EqualTo(IOResult.Ok));
            Assert.That(received, Is.EqualTo(frame.Length));
            Assert.That(sameArray, Is.True, "every byte above is read from the array written below");
        }

        await channel.CloseAsync();
    }

    [Test]
    public async Task An_incomplete_exact_read_is_aborted_by_full_close(CancellationToken token)
    {
        Channel channel = new();
        Task<ReadResult> reading = channel.Reverse.ReadAsync(3, ReadBlockingMode.WaitAll, token).AsTask();
        Assert.That(await channel.WriteAsync(new ReadOnlySequence<byte>([1]), token), Is.EqualTo(IOResult.Ok));

        await channel.CloseAsync();

        ReadResult read = await reading.WaitAsync(token);
        Assert.That(read.Result, Is.EqualTo(IOResult.Aborted));
    }

    [Test]
    public async Task An_aborted_transport_read_is_an_io_error_not_end_of_stream(CancellationToken token)
    {
        Channel channel = new();
        Task<IOResult> writing = channel.WriteAsync(new ReadOnlySequence<byte>([1]), token).AsTask();
        await channel.CloseAsync();
        await writing.WaitAsync(token);
        using ChannelStreamAdapter stream = new(channel.Reverse);

        Assert.That(async () => await stream.ReadAsync(new byte[1], token), Throws.TypeOf<IOException>().With.Message.EqualTo("Channel read failed: Aborted."));
    }

    private static async Task AssertChannelAsync(int[][] chunks, CancellationToken token)
    {
        Channel channel = new();
        byte[] expected = new byte[chunks.Sum(static chunk => chunk.Sum())];
        Random.Shared.NextBytes(expected);

        Task writing = Task.Run(async () =>
        {
            int offset = 0;
            foreach (int[] segments in chunks)
            {
                Assert.That(await channel.WriteAsync(Segmented(expected, offset, segments), token), Is.EqualTo(IOResult.Ok));
                offset += segments.Sum();
            }
        }, token);

        ReadResult read = await channel.Reverse.ReadAsync(expected.Length, ReadBlockingMode.WaitAll, token);
        await writing;
        await channel.CloseAsync();
        await channel.Reverse.CloseAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read.Result, Is.EqualTo(IOResult.Ok));
            Assert.That(read.Data.ToArray(), Is.EqualTo(expected));
        }
    }

    private static ReadOnlySequence<byte> Segmented(byte[] data, int offset, int[] lengths)
    {
        Segment first = new(data.AsMemory(offset, lengths[0]), 0);
        Segment last = first;
        offset += lengths[0];
        foreach (int length in lengths.Skip(1))
        {
            last = last.Append(data.AsMemory(offset, length));
            offset += length;
        }

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            Segment next = new(memory, RunningIndex + Memory.Length);
            Next = next;
            return next;
        }
    }
}
