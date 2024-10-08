// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Network.Rlpx;

namespace Nethermind.Optimism.CL;

public class FrameDecoder
{
    private static (Frame, int) DecodeFrame(byte[] data)
    {
        byte[] channelId = data[..16];
        UInt16 frameNumber = BitConverter.ToUInt16(data[16..18].Reverse().ToArray());
        UInt32 frameDataLength = BitConverter.ToUInt32(data[18..22].Reverse().ToArray());
        byte[] frameData = data[22..(22 + (int)frameDataLength)];
        byte isLast = data[22 + (int)frameDataLength];
        if (isLast != 0 && isLast != 1)
        {
            throw new Exception("Invalid isLast flag");
        }
        return (new Frame()
        {
            ChannelId = channelId,
            FrameNumber = frameNumber,
            FrameData = frameData,
            IsLast = isLast == 0
        }, 23 + (int)frameDataLength);
    }

    public static Frame[] DecodeFrames(byte[] data)
    {
        byte version = data[0];
        if (version != 0)
        {
            throw new Exception($"Frame Decoder version {version} is not supported.");
        }

        List<Frame> frames = new List<Frame>();
        int pos = 1;
        while (pos < data.Length)
        {
            (Frame frame, int decoded) = DecodeFrame(data[pos..]);
            pos += decoded;
            frames.Add(frame);
        }

        if (pos != data.Length)
        {
            throw new Exception("Excess frame data");
        }
        return frames.ToArray();
    }
}

public struct Frame
{
    public byte[] ChannelId;
    public UInt16 FrameNumber;
    public byte[] FrameData;
    public bool IsLast;
}
