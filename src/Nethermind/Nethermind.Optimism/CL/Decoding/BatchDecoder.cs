// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Optimism.CL.Decoding;

public static class BatchDecoder
{
    private const ulong MaxSpanBatchElementCount = 10_000_000;
    public static IEnumerable<BatchV1> DecodeSpanBatches(ReadOnlyMemory<byte> source)
    {
        BinaryMemoryReader reader = new(source);
        while (reader.HasRemainder)
        {
            byte type = reader.TakeByte();
            if (type != 1)
            {
                throw new NotSupportedException($"Only span batches are supported. Got type {type}");
            }
            yield return reader.Read(DecodeSpanBatch);
        }
    }

    /// <remarks>
    /// https://specs.optimism.io/protocol/delta/span-batches.html#span-batch-format
    /// </remarks>
    private static BatchV1 DecodeSpanBatch(BinaryMemoryReader reader)
    {
        ulong relTimestamp = reader.Read(Protobuf.DecodeULong);
        ulong l1OriginNum = reader.Read(Protobuf.DecodeULong);
        ReadOnlyMemory<byte> parentCheck = reader.Take(20);
        ReadOnlyMemory<byte> l1OriginCheck = reader.Take(20);

        // payload
        ulong blockCount = reader.Read(Protobuf.DecodeULong);
        if (blockCount < 1)
        {
            throw new FormatException("Invalid span batch: block_count must be >= 1");
        }
        if (blockCount > MaxSpanBatchElementCount)
        {
            throw new FormatException($"Invalid span batch: block_count exceeds MAX_SPAN_BATCH_ELEMENT_COUNT ({MaxSpanBatchElementCount})");
        }
        BigInteger originBits = reader.Read(Protobuf.DecodeBitList, blockCount);

        ulong[] blockTransactionCounts = new ulong[blockCount];
        ulong totalTxCount = 0;
        for (int i = 0; i < (int)blockCount; ++i)
        {
            blockTransactionCounts[i] = reader.Read(Protobuf.DecodeULong);
            if (blockTransactionCounts[i] > MaxSpanBatchElementCount)
            {
                throw new FormatException($"Invalid span batch: tx count for block {i} exceeds MAX_SPAN_BATCH_ELEMENT_COUNT ({MaxSpanBatchElementCount})");
            }
            totalTxCount += blockTransactionCounts[i];
            if (totalTxCount > MaxSpanBatchElementCount)
            {
                throw new FormatException($"Invalid span batch: totalTxCount exceeds MAX_SPAN_BATCH_ELEMENT_COUNT ({MaxSpanBatchElementCount})");
            }
        }
        // TODO:
        // `totalTxCount` in BatchV1 cannot be greater than MaxSpanBatchElementCount (`10_000_000`).
        // MaxSpanBatchElementCount is the maximum number of blocks, transactions in total, or transaction per block allowed in a BatchV1.

        // Txs

        BigInteger contractCreationBits = reader.Read(Protobuf.DecodeBitList, totalTxCount);
        BigInteger yParityBits = reader.Read(Protobuf.DecodeBitList, totalTxCount);

        // Signatures
        (UInt256 R, UInt256 S)[] signatures = new (UInt256 R, UInt256 S)[totalTxCount];
        for (int i = 0; i < (int)totalTxCount; ++i)
        {
            signatures[i] = (
                R: new UInt256(reader.Take(32).Span, true),
                S: new UInt256(reader.Take(32).Span, true));
        }

        int contractCreationCnt = (int)BigInteger.PopCount(contractCreationBits);

        Address[] tos = new Address[(int)totalTxCount - contractCreationCnt];
        for (int i = 0; i < (int)totalTxCount - contractCreationCnt; ++i)
        {
            tos[i] = new Address(reader.Take(Address.Size).Span);
        }

        ReadOnlyMemory<byte>[] data = new ReadOnlyMemory<byte>[(int)totalTxCount];
        TxType[] types = new TxType[(int)totalTxCount];
        ulong legacyTxCnt = 0;
        for (int i = 0; i < (int)totalTxCount; ++i)
        {
            (data[i], types[i]) = reader.Read(TxParser.Data);
            if (types[i] == TxType.Legacy)
            {
                legacyTxCnt++;
            }
        }

        ulong[] nonces = new ulong[(int)totalTxCount];
        for (int i = 0; i < (int)totalTxCount; ++i)
        {
            nonces[i] = reader.Read(Protobuf.DecodeULong);
        }

        ulong[] gases = new ulong[(int)totalTxCount];
        for (int i = 0; i < (int)totalTxCount; ++i)
        {
            gases[i] = reader.Read(Protobuf.DecodeULong);
        }

        BigInteger protectedBits = reader.Read(Protobuf.DecodeBitList, legacyTxCnt);

        return new BatchV1
        {
            RelTimestamp = relTimestamp,
            L1OriginNum = l1OriginNum,
            ParentCheck = parentCheck,
            L1OriginCheck = l1OriginCheck,
            BlockCount = blockCount,
            OriginBits = originBits,
            BlockTxCounts = blockTransactionCounts,
            Txs = new BatchV1.Transactions
            {
                ContractCreationBits = contractCreationBits,
                YParityBits = yParityBits,
                Signatures = signatures,
                Tos = tos,
                Data = data,
                Types = types,
                TotalLegacyTxCount = legacyTxCnt,
                Nonces = nonces,
                Gases = gases,
                ProtectedBits = protectedBits,
            }
        };
    }
}
