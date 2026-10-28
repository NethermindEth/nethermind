// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Int256;
using Nethermind.Facade.Eth;

namespace Nethermind.Eez.Rpc;

public sealed class EezNativeHistoryTransactionForRpc : EezSystemTransactionForRpc, IFromTransaction<EezNativeHistoryTransactionForRpc>
{
    public new static TxType TxType => (TxType)NativeHistoryEnvelope.Type;
    public override TxType? Type => TxType;
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public UInt256 YParity => UInt256.Zero;

    public ulong Version => NativeHistoryEnvelope.Version;
    public ValueHash256[] Hashes { get; set; } = [];

    [JsonConstructor]
    public EezNativeHistoryTransactionForRpc() { }

    public EezNativeHistoryTransactionForRpc(Transaction transaction, in TransactionForRpcContext context) : base(transaction, in context) =>
        Hashes = ((NativeHistoryTransaction)transaction).Envelope.Hashes.ToArray();

    public new static EezNativeHistoryTransactionForRpc FromTransaction(Transaction transaction, in TransactionForRpcContext context) =>
        new(transaction, in context);
}
