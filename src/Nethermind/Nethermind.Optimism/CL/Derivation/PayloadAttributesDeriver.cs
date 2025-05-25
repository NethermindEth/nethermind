// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core;
using Nethermind.JsonRpc.Data;
using Nethermind.Optimism.CL.Decoding;
using Nethermind.Optimism.Rpc;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Optimism.CL.Derivation;

public class PayloadAttributesDeriver(
    ISystemConfigDeriver systemConfigDeriver,
    DepositTransactionBuilder depositTransactionBuilder
) : IPayloadAttributesDeriver
{
    public readonly Address SequencerFeeVault = new("0x4200000000000000000000000000000000000011");

    private readonly DepositTransactionBuilder _depositTransactionBuilder = depositTransactionBuilder;
    private readonly ISystemConfigDeriver _systemConfigDeriver = systemConfigDeriver;

    public PayloadAttributesRef? TryDerivePayloadAttributes(
        SingularBatch batch,
        PayloadAttributesRef parentPayloadAttributes,
        L1Block l1Origin,
        ReceiptForRpc[] l1Receipts)
    {
        ulong number = parentPayloadAttributes.Number + 1;
        SystemConfig systemConfig = parentPayloadAttributes.SystemConfig;

        if (batch.IsFirstBlockInEpoch)
        {
            systemConfig =
                _systemConfigDeriver.UpdateSystemConfigFromL1BLockReceipts(systemConfig, l1Receipts);
        }

        L1BlockInfo l1BlockInfo = L1BlockInfoBuilder.FromL1BlockAndSystemConfig(l1Origin, systemConfig,
            batch.IsFirstBlockInEpoch ? 0 : parentPayloadAttributes.L1BlockInfo.SequenceNumber + 1);

        Transaction systemTransaction = _depositTransactionBuilder.BuildL1InfoTransaction(l1BlockInfo);
        systemTransaction.Nonce = number + 1;

        OptimismPayloadAttributes payloadAttributes;
        if (batch.IsFirstBlockInEpoch)
        {
            payloadAttributes = BuildFirstBlockInEpoch(batch, l1Origin, systemConfig, systemTransaction, l1Receipts);
        }
        else
        {
            payloadAttributes = BuildRegularBlock(batch, l1Origin, systemConfig, systemTransaction);
        }

        return new PayloadAttributesRef()
        {
            L1BlockInfo = l1BlockInfo,
            Number = number,
            PayloadAttributes = payloadAttributes,
            SystemConfig = systemConfig,
        };
    }

    private OptimismPayloadAttributes BuildRegularBlock(
        SingularBatch batch,
        L1Block l1Origin,
        SystemConfig systemConfig,
        Transaction systemTransaction)
    {
        byte[][] transactions =
        [
            Rlp.Encode(systemTransaction, RlpBehaviors.SkipTypedWrapping).Bytes,
            .. batch.Transactions
        ];
        return BuildOneBlock(l1Origin, batch.Timestamp, systemConfig, transactions);
    }

    private OptimismPayloadAttributes BuildFirstBlockInEpoch(
        SingularBatch batch,
        L1Block l1Origin,
        SystemConfig systemConfig,
        Transaction systemTransaction,
        ReceiptForRpc[] l1OriginReceipts)
    {
        byte[][] transactions =
        [
            Rlp.Encode(systemTransaction, RlpBehaviors.SkipTypedWrapping).Bytes,
            .. _depositTransactionBuilder.BuildUserDepositTransactions(l1OriginReceipts)
                    .Select(x => Rlp.Encode(x, RlpBehaviors.SkipTypedWrapping).Bytes),
            .. batch.Transactions,
        ];
        return BuildOneBlock(l1Origin, batch.Timestamp, systemConfig, transactions);
    }

    private OptimismPayloadAttributes BuildOneBlock(L1Block l1Origin, ulong timestamp, SystemConfig systemConfig, byte[][] txs)
    {
        OptimismPayloadAttributes payload = new()
        {
            GasLimit = (long)systemConfig.GasLimit,
            NoTxPool = true,
            ParentBeaconBlockRoot = l1Origin.ParentBeaconBlockRoot,
            Timestamp = timestamp,
            Withdrawals = [],
            PrevRandao = l1Origin.MixHash,
            EIP1559Params = systemConfig.EIP1559Params,
            SuggestedFeeRecipient = SequencerFeeVault,
            Transactions = txs
        };
        return payload;
    }
}
