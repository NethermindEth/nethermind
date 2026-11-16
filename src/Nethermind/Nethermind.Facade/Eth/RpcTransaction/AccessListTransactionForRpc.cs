// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Facade.Eth.RpcTransaction;

[GenerateJsonWriter(RegisterWithSerializer = false)]
[RepopulatableTransaction]
public class AccessListTransactionForRpc : LegacyTransactionForRpc, IFromTransaction<AccessListTransactionForRpc>
{
    public new static TxType TxType => TxType.AccessList;

    public override TxType? Type => TxType;

    [JsonDiscriminator]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public AccessListForRpc? AccessList { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public virtual UInt256? YParity { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public sealed override ulong? ChainId { get; set; }

    [JsonConstructor]
    public AccessListTransactionForRpc() { }

    public AccessListTransactionForRpc(Transaction transaction, in TransactionForRpcContext extraData)
        : base(transaction, extraData) { }

    internal override void Populate(Transaction transaction, in TransactionForRpcContext extraData)
    {
        base.Populate(transaction, extraData);
        AccessList = AccessListForRpc.FromAccessList(transaction.AccessList);
        YParity = transaction.Signature?.RecoveryId ?? 0;
        ChainId = transaction.ChainId ?? extraData.ChainId ?? BlockchainIds.Mainnet;
        V = YParity ?? 0;
    }

    public override Result<Transaction> ToTransaction(bool validateUserInput = false, ulong? gasCap = null, IReleaseSpec? spec = null)
    {
        Result<Transaction> baseResult = base.ToTransaction(validateUserInput, gasCap, spec);
        if (baseResult.IsError) return baseResult;

        Transaction tx = baseResult.Data;

        if (tx.SupportsAccessList)
        {
            tx.AccessList = AccessList?.ToAccessList() ?? Core.Eip2930.AccessList.Empty;
        }

        return tx;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A typed transaction signs with a y-parity, which its signature carries offset by 27, as a decoded one does: a
    /// <c>v</c> of 0 or 1 is that y-parity, and so is a <see cref="YParity"/> of 0 or 1 when <c>v</c> is left out. Any other
    /// <c>v</c> is taken as given. A request whose fields select no typed envelope, and so got this type by default, keeps
    /// the legacy reading.
    /// </remarks>
    internal override bool TryGetSignatureV(out ulong v)
    {
        if (!IsTypeDefaulted && (V ?? YParity) is { } yParity && yParity <= UInt256.One)
        {
            v = Signature.VOffset + yParity.ToUInt64(null);
            return true;
        }

        return base.TryGetSignatureV(out v);
    }

    public new static AccessListTransactionForRpc FromTransaction(Transaction tx, in TransactionForRpcContext extraData)
        => new(tx, extraData);
}
