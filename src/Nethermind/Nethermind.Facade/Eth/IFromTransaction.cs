// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;

namespace Nethermind.Facade.Eth;

public interface ITxTyped
{
    static abstract TxType TxType { get; }
}

public interface IFromTransactionSource<out T> : ITxTyped where T : TransactionForRpc
{
    static abstract IFromTransaction<T> Converter { get; }
}

public interface IFromTransaction<out T> where T : TransactionForRpc
{
    T FromTransaction(Transaction tx, TransactionConverterExtraData extraData);
    T FromTransaction(Transaction tx) => FromTransaction(tx, new TransactionConverterExtraData());
}

public readonly struct TransactionConverterExtraData
{
    public Hash256? BlockHash { get; init; }
    public long? BlockNumber { get; init; }
    public int? TxIndex { get; init; }
    public UInt256? BaseFee { get; init; }
    public TxReceipt Receipt { get; init; }
}
