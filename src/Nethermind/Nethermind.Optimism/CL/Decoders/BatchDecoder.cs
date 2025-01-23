// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Optimism.CL;

// TODO: maybe we should avoid using Rlp library at all?
public class BatchDecoder : IRlpValueDecoder<BatchV0>, IRlpStreamDecoder<BatchV0>
{
    public static readonly BatchDecoder Instance = new();

    public int GetLength(BatchV0 item, RlpBehaviors rlpBehaviors)
    {
        throw new System.NotImplementedException();
    }

    // rlp_encode([parent_hash, epoch_number, epoch_hash, timestamp, transaction_list])
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
        // TODO: proper error handling
        rlpStream.ReadByte();
        rlpStream.ReadByte();
        rlpStream.ReadByte();
        rlpStream.ReadByte();

        byte versionByte = rlpStream.ReadByte(); // This should be done outside
        if (versionByte != 0 && versionByte != 1)
        {
            throw new FormatException("Invalid batch version.");
        }

        if (versionByte == 0)
        {
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
        else
        {
            return new BatchV0();
        }
    }

    // Batch format
    //
    // SpanBatchType := 1
    // spanBatch := SpanBatchType ++ prefix ++ payload
    // prefix := rel_timestamp ++ l1_origin_num ++ parent_check ++ l1_origin_check
    // payload := block_count ++ origin_bits ++ block_tx_counts ++ txs
    // txs := contract_creation_bits ++ y_parity_bits ++ tx_sigs ++ tx_tos ++ tx_datas ++ tx_nonces ++ tx_gases ++ protected_bits
    public BatchV1 DecodeSpanBatch(RlpStream rlpStream, RlpBehaviors rlpBehaviors)
    {
        rlpStream.ReadByte();
        rlpStream.ReadByte();
        rlpStream.ReadByte();
        rlpStream.ReadByte();

        byte version = rlpStream.ReadByte();
        if (version != 1)
        {
            throw new FormatException("Invalid batch version.");
        }
        ulong relTimestamp = rlpStream.DecodeUlong();
        ulong l1OriginNum = rlpStream.DecodeULong();
        byte[] parentCheck = rlpStream.DecodeByteArray();
        if (parentCheck.Length != 20)
        {
            throw new FormatException("Invalid parent check.");
        }
        byte[] l1OriginCheck = rlpStream.DecodeByteArray();
        if (l1OriginCheck.Length != 20)
        {
            throw new FormatException("Invalid l1 origin check.");
        }
        ulong blockCount = rlpStream.DecodeUlong();
        BigInteger originBits = rlpStream.DecodeUBigInt();
        ulong[] blockTransactionCounts = rlpStream.DecodeArray(x => x.DecodeUlong());
        // TODO: txs

        return new BatchV1()
        {
            RelTimestamp = relTimestamp,
            L1OriginNum = l1OriginNum,
            ParentCheck = parentCheck,
            L1OriginCheck = l1OriginCheck,
            BlockCount = blockCount,
            OriginBits = originBits,
            BlockTxCounts = blockTransactionCounts
        };
    }

    private const int ULongMaxLength = 10;

    // Decodes protobuf encoded ulong
    // Returns the number and number of bytes read
    // TODO: maybe we should use standard library for that?
    (ulong, int) DecodeULong(byte[] data)
    {
        // TODO: handle errors
        ulong x = 0;
        int s = 0;
        for (int i = 0; i <= ULongMaxLength; i++)
        {
            byte b = data[i];
            if (b < 0x80) {
                return (x | ((ulong)b << s), i + 1);
            }

            x |= (ulong)(b & 0x7f) << s;
            s += 7;
        }

        throw new Exception("Overflow");
    }

    private (BigInteger, int) DecodeBits(byte[] data, ulong bitLength)
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
        return (x, (int)bufLen);
    }

    private (byte[], int, TxType) DecodeTxData(byte[] data)
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
            rlpStream = new(data[1..]);
        }
        else
        {
            // Legacy tx
            type = 0;
            rlpStream = new(data[0..]);
        }

        if (!rlpStream.IsSequenceNext())
        {
            throw new FormatException("Invalid tx data.");
        }
        else
        {
            n += rlpStream.PeekNextRlpLength();
            return (rlpStream.PeekNextItem().ToArray(), n, (TxType)type);
        }
    }

    public BatchV1 DecodeSpanBinary(byte[] data)
    {
        // prefix
        int n = 0;
        (ulong relTimestamp, int n1) = DecodeULong(data[n..]);
        n += n1;
        (ulong l1OriginNum, int n2) = DecodeULong(data[n..]);
        n += n2;
        byte[] parentCheck = data[n..(n + 20)];
        n += 20;
        byte[] l1OriginCheck = data[n..(n + 20)];
        n += 20;

        // payload
        (ulong blockCount, int n3) = DecodeULong(data[n..]);
        n += n3;
        (BigInteger originBits, int n4) = DecodeBits(data[n..], blockCount);
        n += n4;

        ulong[] blockTransactionCounts = new ulong[blockCount];
        ulong totalTxCount = 0;
        for (int i = 0; i < (int)blockCount; ++i)
        {
            (blockTransactionCounts[i], int n5) = DecodeULong(data[n..]);
            totalTxCount += blockTransactionCounts[i];
            n += n5;
        }

        // txs

        (BigInteger contractCreationBits, int n6) = DecodeBits(data[n..], totalTxCount);
        n += n6;
        (BigInteger yParityBits, int n7) = DecodeBits(data[n..], totalTxCount);
        n += n7;

        // Signatures
        Signature[] signatures = new Signature[totalTxCount];
        for (int i = 0; i < (int)totalTxCount; ++i)
        {
            signatures[i] = new(
                data[n..(n + 32)],
                data[(n + 32)..(n + 64)],
                27 // TODO: recover v
            );
            n += 64;
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
            tos[i] = new(data[n..(n + Address.Size)]);
            n += Address.Size;
        }

        byte[][] datas = new byte[totalTxCount][];
        TxType[] types = new TxType[totalTxCount];
        ulong legacyTxCnt = 0;
        for (int i = 0; i < (int)totalTxCount; ++i)
        {
            (datas[i], int n8, types[i]) = DecodeTxData(data[n..]);
            if (types[i] == TxType.Legacy)
            {
                legacyTxCnt++;
            }
            n += n8;
        }

        ulong[] nonces = new ulong[totalTxCount];
        for (int i = 0; i < (int)totalTxCount; ++i)
        {
            (nonces[i], int n8) = DecodeULong(data[n..]);
            n += n8;
        }

        ulong[] gases = new ulong[totalTxCount];
        for (int i = 0; i < (int)totalTxCount; ++i)
        {
            (gases[i], int n9) = DecodeULong(data[n..]);
            n += n9;
        }

        (BigInteger protectedBits, int n10) = DecodeBits(data[n..], legacyTxCnt);
        n += n10;

        return new BatchV1()
        {
            RelTimestamp = relTimestamp,
            L1OriginNum = l1OriginNum,
            ParentCheck = parentCheck,
            L1OriginCheck = l1OriginCheck,
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

    public void Encode(RlpStream stream, BatchV0 item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        throw new NotImplementedException();
    }
}
