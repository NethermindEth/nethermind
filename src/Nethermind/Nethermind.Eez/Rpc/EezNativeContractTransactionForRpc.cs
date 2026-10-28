// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Eez.Execution;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;

namespace Nethermind.Eez.Rpc;

public sealed class EezNativeContractTransactionForRpc : EezNativeSystemTransactionForRpc, IFromTransaction<EezNativeContractTransactionForRpc>
{
    public new static TxType TxType => EezConstants.NativeSystemTxType;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public override ulong? Version => null;

    [JsonConstructor]
    public EezNativeContractTransactionForRpc() { }

    public EezNativeContractTransactionForRpc(Transaction transaction, in TransactionForRpcContext context) : base(transaction, in context) { }

    public new static EezNativeContractTransactionForRpc FromTransaction(Transaction transaction, in TransactionForRpcContext context) =>
        new(transaction, in context);
}
