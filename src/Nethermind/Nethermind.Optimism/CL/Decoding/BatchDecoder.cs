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
            ParentHash = parentHash,
            EpochNumber = epochNumber,
            EpochHash = epochHash,
            Timestamp = timestamp,
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
            ParentHash = parentHash,
            EpochNumber = epochNumber,
            EpochHash = epochHash,
            Timestamp = timestamp,
            Transactions = transactionList.ToArray()
        };
    }

    // TODO: Add decoding tests
    public static IEnumerable<BatchV1> DecodeSpanBatches(ReadOnlyMemory<byte> source)
    {
        var parser = new BinaryMemoryReader(source);
        while (source.Length != 0)
        {
            byte type = source.TakeAndMove(1).Span[0];
            if (type != 1)
            {
                throw new NotSupportedException($"Only span batches are supported. Got type {type}");
            }
            yield return parser.Read(DecodeSpanBatch);
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
        // TODO: `blockCount`: This is at least 1, empty span batches are invalid.
        ulong blockCount = reader.Read(Protobuf.DecodeULong);
        BigInteger originBits = reader.Read(Protobuf.DecodeBitList, blockCount);

        ulong[] blockTransactionCounts = new ulong[blockCount];
        ulong totalTxCount = 0;
        for (int i = 0; i < (int)blockCount; ++i)
        {
            blockTransactionCounts[i] = reader.Read(Protobuf.DecodeULong);
            totalTxCount += blockTransactionCounts[i];
        }

        // txs

        BigInteger contractCreationBits = reader.Read(Protobuf.DecodeBitList, totalTxCount);
        BigInteger yParityBits = reader.Read(Protobuf.DecodeBitList, totalTxCount);

        // Signatures
        (UInt256 R, UInt256 S)[] signatures = new (UInt256 R, UInt256 S)[totalTxCount];
        for (int i = 0; i < (int)totalTxCount; ++i)
        {
            signatures[i] = (new(reader.Take(32).Span, true), new(reader.Take(32).Span, true));
        }

        int contractCreationCnt = (int)BigInteger.PopCount(contractCreationBits);

        Address[] tos = new Address[(int)totalTxCount - contractCreationCnt];
        for (int i = 0; i < (int)totalTxCount - contractCreationCnt; ++i)
        {
            tos[i] = new(reader.Take(Address.Size).Span);
        }


        List<ReadOnlyMemory<byte>> datas = new((int)totalTxCount);
        List<TxType> types = new((int)totalTxCount);
        ulong legacyTxCnt = 0;
        for (int i = 0; i < (int)totalTxCount; ++i)
        {
            (datas[i], types[i]) = reader.Read(TxParser.Data);
            if (types[i] == TxType.Legacy)
            {
                legacyTxCnt++;
            }
        }

        List<ulong> nonces = new((int)totalTxCount);
        for (int i = 0; i < (int)totalTxCount; ++i)
        {
            nonces[i] = reader.Read(Protobuf.DecodeULong);
        }

        List<ulong> gases = new((int)totalTxCount);
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
