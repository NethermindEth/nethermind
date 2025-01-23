// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Facade.Eth;
using Nethermind.Optimism.CL.Derivation;
using Nethermind.Optimism.Rpc;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Optimism.CL;

public class PayloadAttributesDeriver : IPayloadAttributesDeriver
{
    public readonly Address SequencerFeeVault = new("0x4200000000000000000000000000000000000011");

    private readonly ulong _chainId;
    private readonly DepositTransactionBuilder _depositTransactionBuilder;

    public PayloadAttributesDeriver(ulong chainId, DepositTransactionBuilder depositTransactionBuilder)
    {
        _chainId = chainId;
        _depositTransactionBuilder = depositTransactionBuilder;
    }

    public OptimismPayloadAttributes[] DerivePayloadAttributes(BatchV1 batch, OptimismPayloadAttributes l2Parent,
        BlockForRpc l1Origin, L1BlockInfo l1OriginBlockInfo, SystemConfig systemConfig)
    {
        // TODO we need to check that data is consistent(l2 parent and l1 origin are correct)
        OptimismPayloadAttributes[] payloadAttributes = new OptimismPayloadAttributes[batch.BlockCount];
        Transaction[] systemTransaction = new []{ _depositTransactionBuilder.BuildSystemTransaction(l1OriginBlockInfo) };
        payloadAttributes[0] = BuildFirstBlockInEpoch(batch, l2Parent, l1Origin, l1OriginBlockInfo, systemConfig);
        ulong txCount = batch.BlockTxCounts[0];
        for (ulong i = 1; i < batch.BlockCount; i++)
        {
            Transaction[] userTxs = systemTransaction.Concat(BuildUserTransactions(batch, txCount, txCount + batch.BlockTxCounts[i])).ToArray();
            payloadAttributes[i] = BuildOneBlock(l1Origin, payloadAttributes[i - 1], systemConfig, userTxs);
            txCount += batch.BlockTxCounts[i];
        }
        return payloadAttributes;
    }

    private OptimismPayloadAttributes BuildFirstBlockInEpoch(BatchV1 batch, OptimismPayloadAttributes l2Parent,
        BlockForRpc l1Origin, L1BlockInfo l1OriginBlockInfo, SystemConfig systemConfig)
    {
        Transaction systemTransaction = _depositTransactionBuilder.BuildSystemTransaction(l1OriginBlockInfo);
        Transaction[] userDepositTxs = _depositTransactionBuilder.BuildUserDepositTransactions();
        Transaction[] upgradeTxs = _depositTransactionBuilder.BuildUpgradeTransactions();
        Transaction[] forceIncludeTxs = _depositTransactionBuilder.BuildForceIncludeTransactions();
        Transaction[] userTransactionsFirstBlock = BuildUserTransactions(batch, 0, batch.BlockTxCounts[0]);

        Transaction[] allTxs = new[] { systemTransaction }.Concat(userDepositTxs).Concat(upgradeTxs)
            .Concat(forceIncludeTxs).Concat(userTransactionsFirstBlock).ToArray();

        return BuildOneBlock(l1Origin, l2Parent, systemConfig, allTxs);
    }

    private OptimismPayloadAttributes BuildOneBlock(BlockForRpc l1Origin, OptimismPayloadAttributes l2Parent, SystemConfig systemConfig, Transaction[] txs)
    {
        OptimismPayloadAttributes payload = new()
        {
            GasLimit = (long)systemConfig.GasLimit,
            NoTxPool = true,
            ParentBeaconBlockRoot = l1Origin.ParentBeaconBlockRoot,
            Timestamp = l2Parent.Timestamp + 2,
            Withdrawals = [],
            PrevRandao = l1Origin.MixHash,
            SuggestedFeeRecipient = SequencerFeeVault,
        };
        payload.SetTransactions(txs);
        return payload;
    }

    private Transaction[] BuildUserTransactions(BatchV1 batch, ulong from, ulong to)
    {
        var userTransactions = new Transaction[to - from];
        for (ulong i = from; i < to; i++)
        {
            userTransactions[i] = new Transaction
            {
                ChainId = _chainId,
                Data = batch.Txs.Datas[i],
                Type = batch.Txs.Types[i],
                Signature = batch.Txs.Signatures[i],
                To = batch.Txs.Tos[i],
                Nonce = batch.Txs.Nonces[i],
                GasLimit = (long)batch.Txs.Gases[i],
            };
        }

        return userTransactions;
    }
}
