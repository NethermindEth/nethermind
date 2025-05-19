// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core;
using Nethermind.Int256;
using System.Text.Json.Serialization;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Eth;
using System;

namespace Nethermind.Optimism.Rpc;

/// <remarks>
/// Defined in:
/// - https://github.com/ethereum-optimism/op-geth/blob/8af19cf20261c0b62f98cc27da3a268f542822ee/core/types/deposit_tx.go#L29-L46
/// - https://specs.optimism.io/protocol/deposits.html#the-deposited-transaction-type
/// </remarks>
public class DepositTransactionForRpc : TransactionForRpc, IFromTransaction<DepositTransactionForRpc>
{
    public static TxType TxType => TxType.DepositTx;

    public override TxType? Type => TxType;

    public Hash256? SourceHash { get; set; }

    public Address? From { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Address? To { get; set; }

    public UInt256? Mint { get; set; }

    public UInt256? Value { get; set; }

    public bool? IsSystemTx { get; set; }

    public byte[]? Input { get; set; }

    public UInt256? Nonce { get; set; }

    #region Nethermind specific fields
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UInt256? DepositReceiptVersion { get; set; }
    #endregion

    [JsonConstructor]
    public DepositTransactionForRpc() { }

    public DepositTransactionForRpc(Transaction transaction, int? txIndex = null, Hash256? blockHash = null, long? blockNumber = null, OptimismTxReceipt? receipt = null)
        : base(transaction, txIndex, blockHash, blockNumber)
    {
        SourceHash = transaction.SourceHash ?? Hash256.Zero;
        From = transaction.SenderAddress ?? Address.SystemUser;
        To = transaction.To;
        Mint = transaction.Mint;
        Value = transaction.Value;
        Gas = transaction.GasLimit;
        IsSystemTx = transaction.IsOPSystemTransaction;
        Input = transaction.Data?.ToArray() ?? [];
        Nonce = receipt?.DepositNonce ?? 0;

        DepositReceiptVersion = receipt?.DepositReceiptVersion;
    }

    public override Transaction ToTransaction()
    {
        var tx = base.ToTransaction();

        tx.SourceHash = SourceHash ?? throw new ArgumentNullException(nameof(SourceHash));
        tx.SenderAddress = From ?? throw new ArgumentNullException(nameof(From));
        tx.To = To;
        tx.Mint = Mint ?? 0;
        tx.Value = Value ?? throw new ArgumentNullException(nameof(Value));
        tx.GasLimit = Gas ?? throw new ArgumentNullException(nameof(Gas));
        tx.IsOPSystemTransaction = IsSystemTx ?? false;
        tx.Data = Input ?? throw new ArgumentNullException(nameof(Input));

        return tx;
    }

    public override void EnsureDefaults(long? gasCap)
    {
        if (Gas is not null && gasCap is not null)
        {
            Gas = Math.Min(Gas.Value, gasCap.Value);
        }

        Gas ??= gasCap;
    }

    public override bool ShouldSetBaseFee() => false;

    public static DepositTransactionForRpc FromTransaction(Transaction tx, TransactionConverterExtraData extraData)
        => new(tx, txIndex: extraData.TxIndex, blockHash: extraData.BlockHash, blockNumber: extraData.BlockNumber, receipt: extraData.Receipt as OptimismTxReceipt);
}
