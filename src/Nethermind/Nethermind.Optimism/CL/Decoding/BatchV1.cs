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

public class BatchV1
{
    public required ulong RelTimestamp;
    public required ulong L1OriginNum;
    public required byte[] ParentCheck; // 20 bytes
    public required byte[] L1OriginCheck; // 20 bytes
    public required ulong BlockCount;
    public required BigInteger OriginBits;
    public required ulong[] BlockTxCounts;
    public required BatchV1Transactions Txs;

    public IEnumerable<SingularBatch> ToSingularBatches(ulong chainId, ulong genesisTimestamp, ulong blockTime)
    {
        ulong currentL1OriginNum = L1OriginNum;
        ulong currentTimestamp = genesisTimestamp + RelTimestamp;
        ulong tosFrom = 0;
        ulong legacyTxFrom = 0;
        ulong txIdx = 0;
        for (int i = 0; i < (int)BlockCount; i++)
        {
            SingularBatch batch = new();
            bool isNewOrigin = ((OriginBits >> i) & 1) == 1;
            if (isNewOrigin)
            {
                currentL1OriginNum++;
            }

            batch.IsFirstBlockInEpoch = isNewOrigin;
            batch.EpochNumber = currentL1OriginNum;
            batch.Timestamp = currentTimestamp;
            currentTimestamp += blockTime;
            (Transaction[] userTransactions, tosFrom, legacyTxFrom) =
                BuildUserTransactions(
                    chainId, txIdx, BlockTxCounts[i], tosFrom, legacyTxFrom);
            txIdx += BlockTxCounts[i];
            batch.Transactions = userTransactions
                .Select(static t => Rlp.Encode(t, RlpBehaviors.SkipTypedWrapping).Bytes)
                .ToArray().ToArray();
            yield return batch;
        }
    }

    private (Transaction[], ulong, ulong) BuildUserTransactions(ulong chainId, ulong from, ulong txCount, ulong tosFrom,
        ulong legacyTxFrom)
    {
        var userTransactions = new Transaction[txCount];
        ulong tosIdx = tosFrom;
        ulong legacyTxIdx = legacyTxFrom;
        for (ulong i = 0; i < txCount; i++)
        {
            ulong txIdx = from + i;
            bool parityBit = ((Txs.YParityBits >> (int)txIdx) & 1) == 1;
            var tx = new Transaction
            {
                ChainId = chainId,
                Type = Txs.Types[txIdx],
                Nonce = Txs.Nonces[txIdx],
                GasLimit = (long)Txs.Gases[txIdx],
            };
            bool contractCreationBit = ((Txs.ContractCreationBits >> (int)txIdx) & 1) == 1;
            if (!contractCreationBit)
            {
                tx.To = Txs.Tos[tosIdx];
                tosIdx++;
            }

            ulong v;
            switch (Txs.Types[txIdx])
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

                        (tx.Value, tx.GasPrice, tx.Data) = DecodeLegacyTransaction(Txs.Datas[txIdx]);
                        break;
                    }
                case TxType.AccessList:
                    {
                        v = EthereumEcdsaExtensions.CalculateV(chainId, parityBit);
                        (tx.Value, tx.GasPrice, tx.Data, tx.AccessList) = DecodeAccessListTransaction(Txs.Datas[txIdx]);
                        break;
                    }
                case TxType.EIP1559:
                    {
                        v = EthereumEcdsaExtensions.CalculateV(chainId, parityBit);
                        (tx.Value, tx.GasPrice, tx.DecodedMaxFeePerGas, tx.Data, tx.AccessList) =
                            DecodeEip1559Transaction(Txs.Datas[txIdx]);
                        break;
                    }
                default:
                    {
                        throw new ArgumentException($"Invalid tx type {Txs.Types[txIdx]}");
                    }
            }

            Signature signature = new(Txs.Signatures[txIdx].R, Txs.Signatures[txIdx].S, v);
            tx.Signature = signature;
            userTransactions[i] = tx;
        }

        return (userTransactions, tosIdx, legacyTxIdx);
    }

    private (UInt256 Value, UInt256 GasPrice, byte[] Data) DecodeLegacyTransaction(byte[] encoded)
    {
        // rlp_encode(value, gasPrice, data)
        RlpStream stream = new(encoded);
        int length = stream.ReadSequenceLength();
        UInt256 value = stream.DecodeUInt256();
        UInt256 gasPrice = stream.DecodeUInt256();
        byte[] data = stream.DecodeByteArray();
        return (value, gasPrice, data);
    }

    private (UInt256 Value, UInt256 GasPrice, byte[] Data, AccessList? AccessList)
        DecodeAccessListTransaction(byte[] encoded)
    {
        // 0x01 ++ rlp_encode(value, gasPrice, data, accessList)
        RlpStream stream = new(encoded);
        stream.ReadByte(); // type
        int length = stream.ReadSequenceLength();
        UInt256 value = stream.DecodeUInt256();
        UInt256 gasPrice = stream.DecodeUInt256();
        byte[] data = stream.DecodeByteArray();
        AccessList? accessList = AccessListDecoder.Instance.Decode(stream);
        return (value, gasPrice, data, accessList);
    }

    private (UInt256 Value, UInt256 MaxPriorityFeePerGas, UInt256 MaxFeePerGas, byte[] Data, AccessList? AccessList)
        DecodeEip1559Transaction(byte[] encoded)
    {
        // 0x02 ++ rlp_encode(value, max_priority_fee_per_gas, max_fee_per_gas, data, access_list)
        RlpStream stream = new(encoded);
        byte type = stream.ReadByte(); // type
        int length = stream.ReadSequenceLength();
        UInt256 value = stream.DecodeUInt256();
        UInt256 maxPriorityFeePerGas = stream.DecodeUInt256();
        UInt256 maxFeePerGas = stream.DecodeUInt256();
        byte[] data = stream.DecodeByteArray();
        AccessList? accessList = AccessListDecoder.Instance.Decode(stream);
        return (value, maxPriorityFeePerGas, maxFeePerGas, data, accessList);
    }
}
