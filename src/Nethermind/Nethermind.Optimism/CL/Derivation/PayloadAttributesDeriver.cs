// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.Logging;
using Nethermind.Optimism.CL.Decoding;
using Nethermind.Optimism.CL.Derivation;
using Nethermind.Optimism.CL.L1Bridge;
using Nethermind.Optimism.Rpc;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip2930;

namespace Nethermind.Optimism.CL;

public class PayloadAttributesDeriver : IPayloadAttributesDeriver
{
    public readonly Address SequencerFeeVault = new("0x4200000000000000000000000000000000000011");

    private readonly ulong _chainId;
    private readonly DepositTransactionBuilder _depositTransactionBuilder;
    private readonly ISystemConfigDeriver _systemConfigDeriver;
    private readonly ILogger _logger;

    OptimismTxDecoder<Transaction> decoder = new();


    public PayloadAttributesDeriver(ulong chainId, ISystemConfigDeriver systemConfigDeriver, DepositTransactionBuilder depositTransactionBuilder, ILogger logger)
    {
        _chainId = chainId;
        _depositTransactionBuilder = depositTransactionBuilder;
        _systemConfigDeriver = systemConfigDeriver;
        _logger = logger;
    }

    public PayloadAttributesRef[] DerivePayloadAttributes(BatchV1 batch, L2Block l2Parent, L1Block[] l1Origins, ReceiptForRpc[][] l1Receipts)
    {
        // TODO we need to check that data is consistent(l2 parent and l1 origin are correct)
        PayloadAttributesRef[] result = new PayloadAttributesRef[batch.BlockCount];
        ulong txIdx = 0;
        int originIdx = 0;
        ulong l2ParentTimestamp = l2Parent.PayloadAttributes.Timestamp;
        SystemConfig currentSystemConfig = l2Parent.SystemConfig;
        L1BlockInfo currentL1OriginBlockInfo = l2Parent.L1BlockInfo;
        for (int i = 0; i < (int)batch.BlockCount; i++)
        {
            bool isNewOrigin = ((batch.OriginBits >> i) & 1) == 1;
            OptimismPayloadAttributes payloadAttributes;
            if (isNewOrigin)
            {
                originIdx++;
                currentSystemConfig =
                    _systemConfigDeriver.UpdateSystemConfigFromL1BLockReceipts(currentSystemConfig, l1Receipts[originIdx]);

                currentL1OriginBlockInfo = L1BlockInfoBuilder.FromL1BlockAndSystemConfig(l1Origins[originIdx], currentSystemConfig, 0);
            }
            else
            {
                currentL1OriginBlockInfo.SequenceNumber++;
            }

            Transaction systemTransaction = _depositTransactionBuilder.BuildL1InfoTransaction(currentL1OriginBlockInfo);
            systemTransaction.Nonce = l2Parent.Number + 2 + (ulong)i;

            if (isNewOrigin)
            {
                payloadAttributes = BuildFirstBlockInEpoch(batch, l2ParentTimestamp, l1Origins[originIdx],
                currentSystemConfig, systemTransaction, l1Receipts[originIdx], i, txIdx);
            }
            else
            {
                payloadAttributes = BuildRegularBlock(batch, l2ParentTimestamp, l1Origins[originIdx], currentSystemConfig, systemTransaction, i, txIdx);
            }

            result[i] = new()
            {
                SystemConfig = currentSystemConfig,
                L1BlockInfo = currentL1OriginBlockInfo,
                Number = batch.RelTimestamp / 2 + (ulong)i,
                PayloadAttributes = payloadAttributes
            };
            l2ParentTimestamp = payloadAttributes.Timestamp;
            txIdx += batch.BlockTxCounts[i];
        }
        return result;
    }

