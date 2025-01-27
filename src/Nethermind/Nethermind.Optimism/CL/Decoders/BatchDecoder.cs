// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Optimism.CL;

// TODO: maybe we should avoid using Rlp library at all?
// TODO: Split into singular and span decoders
public class BatchDecoder
{
    public static readonly BatchDecoder Instance = new();

    public BatchV0 Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        // TODO: not tested, do we need this?
        // TODO: proper error handling
        byte versionByte = decoderContext.ReadByte(); // This should be done outside
        if (versionByte != 0)
        {
            throw new FormatException("Invalid batch version.");
        }
        int length = decoderContext.ReadSequenceLength();
        int batchCheck = length + decoderContext.Position;
        Hash256? parentHash = decoderContext.DecodeKeccak();
        ArgumentNullException.ThrowIfNull(parentHash);
        ulong epochNumber = decoderContext.DecodeULong();
        Hash256? epochHash = decoderContext.DecodeKeccak();
        ArgumentNullException.ThrowIfNull(epochHash);
        ulong timestamp = decoderContext.DecodeULong();
        int transactionListLenght = decoderContext.ReadSequenceLength();
        List<byte[]> transactionList = new();
        while (decoderContext.Position < transactionListLenght)
        {
            transactionList.Add(decoderContext.DecodeByteArray());
        }

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
        {
            decoderContext.Check(batchCheck);
        }

        return new()
        {
            ParentHash = parentHash, EpochNumber = epochNumber, EpochHash = epochHash, Timestamp = timestamp,
            Transactions = transactionList.ToArray()
        };
    }

    public BatchV0 Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        // TODO: implement singular batch

        int length = rlpStream.ReadSequenceLength();
        int batchCheck = length + rlpStream.Position;
        Hash256? parentHash = rlpStream.DecodeKeccak();
        ArgumentNullException.ThrowIfNull(parentHash);
        ulong epochNumber = rlpStream.DecodeULong();
        Hash256? epochHash = rlpStream.DecodeKeccak();
        ArgumentNullException.ThrowIfNull(epochHash);
        ulong timestamp = rlpStream.DecodeULong();
        int transactionListLenght = rlpStream.ReadSequenceLength();
        List<byte[]> transactionList = new();
        while (rlpStream.Position < transactionListLenght)
        {
            transactionList.Add(rlpStream.DecodeByteArray());
        }

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
        {
            rlpStream.Check(batchCheck);
        }

        return new()
        {
            ParentHash = parentHash, EpochNumber = epochNumber, EpochHash = epochHash, Timestamp = timestamp,
            Transactions = transactionList.ToArray()
        };
    }

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
        RlpStream rlpStream;
        if (firstByte <= 0x7F)
        {
            // Tx with type
            n++;
            type = firstByte;
            rlpStream = new(data[1..].ToArray());
        }
        else
        {
            // Legacy tx
            type = 0;
            rlpStream = new(data.ToArray());
        }

        if (!rlpStream.IsSequenceNext())
        {
            throw new FormatException("Invalid tx data.");
        }
        else
        {
            n += rlpStream.PeekNextRlpLength();
            byte[] result = rlpStream.PeekNextItem().ToArray();
            data.TakeAndMove(n);
            return (result, (TxType)type);
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
        Signature[] signatures = new Signature[totalTxCount];
        for (int i = 0; i < (int)totalTxCount; ++i)
        {
            signatures[i] = new(
                data.TakeAndMove(32),
                data.TakeAndMove(32),
                27 // TODO: recover v
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
