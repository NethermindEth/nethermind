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

namespace Nethermind.Optimism.CL;

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
public struct BatchV0
{
    public Hash256 ParentHash;
    public ulong EpochNumber;
    public Hash256 EpochHash;
    public ulong Timestamp;
    public byte[][] Transactions;
}

// Span batch
public struct BatchV1
{
    public ulong RelTimestamp;
    public ulong L1OriginNum;
    public byte[] ParentCheck; // 20 bytes
    public byte[] L1OriginCheck; // 20 bytes
    public ulong BlockCount;
    public BigInteger OriginBits;
    public ulong[] BlockTxCounts;
    public BatchV1Transactions Txs;
}

public struct BatchV1Transactions
{
    public BigInteger ContractCreationBits;
    public BigInteger YParityBits;
    public Signature[] Signatures;
    public Address[] Tos;
    public byte[][] Datas;
    public ulong[] Nonces;
    public ulong[] Gases;
    public BigInteger ProtectedBits;

    public TxType[] Types;
}
