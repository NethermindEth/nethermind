// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip2930;

namespace Nethermind.Optimism.CL.Decoding;

public sealed class BatchV1
{
    public required ulong RelTimestamp;
    public required ulong L1OriginNum;
    public required ReadOnlyMemory<byte> ParentCheck; // 20 bytes
    public required ReadOnlyMemory<byte> L1OriginCheck; // 20 bytes
    public required ulong BlockCount;
    public required BigInteger OriginBits;
    public required IReadOnlyList<ulong> BlockTxCounts;
    public required Transactions Txs;

    public sealed class Transactions
    {
        public required BigInteger ContractCreationBits;
        public required BigInteger YParityBits;
        public required IReadOnlyList<(UInt256 R, UInt256 S)> Signatures; // TODO: Do we want to use `Nethermind.Core.Crypto.Signature`?
        public required IReadOnlyList<Address> Tos;
        public required IReadOnlyList<ReadOnlyMemory<byte>> Data;
        public required IReadOnlyList<TxType> Types;
        public required ulong TotalLegacyTxCount;
        public required IReadOnlyList<ulong> Nonces;
        public required IReadOnlyList<ulong> Gases;
        public required BigInteger ProtectedBits;
    }

    public IEnumerable<SingularBatch> ToSingularBatches(ulong chainId, ulong genesisTimestamp, ulong blockTime)
    {
        ulong currentL1OriginNum = L1OriginNum;
        ulong currentTimestamp = genesisTimestamp + RelTimestamp;
        ulong tosFrom = 0;
        ulong legacyTxFrom = 0;
        ulong txIdx = 0;
        for (int i = 0; i < (int)BlockCount; i++)
        {

            bool isNewOrigin = ((OriginBits >> i) & 1) == 1;
            if (isNewOrigin)
            {
                currentL1OriginNum++;
            }

            (Transaction[] userTransactions, tosFrom, legacyTxFrom) = BuildUserTransactions(chainId, txIdx, BlockTxCounts[i], tosFrom, legacyTxFrom);
            yield return new()
            {
                IsFirstBlockInEpoch = isNewOrigin,
                EpochNumber = currentL1OriginNum,
                Timestamp = currentTimestamp,
                Transactions = userTransactions
                    .Select(static t => Rlp.Encode(t, RlpBehaviors.SkipTypedWrapping).Bytes)
                    .ToArray()
            };
            txIdx += BlockTxCounts[i];
            currentTimestamp += blockTime;
        }
    }

    private (Transaction[], ulong, ulong) BuildUserTransactions(
        ulong chainId,
        ulong from,
        ulong txCount,
        ulong tosFrom,
        ulong legacyTxFrom)
    {
        Transaction[] userTransactions = new Transaction[txCount];
        ulong tosIdx = tosFrom;
        ulong legacyTxIdx = legacyTxFrom;
        for (ulong i = 0; i < txCount; i++)
        {
            ulong txIdx = from + i;
            bool parityBit = ((Txs.YParityBits >> (int)txIdx) & 1) == 1;
            Transaction tx = new()
            {
                ChainId = chainId,
                Type = Txs.Types[(int)txIdx],
                Nonce = Txs.Nonces[(int)txIdx],
                GasLimit = Txs.Gases[(int)txIdx],
            };
            bool contractCreationBit = ((Txs.ContractCreationBits >> (int)txIdx) & 1) == 1;
            if (!contractCreationBit)
            {
                tx.To = Txs.Tos[(int)tosIdx];
                tosIdx++;
            }

            ulong v;
            switch (Txs.Types[(int)txIdx])
            {
                case TxType.Legacy:
                    {
                        bool protectedBit = ((Txs.ProtectedBits >> (int)legacyTxIdx) & 1) == 1;
                        legacyTxIdx++;
                        if (protectedBit)
                        {
                            v = EthereumEcdsaExtensions.CalculateV(chainId, parityBit);
                        }
                        else
                        {
                            v = 27u + (parityBit ? 1u : 0u);
                        }

                        (UInt256 legacyValue, tx.GasPrice, byte[] legacyData) = DecodeLegacyTransaction(Txs.Data[(int)txIdx].Span);
                        tx.Value = legacyValue;
                        tx.Data = legacyData;
                        break;
                    }
                case TxType.AccessList:
                    {
                        v = EthereumEcdsaExtensions.CalculateV(chainId, parityBit);
                        (UInt256 accessValue, tx.GasPrice, byte[] accessData, tx.AccessList) = DecodeAccessListTransaction(Txs.Data[(int)txIdx].Span);
                        tx.Value = accessValue;
                        tx.Data = accessData;
                        break;
                    }
                case TxType.EIP1559:
                    {
                        v = EthereumEcdsaExtensions.CalculateV(chainId, parityBit);
                        (UInt256 eipValue, tx.GasPrice, UInt256 maxFee, byte[] eipData, tx.AccessList) =
                            DecodeEip1559Transaction(Txs.Data[(int)txIdx].Span);
                        tx.Value = eipValue;
                        tx.DecodedMaxFeePerGas = maxFee;
                        tx.Data = eipData;
                        break;
                    }
                default:
                    {
                        throw new ArgumentException($"Invalid tx type {Txs.Types[(int)txIdx]}");
                    }
            }

            Signature signature = new(Txs.Signatures[(int)txIdx].R, Txs.Signatures[(int)txIdx].S, v);
            tx.Signature = signature;
            userTransactions[i] = tx;
        }

        return (userTransactions, tosIdx, legacyTxIdx);
    }

    private (UInt256 Value, UInt256 GasPrice, byte[] Data) DecodeLegacyTransaction(ReadOnlySpan<byte> encoded)
    {
        // rlp_encode(value, gasPrice, data)
        RlpReader decoder = new(encoded);
        decoder.ReadSequenceLength();
        return (decoder.DecodeUInt256(), decoder.DecodeUInt256(), decoder.DecodeByteArray());
    }

    private (UInt256 Value, UInt256 GasPrice, byte[] Data, AccessList? AccessList) DecodeAccessListTransaction(ReadOnlySpan<byte> encoded)
    {
        // 0x01 ++ rlp_encode(value, gasPrice, data, accessList)
        RlpReader decoder = new(encoded);
        decoder.ReadSequenceLength();
        return (decoder.DecodeUInt256(), decoder.DecodeUInt256(), decoder.DecodeByteArray(), AccessListDecoder.Instance.Decode(ref decoder));
    }

    private (UInt256 Value, UInt256 MaxPriorityFeePerGas, UInt256 MaxFeePerGas, byte[] Data, AccessList? AccessList) DecodeEip1559Transaction(ReadOnlySpan<byte> encoded)
    {
        // 0x02 ++ rlp_encode(value, max_priority_fee_per_gas, max_fee_per_gas, data, access_list)
        RlpReader decoder = new(encoded);
        decoder.ReadSequenceLength();
        return (decoder.DecodeUInt256(), decoder.DecodeUInt256(), decoder.DecodeUInt256(), decoder.DecodeByteArray(), AccessListDecoder.Instance.Decode(ref decoder));
    }
}
