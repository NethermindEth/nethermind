// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Facade.Eth.RpcTransaction;

public class RpcEIP1559Transaction : RpcAccessListTransaction
{
    public override TxType? Type => TxType.EIP1559;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public UInt256? MaxPriorityFeePerGas { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public UInt256? MaxFeePerGas { get; set; }

    [JsonConstructor]
    public RpcEIP1559Transaction() { }

    public RpcEIP1559Transaction(Transaction transaction, int? txIndex = null, Hash256? blockHash = null, long? blockNumber = null, UInt256? baseFee = null)
        : base(transaction, txIndex, blockHash, blockNumber)
    {
        MaxFeePerGas = transaction.MaxFeePerGas;
        MaxPriorityFeePerGas = transaction.MaxPriorityFeePerGas;
        GasPrice = baseFee is not null
                ? transaction.CalculateEffectiveGasPrice(eip1559Enabled: true, baseFee.Value)
                : transaction.MaxFeePerGas;
    }

    public override Transaction ToTransaction()
    {
        var tx = base.ToTransaction();

        tx.GasPrice = MaxPriorityFeePerGas ?? 0;
        tx.DecodedMaxFeePerGas = MaxFeePerGas ?? 0;

        return tx;
    }

    public new static readonly IFromTransaction<RpcEIP1559Transaction> Converter = new ConverterImpl();

    private class ConverterImpl : IFromTransaction<RpcEIP1559Transaction>
    {
        public RpcEIP1559Transaction FromTransaction(Transaction tx, TransactionConverterExtraData extraData)
            => new(tx, txIndex: extraData.TxIndex, blockHash: extraData.BlockHash, blockNumber: extraData.BlockNumber, baseFee: extraData.BaseFee);
    }
}
