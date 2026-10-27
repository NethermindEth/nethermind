// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;

namespace Nethermind.Eez.Rpc;

/// <summary>Output-only native-v1 transaction fields and fixed Ethereum RPC placeholders.</summary>
public sealed class EezNativeSystemTransactionForRpc : EezSystemTransactionForRpc, IFromTransaction<EezNativeSystemTransactionForRpc>
{
    private static readonly AuthorizationListForRpc EmptyAuthorizations = new();

    public new static TxType TxType => EezConstants.NativeSystemTxType;
    public override TxType? Type => TxType;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public ulong Version => EezConstants.NativeSystemTxVersion;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Hash256? SourceHash { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public UInt256 YParity => UInt256.Zero;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public AccessListForRpc AccessList => AccessListForRpc.FromAccessList(null);

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Hash256[] BlobVersionedHashes => [];

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public AuthorizationListForRpc AuthorizationList => EmptyAuthorizations;

    [JsonConstructor]
    public EezNativeSystemTransactionForRpc() { }

    public EezNativeSystemTransactionForRpc(Transaction transaction, in TransactionForRpcContext extraData)
        : base(transaction, in extraData) => SourceHash = transaction.SourceHash;

    public new static EezNativeSystemTransactionForRpc FromTransaction(Transaction tx, in TransactionForRpcContext extraData) =>
        new(tx, in extraData);
}
