// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Libp2p.Core;
using Nethermind.Network.Libp2p;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P;

/// <summary>An exact-length read above the stream gets every byte, however the yamux frames below it were split into segments.</summary>
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
