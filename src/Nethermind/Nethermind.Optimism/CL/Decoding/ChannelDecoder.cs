// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Optimism.CL.Decoding;

public class ChannelDecoder
{
    private const int MaxRlpBytesPerChannel = 100_000_000;

    public static byte[] DecodeChannel(byte[] data)
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
        return memoryStream.ToArray();
    }

    private static void CopyDataWithLimit(Stream input, Stream output)
    {
        byte[] buffer = new byte[4096];
        int bytesRead = 0;
        int totalRead = 0;

        while (totalRead <= MaxRlpBytesPerChannel &&
               (bytesRead = input.Read(buffer, 0, Math.Min(buffer.Length, MaxRlpBytesPerChannel - totalRead))) > 0)
        {
            totalRead += bytesRead;
            output.Write(buffer, 0, bytesRead);
        }
    }
}

public struct SingularBatch
{
    public bool IsFirstBlockInEpoch;
    public ulong EpochNumber;
    public ulong Timestamp;
    public byte[][] Transactions;
}

public struct BatchV1Transactions
{
    public BigInteger ContractCreationBits;
    public BigInteger YParityBits;
    public (UInt256 R, UInt256 S)[] Signatures;
    public Address[] Tos;
    public byte[][] Datas;
    public ulong[] Nonces;
    public ulong[] Gases;
    public BigInteger ProtectedBits;

    public TxType[] Types;
}
