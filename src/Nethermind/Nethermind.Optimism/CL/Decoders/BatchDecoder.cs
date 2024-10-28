// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Optimism.CL;

// TODO: maybe we should avoid using Rlp library at all?
public class BatchDecoder : IRlpValueDecoder<BatchV0>, IRlpStreamDecoder<BatchV0>
{
    public static BatchDecoder Instance = new();

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

    public BatchV1 DecodeSpanBinary(byte[] data)
    {
        byte version = data[4];
        if (version != 1)
        {
            throw new FormatException("Invalid batch version.");
        }
        ulong relTimestamp = BitConverter.ToUInt64(data[4..12]);
        ulong l1OriginNum = BitConverter.ToUInt64(data[12..20]);
        byte[] parentCheck = data[20..40];
        byte[] l1OriginCheck = data[40..60];
        ulong blockCount = BitConverter.ToUInt64(data[60..68]);
        BigInteger originBits = 0;
        // ulong[] blockTransactionCounts = rlpStream.DecodeArray(x => x.DecodeUlong());
        // TODO: txs

        return new BatchV1()
        {
            RelTimestamp = relTimestamp,
            L1OriginNum = l1OriginNum,
            ParentCheck = parentCheck,
            L1OriginCheck = l1OriginCheck,
            BlockCount = blockCount,
            OriginBits = originBits,
            BlockTxCounts = Array.Empty<ulong>()
        };
    }

    public void Encode(RlpStream stream, BatchV0 item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        throw new NotImplementedException();
    }
}
