// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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

    public static IEnumerable<BatchV1> DecodeSpanBatches(ReadOnlyMemory<byte> source)
    {
        var parser = new BinaryMemoryReader(source);
        while (parser.HasRemainder)
        {
            var type = parser.TakeByte();
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
        var relTimestamp = reader.Read(Protobuf.DecodeULong);
        var l1OriginNum = reader.Read(Protobuf.DecodeULong);
        var parentCheck = reader.Take(20);
        var l1OriginCheck = reader.Take(20);

        // payload
        // TODO: `blockCount`: This is at least 1, empty span batches are invalid.
        var blockCount = reader.Read(Protobuf.DecodeULong);
        var originBits = reader.Read(Protobuf.DecodeBitList, blockCount);

        var blockTransactionCounts = new ulong[blockCount];
        ulong totalTxCount = 0;
        for (var i = 0; i < (int)blockCount; ++i)
        {
            blockTransactionCounts[i] = reader.Read(Protobuf.DecodeULong);
            totalTxCount += blockTransactionCounts[i];
        }
        // TODO:
        // `totalTxCount` in BatchV1 cannot be greater than MaxSpanBatchElementCount (`10_000_000`).
        // MaxSpanBatchElementCount is the maximum number of blocks, transactions in total, or transaction per block allowed in a BatchV1.

        // Txs

        var contractCreationBits = reader.Read(Protobuf.DecodeBitList, totalTxCount);
        var yParityBits = reader.Read(Protobuf.DecodeBitList, totalTxCount);

        // Signatures
        var signatures = new (UInt256 R, UInt256 S)[totalTxCount];
        for (var i = 0; i < (int)totalTxCount; ++i)
        {
            signatures[i] = (
                R: new UInt256(reader.Take(32).Span, true),
                S: new UInt256(reader.Take(32).Span, true));
        }

        var contractCreationCnt = (int)BigInteger.PopCount(contractCreationBits);

        var tos = new Address[(int)totalTxCount - contractCreationCnt];
        for (var i = 0; i < (int)totalTxCount - contractCreationCnt; ++i)
        {
            tos[i] = new Address(reader.Take(Address.Size).Span);
        }

        var datas = new ReadOnlyMemory<byte>[(int)totalTxCount];
        var types = new TxType[(int)totalTxCount];
        ulong legacyTxCnt = 0;
        for (var i = 0; i < (int)totalTxCount; ++i)
        {
            (datas[i], types[i]) = reader.Read(TxParser.Data);
            if (types[i] == TxType.Legacy)
            {
                legacyTxCnt++;
            }
        }

        var nonces = new ulong[(int)totalTxCount];
        for (var i = 0; i < (int)totalTxCount; ++i)
        {
            nonces[i] = reader.Read(Protobuf.DecodeULong);
        }

        var gases = new ulong[(int)totalTxCount];
        for (var i = 0; i < (int)totalTxCount; ++i)
        {
            gases[i] = reader.Read(Protobuf.DecodeULong);
        }

        var protectedBits = reader.Read(Protobuf.DecodeBitList, legacyTxCnt);

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
                Datas = datas,
                Types = types,
                TotalLegacyTxCount = legacyTxCnt,
                Nonces = nonces,
                Gases = gases,
                ProtectedBits = protectedBits,
            }
        };
    }
}