    private OptimismPayloadAttributes BuildFirstBlockInEpoch(BatchV1 batch, ulong l2ParentTimestamp,
        L1Block l1Origin, SystemConfig systemConfig, Transaction systemTransaction, ReceiptForRpc[] l1OriginReceipts, int blockIdx, ulong txsFrom)
    {
        List<Transaction> transactions = new();
        transactions.AddRange(_depositTransactionBuilder.BuildUserDepositTransactions(l1OriginReceipts));
        // Transaction[] upgradeTxs = _depositTransactionBuilder.BuildUpgradeTransactions();
        // Transaction[] forceIncludeTxs = _depositTransactionBuilder.BuildForceIncludeTransactions();
        Transaction[] userTransactions = BuildUserTransactions(batch, txsFrom, batch.BlockTxCounts[blockIdx]);
        transactions.AddRange(userTransactions);

        // Transaction[] allTxs = new[] { systemTransaction }.Concat(userDepositTxs).Concat(upgradeTxs)
        //     .Concat(forceIncludeTxs).Concat(userTransactions).ToArray();

        return BuildOneBlock(l1Origin, l2ParentTimestamp, systemConfig, systemTransaction, transactions.ToArray());
    }

    private OptimismPayloadAttributes BuildRegularBlock(BatchV1 batch, ulong l2ParentTimestamp,
        L1Block l1Origin, SystemConfig systemConfig, Transaction systemTransaction, int blockIdx, ulong txsFrom)
    {
        Transaction[] userTransactions = BuildUserTransactions(batch, txsFrom, batch.BlockTxCounts[blockIdx]);

        return BuildOneBlock(l1Origin, l2ParentTimestamp, systemConfig, systemTransaction, userTransactions);
    }

    private OptimismPayloadAttributes BuildOneBlock(L1Block l1Origin, ulong l2ParentTimestamp, SystemConfig systemConfig, Transaction systemTx, Transaction[] userTxs)
    {
        OptimismPayloadAttributes payload = new()
        {
            GasLimit = (long)systemConfig.GasLimit,
            NoTxPool = true,
            ParentBeaconBlockRoot = l1Origin.ParentBeaconBlockRoot,
            Timestamp = l2ParentTimestamp + 2,
            Withdrawals = [],
            PrevRandao = l1Origin.MixHash,
            EIP1559Params = systemConfig.EIP1559Params,
            SuggestedFeeRecipient = SequencerFeeVault,
            Transactions = (new [] { Rlp.Encode(systemTx, RlpBehaviors.SkipTypedWrapping).Bytes }).Concat(userTxs
                .Select(static t => Rlp.Encode(t, RlpBehaviors.SkipTypedWrapping).Bytes)
                .ToArray()).ToArray()
        };
        return payload;
    }

    private Transaction[] BuildUserTransactions(BatchV1 batch, ulong from, ulong txCount)
    {
        var userTransactions = new Transaction[txCount];
        for (ulong i = 0; i < txCount; i++)
        {
            ulong txIdx = from + i;
            bool parityBit = ((batch.Txs.YParityBits >> (int)txIdx) & 1) == 1;
            ulong v = EthereumEcdsaExtensions.CalculateV(_chainId, parityBit);
            Signature signature = new(batch.Txs.Signatures[txIdx].R, batch.Txs.Signatures[txIdx].S, v);
            var tx = new Transaction
            {
                ChainId = _chainId,
                Type = batch.Txs.Types[txIdx],
                Signature = signature,
                To = batch.Txs.Tos[txIdx],
                Nonce = batch.Txs.Nonces[txIdx],
                GasLimit = (long)batch.Txs.Gases[txIdx],
            };
            switch (batch.Txs.Types[txIdx])
            {
                case TxType.Legacy:
                {
                    (tx.Value, tx.GasPrice, tx.Data) = DecodeLegacyTransaction(batch.Txs.Datas[txIdx]);
                    break;
                }
                case TxType.AccessList:
                {
                    (tx.Value, tx.GasPrice, tx.Data, tx.AccessList) = DecodeAccessListTransaction(batch.Txs.Datas[txIdx]);
                    break;
                }
                case TxType.EIP1559:
                {
                    (tx.Value, tx.GasPrice, tx.DecodedMaxFeePerGas, tx.Data, tx.AccessList) = DecodeEip1559Transaction(batch.Txs.Datas[txIdx]);
                    break;
                }
            }
            userTransactions[i] = tx;

        }

        return userTransactions;
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
