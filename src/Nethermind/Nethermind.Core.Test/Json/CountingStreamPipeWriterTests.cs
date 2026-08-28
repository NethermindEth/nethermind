// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

[Parallelizable(ParallelScope.All)]
public class CountingStreamPipeWriterTests
{
    private static StreamPipeWriterOptions Options =>
        new(pool: MemoryPool<byte>.Shared, minimumBufferSize: 16 * 1024, leaveOpen: true);

    [Test]
    public void Advance_does_not_flush_and_invalidate_outstanding_buffer()
    {
        using MemoryStream stream = new();
        CountingStreamPipeWriter writer = new(stream, Options);

        const int first = 20_000;
        const int second = 5_000;

        Span<byte> buffer = writer.GetSpan(64 * 1024);
        buffer.Slice(0, first).Fill(0xAB);
        writer.Advance(first);

        buffer.Slice(first, second).Fill(0xCD);
        Assert.DoesNotThrow(() => writer.Advance(second));

        writer.Complete();

        Assert.That(stream.Length, Is.EqualTo(first + second));
        Assert.That(writer.WrittenCount, Is.EqualTo(first + second));
        byte[] written = stream.ToArray();
        Assert.That(written.AsSpan(0, first).ToArray(), Is.All.EqualTo((byte)0xAB));
        Assert.That(written.AsSpan(first, second).ToArray(), Is.All.EqualTo((byte)0xCD));
    }

    public async Task Large_payload_round_trips([Values] bool useAsync)
    {
        string[] payload = Enumerable.Range(0, 100_000).Select(i => $"item-{i:D6}").ToArray();

        EthereumJsonSerializer serializer = new();
        await using MemoryStream stream = new();

        long written = useAsync
            ? await serializer.SerializeAsync(stream, payload, CancellationToken.None)
            : serializer.Serialize(stream, payload);

        Assert.That(written, Is.EqualTo(stream.Length));
        stream.Position = 0;
        Assert.That(serializer.Deserialize<string[]>(stream), Is.EqualTo(payload));
    }
}
