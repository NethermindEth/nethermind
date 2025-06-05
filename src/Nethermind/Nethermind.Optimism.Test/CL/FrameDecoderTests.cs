// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Optimism.CL.Decoding;
using NUnit.Framework;

namespace Nethermind.Optimism.Test.CL;

public class FrameDecoderTests
{
    [Test]
    public void DecodeSingleFrame()
    {
        var frame = new Frame
        {
            ChannelId = 1,
            FrameNumber = 1,
            FrameData = [0x01, 0xFF, 0x02, 0xEE],
            IsLast = false,
        };

        var buffer = new byte[frame.Size];

        int written = frame.WriteTo(buffer);
        written.Should().Be(frame.Size);

        int read = Frame.FromBytes(buffer, out var decoded);
        read.Should().Be(written);
        decoded.Should().Be(frame);
    }

    [Test]
    public void DecodeMultipleFrames()
    {
        var frame1 = new Frame
        {
            ChannelId = 1,
            FrameNumber = 1,
            FrameData = [0x01, 0xFF, 0x02, 0xEE],
            IsLast = false,
        };


        var frame2 = new Frame
        {
            ChannelId = 1,
            FrameNumber = 2,
            FrameData = [0xFF, 0x01, 0x02, 0xEE],
            IsLast = true,
        };

        byte[] buffer = new byte[1 + frame1.Size + 1 + frame2.Size];

        {
            Span<byte> span = buffer;
            span.TakeAndMove(1)[0] = 0;
            int written = frame1.WriteTo(span);
            span.TakeAndMove(written);

            span.TakeAndMove(1)[0] = 0;
            written = frame2.WriteTo(span);
            span.TakeAndMove(written);
        }

        var decoded = FrameDecoder.DecodeFrames(buffer).ToList();

        decoded.Count.Should().Be(2);
        decoded[0].Should().Be(frame1);
        decoded[1].Should().Be(frame2);
    }
}
