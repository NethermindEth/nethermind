// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    public async Task Checkpoint_flushes_to_the_reader_before_the_writer_is_finished()
    {
        Pipe pipe = new(new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0));
        await using BeaconJsonStream stream = new(pipe.Writer, CancellationToken.None);
        stream.Writer.WriteStartArray();
        while (stream.Writer.BytesCommitted + stream.Writer.BytesPending < 4 * ThresholdBytes)
        {
            stream.Writer.WriteStringValue("filler element of the kind a long state list is made of");
            await stream.CheckpointAsync();
        }

        // Utf8JsonWriter hands the pipe every full segment it grows out of, so BytesPending alone
        // stays around one segment; only a checkpoint that counts committed bytes ever flushes.
        Assert.That(pipe.Reader.TryRead(out ReadResult read), Is.True, "nothing reached the reader: the checkpoint never flushed");
        Assert.That(read.Buffer.Length, Is.GreaterThanOrEqualTo(ThresholdBytes));
        pipe.Reader.AdvanceTo(read.Buffer.End);
    }
}
