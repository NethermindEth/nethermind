// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.IO.Compression;
using Nethermind.Logging;
using Nethermind.Optimism.CL.Decoding;
using NUnit.Framework;

namespace Nethermind.Optimism.Test.CL;

public class FrameQueueTests
{
    [Test]
    public void Clear_DropsBufferedFrameData()
    {
        FrameQueue queue = new(LimboLogs.Instance);

        // A non-last frame with a leading byte that is not a valid channel-compression marker
        // (not zlib `*8`/`*F` and not brotli `0x01`).
        Frame staleFrame = new()
        {
            ChannelId = 1,
            FrameNumber = 0,
            FrameData = [0x77, 0x77, 0x77],
            IsLast = false,
        };
        Assert.That(queue.ConsumeFrame(staleFrame), Is.Null);

        queue.Clear();

        // Build a self-contained brotli-encoded channel: marker byte `0x01` followed by a
        // brotli stream of an empty RLP byte array (`0x80`).
        byte[] brotli = BrotliCompress([0x80]);
        byte[] channelPayload = new byte[brotli.Length + 1];
        channelPayload[0] = 0x01;
        brotli.CopyTo(channelPayload, 1);

        Frame freshFrame = new()
        {
            ChannelId = 2,
            FrameNumber = 0,
            FrameData = channelPayload,
            IsLast = true,
        };

        // If Clear() leaks `_frameData`, the leftover `0x77` prefix makes ChannelDecoder
        // throw "Unsupported compression algorithm 119" before the fresh payload is read.
        BatchV1[]? result = null;
        Action consume = () => result = queue.ConsumeFrame(freshFrame);
        Assert.That(consume, Throws.Nothing);
        Assert.That(result, Is.Not.Null);
    }

    private static byte[] BrotliCompress(ReadOnlySpan<byte> data)
    {
        using MemoryStream output = new();
        using (BrotliStream brotli = new(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            brotli.Write(data);
        }
        return output.ToArray();
    }
}
