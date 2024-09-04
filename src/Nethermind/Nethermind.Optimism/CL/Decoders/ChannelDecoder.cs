// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.IO.Compression;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Optimism.CL;

public class ChannelDecoder
{
    public static byte[] DecodeChannel(Frame frame)
    {
        if ((frame.FrameData[0] & 0x0F) == 8 || (frame.FrameData[0] & 0x0F) == 15)
        {
            // zlib
            var deflateStream = new DeflateStream(new MemoryStream(frame.FrameData[2..]), CompressionMode.Decompress);
            var memoryStream = new MemoryStream();
            deflateStream.CopyTo(memoryStream);
            return memoryStream.ToArray();
        } else if (frame.FrameData[0] == 1)
        {
            // brotli
            throw new NotImplementedException("Brotli is not supported");
        }
        else
        {
            throw new Exception($"Unsupported compression algorithm {frame.FrameData[0]}");
        }
    }
}
