// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Libp2p.Core;
using Nethermind.Network.Libp2p;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P;

/// <summary>An exact-length read above the relay gets every byte, however the yamux frames below it were split into segments.</summary>
/// <remarks>Nethermind.Libp2p 1.0.0 <c>Channel.ReadAsync</c> keeps only the first segment of each later chunk; pubsub reads a whole RPC that way.</remarks>
public class ContiguousChunkProtocolTests
{
    // Each chunk is one yamux frame as it is handed up, given by its segment lengths: Noise frames of up to 65,535 bytes, and small frames.
    private static readonly int[][][] Chunkings =
    [
        [[65535, 65535, 65535, 65535], [20], [65535, 65535, 30000]],
        [[16], [7, 9, 11], [65535, 3], [1], [40000, 40000, 40000]],
    ];

    [Test]
    [CancelAfter(10_000)]
    public Task An_exact_length_read_receives_every_segment_of_every_chunk([ValueSource(nameof(Chunkings))] int[][] chunks, CancellationToken token) =>
        AssertRelayedAsync(chunks, token);

    /// <summary>A channel the protocol above closes is closed below too, although the peer never ended its side.</summary>
    [Test]
    [CancelAfter(10_000)]
    public async Task A_full_close_above_closes_the_channel_below(CancellationToken token)
    {
        Channel lower = new();
        Channel upper = new();
        Task relay = ContiguousChunkProtocol.RelayAsync(lower.Reverse, upper);

        await upper.Reverse.CloseAsync();
        await relay.WaitAsync(token);

        Assert.That((await lower.ReadAsync(1, ReadBlockingMode.WaitAny, token)).Result, Is.EqualTo(IOResult.Ended));
    }

    /// <summary>A response the protocol above writes just before a full close reaches the peer before the channel below closes.</summary>
    /// <remarks>The relay can hold the response between its read above and its write below when the close arrives, so the check repeats.</remarks>
    [Test]
    [CancelAfter(30_000)]
    public async Task A_response_written_just_before_a_full_close_reaches_the_peer(CancellationToken token)
    {
        for (int attempt = 0; attempt < 5_000; attempt++)
        {
            Channel lower = new();
            Channel upper = new();
            Task relay = ContiguousChunkProtocol.RelayAsync(lower.Reverse, upper);
            Task<ReadResult> received = lower.ReadAsync(3, ReadBlockingMode.WaitAll, token).AsTask();

            await upper.Reverse.WriteAsync(new ReadOnlySequence<byte>([1, 2, 3]), token);
            await upper.Reverse.CloseAsync();
            await relay.WaitAsync(token);

            ReadResult read = await received;
            Assert.That(read.Result == IOResult.Ok ? read.Data.ToArray() : [], Is.EqualTo(new byte[] { 1, 2, 3 }), $"attempt {attempt}");
        }
    }

    /// <summary>A request the protocol above half-closes still receives its response from below.</summary>
    [Test]
    [CancelAfter(10_000)]
    public async Task A_half_close_above_keeps_the_response_flowing(CancellationToken token)
    {
        Channel lower = new();
        Channel upper = new();
        Task relay = ContiguousChunkProtocol.RelayAsync(lower.Reverse, upper);

        Assert.That(await upper.Reverse.WriteEofAsync(token), Is.EqualTo(IOResult.Ok));
        Assert.That((await lower.ReadAsync(1, ReadBlockingMode.WaitAny, token)).Result, Is.EqualTo(IOResult.Ended), "the request end reaches the peer");
        Task<IOResult> response = lower.WriteAsync(new ReadOnlySequence<byte>([1, 2, 3]), token).AsTask();
        ReadResult received = await upper.Reverse.ReadAsync(3, ReadBlockingMode.WaitAll, token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await response, Is.EqualTo(IOResult.Ok));
            Assert.That(received.Data.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(relay.IsCompleted, Is.False, "a half-close leaves the channel open");
        }

        await lower.CloseAsync();
        await relay.WaitAsync(token);
    }

    /// <summary>Segments go upward as they are: a multi-segment frame can be megabytes, and a copy of each would land on the large object heap.</summary>
    [Test]
    [CancelAfter(10_000)]
    public async Task Segments_are_passed_upward_without_a_copy(CancellationToken token)
    {
        byte[] frame = new byte[140_003];
        Channel lower = new();
        Channel upper = new();
        Task relay = ContiguousChunkProtocol.RelayAsync(lower.Reverse, upper);
        Task<IOResult> written = lower.WriteAsync(Segmented(frame, 0, [70_000, 3, 70_000]), token).AsTask();

        long received = 0;
        bool sameArray = true;
        while (received < frame.Length)
        {
            ReadResult read = await upper.Reverse.ReadAsync(0, ReadBlockingMode.WaitAny, token);
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

        await lower.CloseAsync();
        await relay.WaitAsync(token);
    }

    private static async Task AssertRelayedAsync(int[][] chunks, CancellationToken token)
    {
        Channel lower = new();
        Channel upper = new();
        Task relay = ContiguousChunkProtocol.RelayAsync(lower.Reverse, upper);
        byte[] expected = new byte[chunks.Sum(static chunk => chunk.Sum())];
        Random.Shared.NextBytes(expected);

        Task writing = Task.Run(async () =>
        {
            int offset = 0;
            foreach (int[] segments in chunks)
            {
                Assert.That(await lower.WriteAsync(Segmented(expected, offset, segments), token), Is.EqualTo(IOResult.Ok));
                offset += segments.Sum();
            }
        }, token);

        ReadResult read = await upper.Reverse.ReadAsync(expected.Length, ReadBlockingMode.WaitAll, token);
        await writing;
        await lower.CloseAsync();
        await upper.Reverse.CloseAsync();
        await relay.WaitAsync(token);

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
