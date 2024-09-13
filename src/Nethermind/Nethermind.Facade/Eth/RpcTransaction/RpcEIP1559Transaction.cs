// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
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
    public new UInt256 GasPrice { get; set; }

    [JsonConstructor]
    public RpcEIP1559Transaction() { }

    public RpcEIP1559Transaction(Transaction transaction) : base(transaction)
    {
        MaxPriorityFeePerGas = transaction.MaxPriorityFeePerGas;
        MaxFeePerGas = transaction.MaxFeePerGas;
        GasPrice = transaction.GasPrice;
    }

    public new static readonly ITransactionConverter<RpcEIP1559Transaction> Converter = new ConverterImpl();

    private class ConverterImpl : ITransactionConverter<RpcEIP1559Transaction>
    {
        public RpcEIP1559Transaction FromTransaction(Transaction transaction) => new(transaction);
    }
}
