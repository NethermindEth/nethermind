// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Data;
using Nethermind.Logging;
using Nethermind.Optimism.CL.Derivation;
using Nethermind.Optimism.Rpc;

namespace Nethermind.Optimism.CL;

public class PayloadAttributesDeriver : IPayloadAttributesDeriver
{
    public readonly Address SequencerFeeVault = new("0x4200000000000000000000000000000000000011");

    private readonly ulong _chainId;
    private readonly DepositTransactionBuilder _depositTransactionBuilder;
    private readonly ISystemConfigDeriver _systemConfigDeriver;
    private readonly ILogger _logger;

    public PayloadAttributesDeriver(ulong chainId, ISystemConfigDeriver systemConfigDeriver, DepositTransactionBuilder depositTransactionBuilder, ILogger logger)
    {
        _chainId = chainId;
        _depositTransactionBuilder = depositTransactionBuilder;
        _systemConfigDeriver = systemConfigDeriver;
        _logger = logger;
    }

    public OptimismPayloadAttributes[] DerivePayloadAttributes(BatchV1 batch, L2Block l2Parent, BlockForRpc[] l1Origins, ReceiptForRpc[][] l1Receipts)
    {
        // TODO we need to check that data is consistent(l2 parent and l1 origin are correct)
        OptimismPayloadAttributes[] payloadAttributes = new OptimismPayloadAttributes[batch.BlockCount];
        ulong txIdx = 0;
        int originIdx = 0;
        ulong l2ParentTimestamp = l2Parent.Timestamp;
        SystemConfig currentSystemConfig =
            _systemConfigDeriver.UpdateSystemConfigFromL1BLockReceipts(l2Parent.SystemConfig, l1Receipts[originIdx]);
        L1BlockInfo currentL1OriginBlockInfo = L1BlockInfoBuilder.FromL1BlockAndSystemConfig(l1Origins[0], currentSystemConfig);
        Transaction currentSystemTransaction =
            _depositTransactionBuilder.BuildSystemTransaction(currentL1OriginBlockInfo);
        currentSystemTransaction.Nonce = l2Parent.Number + 2;
        payloadAttributes[0] =
            BuildFirstBlockInEpoch(batch, l2ParentTimestamp, l1Origins[0], currentSystemConfig,
                currentSystemTransaction, 0, 0);
        for (int i = 1; i < (int)batch.BlockCount; i++)
        {
            if (((batch.OriginBits >> i) & 1) == 1)
            {
                // New origin => next epoch
                originIdx++;
                BlockForRpc l1Origin = l1Origins[originIdx];
                currentSystemConfig =
                    _systemConfigDeriver.UpdateSystemConfigFromL1BLockReceipts(currentSystemConfig, l1Receipts[originIdx]);

                currentL1OriginBlockInfo = L1BlockInfoBuilder.FromL1BlockAndSystemConfig(l1Origin, currentSystemConfig);
                currentSystemTransaction = _depositTransactionBuilder.BuildSystemTransaction(currentL1OriginBlockInfo);
                currentSystemTransaction.Nonce = l2Parent.Number + 2 + (ulong)i;

                payloadAttributes[i] = BuildFirstBlockInEpoch(batch, l2ParentTimestamp, l1Origin,
                    currentSystemConfig, currentSystemTransaction, i, txIdx);
            }
            else
            {
                currentSystemTransaction.Nonce = l2Parent.Number + 2 + (ulong)i;
                Transaction[] txs =
                    new[] { currentSystemTransaction }.Concat(BuildUserTransactions(batch, txIdx,
                        batch.BlockTxCounts[i])).ToArray();
                payloadAttributes[i] = BuildOneBlock(l1Origins[originIdx], l2ParentTimestamp, currentSystemConfig, txs);

            }

            l2ParentTimestamp = payloadAttributes[i].Timestamp;
            txIdx += batch.BlockTxCounts[i];
        }
        return payloadAttributes;
    }

    private OptimismPayloadAttributes BuildFirstBlockInEpoch(BatchV1 batch, ulong l2ParentTimestamp,
        BlockForRpc l1Origin, SystemConfig systemConfig, Transaction systemTransaction, int blockIdx, ulong txsFrom)
    {
        Transaction[] userDepositTxs = _depositTransactionBuilder.BuildUserDepositTransactions();
        Transaction[] upgradeTxs = _depositTransactionBuilder.BuildUpgradeTransactions();
        Transaction[] forceIncludeTxs = _depositTransactionBuilder.BuildForceIncludeTransactions();
        Transaction[] userTransactions = BuildUserTransactions(batch, txsFrom, batch.BlockTxCounts[blockIdx]);

        Transaction[] allTxs = new[] { systemTransaction }.Concat(userDepositTxs).Concat(upgradeTxs)
            .Concat(forceIncludeTxs).Concat(userTransactions).ToArray();

        return BuildOneBlock(l1Origin, l2ParentTimestamp, systemConfig, allTxs);
    }

    private OptimismPayloadAttributes BuildOneBlock(BlockForRpc l1Origin, ulong l2ParentTimestamp, SystemConfig systemConfig, Transaction[] txs)
    {
        OptimismPayloadAttributes payload = new()
        {
            GasLimit = (long)systemConfig.GasLimit,
            NoTxPool = true,
            ParentBeaconBlockRoot = l1Origin.ParentBeaconBlockRoot,
            Timestamp = l2ParentTimestamp + 2,
            Withdrawals = [],
            PrevRandao = l1Origin.MixHash,
            SuggestedFeeRecipient = SequencerFeeVault,
        };
        payload.SetTransactions(txs);
        return payload;
    }

    private Transaction[] BuildUserTransactions(BatchV1 batch, ulong from, ulong txCount)
    {
        var userTransactions = new Transaction[txCount];
        for (ulong i = 0; i < txCount; i++)
        {
            ulong txIdx = from + i;
            userTransactions[i] = new Transaction
            {
                ChainId = _chainId,
                Data = batch.Txs.Datas[txIdx],
                Type = batch.Txs.Types[txIdx],
                Signature = batch.Txs.Signatures[txIdx],
                To = batch.Txs.Tos[txIdx],
                Nonce = batch.Txs.Nonces[txIdx],
                GasLimit = (long)batch.Txs.Gases[txIdx],
            };
        }

        return userTransactions;
    }
}
