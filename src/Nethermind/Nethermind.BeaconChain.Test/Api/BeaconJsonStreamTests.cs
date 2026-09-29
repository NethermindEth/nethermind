// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Api.Common;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Api;

/// <summary>
/// The state writer's promise is that a multi-hundred-MB body reaches the client as it is produced.
/// That only holds if the checkpoint actually flushes; a threshold measured against a counter that
/// can never reach it turns the stream into a whole-body buffer.
/// </summary>
public class BeaconJsonStreamTests
{
    private const int ThresholdBytes = 64 * 1024;

    [Test]
    public async Task Checkpoint_flushes_once_per_threshold_of_bytes_and_the_reader_sees_them_before_the_writer_is_finished()
    {
        Pipe pipe = new(new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0));
        FlushCountingWriter output = new(pipe.Writer);
        await using BeaconJsonStream stream = new(output, CancellationToken.None);
        stream.Writer.WriteStartArray();
        while (stream.Writer.BytesCommitted + stream.Writer.BytesPending < 4 * ThresholdBytes)
        {
            stream.Writer.WriteStringValue("filler element of the kind a long state list is made of");
            await stream.CheckpointAsync();
        }

        long written = stream.Writer.BytesCommitted + stream.Writer.BytesPending;

        // Utf8JsonWriter hands the pipe every full segment it grows out of, so BytesPending alone
        // stays around one segment; only a checkpoint that counts committed bytes ever flushes.
        // A flush covers at least one threshold of bytes, so a checkpoint that flushed per element would exceed the upper bound.
        Assert.That(output.Flushes, Is.InRange(written / ThresholdBytes - 1, written / ThresholdBytes), "one flush per threshold of written bytes");
        Assert.That(pipe.Reader.TryRead(out ReadResult read), Is.True, "nothing reached the reader: the checkpoint never flushed");
        Assert.That(read.Buffer.Length, Is.GreaterThanOrEqualTo(ThresholdBytes));
        pipe.Reader.AdvanceTo(read.Buffer.End);
    }

    private sealed class FlushCountingWriter(PipeWriter inner) : PipeWriter
    {
        public int Flushes { get; private set; }

        public override void Advance(int bytes) => inner.Advance(bytes);
        public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
        public override void CancelPendingFlush() => inner.CancelPendingFlush();
        public override void Complete(Exception? exception = null) => inner.Complete(exception);

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            Flushes++;
            return inner.FlushAsync(cancellationToken);
        }
    }
}
