// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Facade.Eth.RpcTransaction;

public class EIP1559TransactionForRpc : AccessListTransactionForRpc, IFromTransaction<EIP1559TransactionForRpc>
{
    public new static TxType TxType => TxType.EIP1559;

    public override TxType? Type => TxType.EIP1559;

    [JsonDiscriminator]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public UInt256? MaxPriorityFeePerGas { get; set; }

    [JsonDiscriminator]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public UInt256? MaxFeePerGas { get; set; }

    [JsonConstructor]
    public EIP1559TransactionForRpc() { }

    public EIP1559TransactionForRpc(Transaction transaction, int? txIndex = null, Hash256? blockHash = null, long? blockNumber = null, UInt256? baseFee = null, ulong? chainId = null)
        : base(transaction, txIndex, blockHash, blockNumber, chainId)
    {
        MaxFeePerGas = transaction.MaxFeePerGas;
        MaxPriorityFeePerGas = transaction.MaxPriorityFeePerGas;
        GasPrice = null;
    }

    public override Transaction ToTransaction()
    {
        var tx = base.ToTransaction();

        tx.GasPrice = MaxPriorityFeePerGas ?? 0;
        tx.DecodedMaxFeePerGas = MaxFeePerGas ?? 0;

        return tx;
    }

    public override bool ShouldSetBaseFee() =>
        base.ShouldSetBaseFee() || MaxFeePerGas.IsPositive() || MaxPriorityFeePerGas.IsPositive();

    public new static EIP1559TransactionForRpc FromTransaction(Transaction tx, TransactionConverterExtraData extraData)
        => new(tx, txIndex: extraData.TxIndex, blockHash: extraData.BlockHash, blockNumber: extraData.BlockNumber, baseFee: extraData.BaseFee, chainId: extraData.ChainId);
}
