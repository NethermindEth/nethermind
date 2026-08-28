// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core;
using Nethermind.Int256;
using System.Text.Json.Serialization;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Eth;
using Nethermind.Core.Specs;
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

    public DepositTransactionForRpc(Transaction transaction, in TransactionForRpcContext extraData)
        : base(transaction, extraData)
    {
        OptimismTxReceipt? receipt = extraData.Receipt as OptimismTxReceipt;
        SourceHash = transaction.SourceHash ?? Hash256.Zero;
        From = transaction.SenderAddress ?? Address.SystemUser;
        To = transaction.To;
        Mint = transaction.Mint;
        Value = transaction.Value;
        Gas = transaction.GasLimit;
        IsSystemTx = transaction.IsOPSystemTransaction;
        Input = transaction.Data.ToArray();
        Nonce = receipt?.DepositNonce ?? 0;

        DepositReceiptVersion = receipt?.DepositReceiptVersion;
    }

    public override Result<Transaction> ToTransaction(bool validateUserInput = false, ulong? gasCap = null, IReleaseSpec? spec = null)
    {
        Result<Transaction> baseResult = base.ToTransaction(validateUserInput, gasCap, spec);
        if (baseResult.IsError) return baseResult;

        Transaction tx = baseResult.Data;
        tx.SourceHash = SourceHash ?? throw new ArgumentNullException(nameof(SourceHash));
        tx.SenderAddress = From ?? throw new ArgumentNullException(nameof(From));
        tx.To = To;
        tx.Mint = Mint ?? 0;
        tx.Value = Value ?? throw new ArgumentNullException(nameof(Value));
        tx.IsOPSystemTransaction = IsSystemTx ?? false;
        tx.Data = Input ?? throw new ArgumentNullException(nameof(Input));

        // Deposit txs require an explicit Gas; the original EnsureDefaults granted a graceful fallback to
        // gasCap when the request omitted it, which we preserve. gasCap is null/0 mean "no cap" (matching
        // LegacyTransactionForRpc), so neither substitutes for a missing Gas — that case still throws.
        ulong effectiveCap = gasCap is null or 0 ? ulong.MaxValue : gasCap.Value;
        ulong? gasOrDefault = Gas ?? (effectiveCap == ulong.MaxValue ? null : effectiveCap);
        tx.GasLimit = ulong.Min(gasOrDefault ?? throw new ArgumentNullException(nameof(Gas)), effectiveCap);

        return tx;
    }

    public override bool ShouldSetBaseFee() => false;

    public static DepositTransactionForRpc FromTransaction(Transaction tx, in TransactionForRpcContext extraData)
        => new(tx, extraData);
}
