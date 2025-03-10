// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Optimism.CL.Decoding;

public class ChannelDecoder
{
    public static byte[] DecodeChannel(byte[] data)
    {
        // TODO: avoid allocation
        var memoryStream = new MemoryStream();
        if ((data[0] & 0x0F) == 8 || (data[0] & 0x0F) == 15)
        {
            // zlib
            // TODO: test
            var deflateStream = new DeflateStream(new MemoryStream(data[2..]), CompressionMode.Decompress);
            deflateStream.CopyTo(memoryStream);
        } else if (data[0] == 1)
        {
            // brotli
            BrotliStream stream = new BrotliStream(new MemoryStream(data[1..]), CompressionMode.Decompress);
            stream.CopyTo(memoryStream);
        }
        else
        {
            throw new Exception($"Unsupported compression algorithm {data[0]}");
        }
        // TODO: make rlp stream out of MemoryStream without conversion
        return memoryStream.ToArray();
    }
}

// TODO: support singular batches
// In op spec BatchV0 is called Singular batch
public struct BatchV0
{
    public Hash256 ParentHash;
    public ulong EpochNumber;
    public Hash256 EpochHash;
    public ulong Timestamp;
    public byte[][] Transactions;
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
