// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

[Parallelizable(ParallelScope.All)]
public class CountingStreamPipeWriterTests
{
    // Mirrors the options EthereumJsonSerializer uses for its pipe writer.
    private static StreamPipeWriterOptions Options =>
        new(pool: MemoryPool<byte>.Shared, minimumBufferSize: 16 * 1024, leaveOpen: true);

    [Test]
    public void Advance_does_not_flush_and_invalidate_outstanding_buffer()
    {
        // Regression for #9198. Previously Advance flushed the buffered data (and reset _tailMemory)
        // as soon as it crossed _minimumBufferSize. A consumer that obtained one buffer and advanced
        // into it more than once — which Utf8JsonWriter effectively does across a pipe flush — then
        // hit ArgumentOutOfRangeException on the next Advance because _tailMemory had been cleared.
        // The BCL StreamPipeWriter never flushes inside Advance; neither should this one.
        using MemoryStream stream = new();
        CountingStreamPipeWriter writer = new(stream, Options);

        const int first = 20_000;  // crosses the 16 KiB threshold on the first Advance
        const int second = 5_000;

        // Obtain a single buffer and advance into it twice without requesting it again in between.
        Span<byte> buffer = writer.GetSpan(64 * 1024);
        buffer.Slice(0, first).Fill(0xAB);
        writer.Advance(first);

        // The old in-Advance flush recycled this segment and cleared _tailMemory here, so the next
        // Advance threw ArgumentOutOfRangeException. It must not.
        buffer.Slice(first, second).Fill(0xCD);
        Assert.DoesNotThrow(() => writer.Advance(second));

        writer.Complete();

        Assert.That(stream.Length, Is.EqualTo(first + second));
        Assert.That(writer.WrittenCount, Is.EqualTo(first + second));
        byte[] written = stream.ToArray();
        Assert.That(written.AsSpan(0, first).ToArray(), Is.All.EqualTo((byte)0xAB));
        Assert.That(written.AsSpan(first, second).ToArray(), Is.All.EqualTo((byte)0xCD));
    }

    [Test]
    public async Task SerializeAsync_large_payload_round_trips()
    {
        // Exercises System.Text.Json's async PipeWriter path (Utf8JsonWriter.Dispose -> Flush ->
        // CountingStreamPipeWriter.Advance), the exact frame that threw in #9198, with a payload
        // large enough to span many buffer segments and pipe flushes.
        string[] payload = Enumerable.Range(0, 100_000).Select(i => $"item-{i:D6}").ToArray();

        EthereumJsonSerializer serializer = new();
        await using MemoryStream stream = new();

        long written = await serializer.SerializeAsync(stream, payload, CancellationToken.None);

        Assert.That(written, Is.EqualTo(stream.Length));
        stream.Position = 0;
        Assert.That(serializer.Deserialize<string[]>(stream), Is.EqualTo(payload));
    }

    [Test]
    public void Serialize_large_payload_round_trips()
    {
        // The synchronous path never calls FlushAsync mid-write, so the relocated threshold flush
        // (now in the buffer hand-off path) is what keeps memory bounded. Verify it stays correct.
        string[] payload = Enumerable.Range(0, 100_000).Select(i => $"item-{i:D6}").ToArray();

        EthereumJsonSerializer serializer = new();
        using MemoryStream stream = new();

        long written = serializer.Serialize(stream, payload);

        Assert.That(written, Is.EqualTo(stream.Length));
        stream.Position = 0;
        Assert.That(serializer.Deserialize<string[]>(stream), Is.EqualTo(payload));
    }
}
