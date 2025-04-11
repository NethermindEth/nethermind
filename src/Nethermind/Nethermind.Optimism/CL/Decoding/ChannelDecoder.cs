// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;

namespace Nethermind.Optimism.CL.Decoding;

public class ChannelDecoder
{
    private const int MaxRlpBytesPerChannel = 100_000_000;

    public static ReadOnlyMemory<byte> DecodeChannel(byte[] data)
    {
        MemoryStream memoryStream = new();
        if ((data[0] & 0x0F) == 8 || (data[0] & 0x0F) == 15)
        {
            // zlib
            var deflateStream = new DeflateStream(new MemoryStream(data[2..]), CompressionMode.Decompress);
            CopyDataWithLimit(deflateStream, memoryStream);
        }
        else if (data[0] == 1)
        {
            // brotli
            BrotliStream stream = new BrotliStream(new MemoryStream(data[1..]), CompressionMode.Decompress);
            CopyDataWithLimit(stream, memoryStream);
        }
        else
        {
            throw new Exception($"Unsupported compression algorithm {data[0]}");
        }
        return memoryStream.GetBuffer();
    }

    private static void CopyDataWithLimit(Stream input, Stream output)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
        int bytesRead = 0;
        int totalRead = 0;

        try
        {
            while (totalRead <= MaxRlpBytesPerChannel &&
                   (bytesRead = input.Read(buffer, 0, Math.Min(buffer.Length, MaxRlpBytesPerChannel - totalRead))) > 0)
            {
                totalRead += bytesRead;
                output.Write(buffer, 0, bytesRead);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

public readonly struct SingularBatch
{
    public bool IsFirstBlockInEpoch { get; init; }
    public ulong EpochNumber { get; init; }
    public ulong Timestamp { get; init; }
    public byte[][] Transactions { get; init; }
}
