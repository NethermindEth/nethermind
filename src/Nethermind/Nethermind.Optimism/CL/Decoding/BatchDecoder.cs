// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Optimism.CL.Decoding;

// TODO: maybe we should avoid using Rlp library at all?
// TODO: Split into singular and span decoders
public class BatchDecoder
{
    public static readonly BatchDecoder Instance = new();

    private const int ULongMaxLength = 10;

    // Decodes protobuf encoded ulong
    // Returns the number and number of bytes read
    // TODO: maybe we should use standard library for that?
    private ulong DecodeULong(ref ReadOnlySpan<byte> data)
    {
        // TODO: handle errors
        ulong x = 0;
        int s = 0;
        for (int i = 0; i <= ULongMaxLength; i++)
        {
            byte b = data[i];
            if (b < 0x80)
            {
                data.TakeAndMove(i + 1);
                return x | ((ulong)b << s);
            }

            x |= (ulong)(b & 0x7f) << s;
            s += 7;
        }

        throw new Exception("Overflow");
    }

    private BigInteger DecodeBits(ref ReadOnlySpan<byte> data, ulong bitLength)
    {
        ulong bufLen = bitLength / 8;
        if (bitLength % 8 != 0)
        {
            bufLen++;
        }

        BigInteger x = new(data[..(int)bufLen], true, true);
        if (x.GetBitLength() > (long)bitLength)
        {
            throw new FormatException("Invalid bit length.");
        }
        data.TakeAndMove((int)bufLen);
        return x;
    }

    private (byte[], TxType) DecodeTxData(ref ReadOnlySpan<byte> data)
    {
        byte firstByte = data[0];
        int n = 0;
        byte type;

        Rlp.ValueDecoderContext decoder;
        if (firstByte <= 0x7F)
        {
            // Tx with type
            n++;
            type = firstByte;
            decoder = new(data[1..]);
        }
        else
        {
            // Legacy tx
            type = 0;
            decoder = new(data);
        }

        if (!decoder.IsSequenceNext())
        {
            throw new FormatException("Invalid tx data.");
        }
        else
        {
            n += decoder.PeekNextRlpLength();
            return (data.TakeAndMove(n).ToArray(), (TxType)type);
        }
    }

    public BatchV1[] DecodeSpanBatches(ref ReadOnlySpan<byte> data)
    {
        List<BatchV1> batches = new();
        while (data.Length != 0)
        {
            byte type = data.TakeAndMove(1)[0];
            if (type != 1)
            {
                throw new NotSupportedException($"Only span batches are supported. Got type {type}");
            }
            batches.Add(DecodeSpanBatch(ref data));
        }
        return batches.ToArray();
    }

    public BatchV1 DecodeSpanBatch(ref ReadOnlySpan<byte> data)
    {
        // prefix
        ulong relTimestamp = DecodeULong(ref data);
        ulong l1OriginNum = DecodeULong(ref data);
        ReadOnlySpan<byte> parentCheck = data.TakeAndMove(20);
        ReadOnlySpan<byte> l1OriginCheck = data.TakeAndMove(20);
        // payload
        ulong blockCount = DecodeULong(ref data);
        BigInteger originBits = DecodeBits(ref data, blockCount);
        ulong[] blockTransactionCounts = new ulong[blockCount];
        ulong totalTxCount = 0;
        for (int i = 0; i < (int)blockCount; ++i)
        {
            blockTransactionCounts[i] = DecodeULong(ref data);
            totalTxCount += blockTransactionCounts[i];
        }

        // txs

        BigInteger contractCreationBits = DecodeBits(ref data, totalTxCount);
        BigInteger yParityBits = DecodeBits(ref data, totalTxCount);

        // Signatures
        (UInt256 R, UInt256 S)[] signatures = new (UInt256 R, UInt256 S)[totalTxCount];
        for (int i = 0; i < (int)totalTxCount; ++i)
        {
            signatures[i] = new(
                new(data.TakeAndMove(32), true),
                new(data.TakeAndMove(32), true)
            );
        }

        int contractCreationCnt = 0;
        byte[] contractCreationBytes = contractCreationBits.ToByteArray();
        for (int i = 0; i / 8 < contractCreationBytes.Length && i < (int)totalTxCount; ++i)
        {
            if (((contractCreationBytes[i / 8] >> (i % 8)) & 1) == 1)
            {
                contractCreationCnt++;
            }
        }

        Address[] tos = new Address[(int)totalTxCount - contractCreationCnt];
        for (int i = 0; i < (int)totalTxCount - contractCreationCnt; ++i)
        {
            tos[i] = new(data.TakeAndMove(Address.Size));
        }

        byte[][] datas = new byte[totalTxCount][];
        TxType[] types = new TxType[totalTxCount];
        ulong legacyTxCnt = 0;
        for (int i = 0; i < (int)totalTxCount; ++i)
        {
            // TODO: a lot of allocations here
            (datas[i], types[i]) = DecodeTxData(ref data);
            if (types[i] == TxType.Legacy)
            {
                legacyTxCnt++;
            }
        }

        ulong[] nonces = new ulong[totalTxCount];
        for (int i = 0; i < (int)totalTxCount; ++i)
        {
            nonces[i] = DecodeULong(ref data);
        }

        ulong[] gases = new ulong[totalTxCount];
        for (int i = 0; i < (int)totalTxCount; ++i)
        {
            gases[i] = DecodeULong(ref data);
        }

        BigInteger protectedBits = DecodeBits(ref data, legacyTxCnt);

        return new BatchV1()
        {
            RelTimestamp = relTimestamp,
            L1OriginNum = l1OriginNum,
            ParentCheck = parentCheck.ToArray(),
            L1OriginCheck = l1OriginCheck.ToArray(),
            BlockCount = blockCount,
            OriginBits = originBits,
            BlockTxCounts = blockTransactionCounts,
            Txs = new()
            {
                ContractCreationBits = contractCreationBits,
                YParityBits = yParityBits,
                Signatures = signatures,
                Tos = tos,
                Datas = datas,
                Nonces = nonces,
                Gases = gases,
                ProtectedBits = protectedBits,

                Types = types
            }
        };
    }
}
