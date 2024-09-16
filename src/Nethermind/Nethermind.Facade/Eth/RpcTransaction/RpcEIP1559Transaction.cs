// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Facade.Eth.RpcTransaction;

public class RpcEIP1559Transaction : RpcAccessListTransaction
{
    public UInt256 MaxPriorityFeePerGas { get; set; }

    public UInt256 MaxFeePerGas { get; set; }

    /// <summary>
    /// The effective gas price paid by the sender in wei. For transactions not yet included in a block, this value should be set equal to the max fee per gas.
    /// This field is <b>DEPRECATED</b>, please transition to using <c>effectiveGasPrice</c> in the receipt object going forward.
    /// </summary>
    public override UInt256 GasPrice { get; set; }

    public RpcEIP1559Transaction(Transaction transaction, int? txIndex = null, Hash256? blockHash = null, long? blockNumber = null, UInt256? baseFee = null)
        : base(transaction, txIndex, blockHash, blockNumber)
    {
        MaxFeePerGas = transaction.MaxFeePerGas;
        MaxPriorityFeePerGas = transaction.MaxPriorityFeePerGas;
        GasPrice = baseFee is not null
                ? transaction.CalculateEffectiveGasPrice(eip1559Enabled: true, baseFee.Value)
                : transaction.MaxFeePerGas;
    }

    public new class Converter : IToTransaction<RpcGenericTransaction>, IFromTransaction<RpcEIP1559Transaction>
    {
        private readonly RpcAccessListTransaction.Converter _baseConverter = new();

        public RpcEIP1559Transaction FromTransaction(Transaction tx, TransactionConverterExtraData extraData)
            => new(tx, txIndex: extraData.TxIndex, blockHash: extraData.BlockHash, blockNumber: extraData.BlockNumber, baseFee: extraData.BaseFee);

        public Transaction ToTransaction(RpcGenericTransaction rpcTx)
        {
            var tx = _baseConverter.ToTransaction(rpcTx);
            tx.GasPrice = rpcTx.MaxPriorityFeePerGas ?? 0;
            tx.DecodedMaxFeePerGas = rpcTx.MaxFeePerGas ?? 0;
            return tx;
        }

        public Transaction ToTransactionWithDefaults(RpcGenericTransaction t, ulong chainId)
        {
            throw new NotImplementedException();
        }
    }
}
