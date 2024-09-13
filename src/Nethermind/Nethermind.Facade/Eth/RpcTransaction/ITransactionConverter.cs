// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Facade.Eth.RpcTransaction;

public interface ITransactionConverter<out T>
{
    T FromTransaction(Transaction tx, TransactionConverterExtraData extraData);
    T FromTransaction(Transaction tx) => FromTransaction(tx, new TransactionConverterExtraData());
}

public readonly struct TransactionConverterExtraData
{
    public Hash256? BlockHash { get; }
    public long? BlockNumber { get; }
    public int? TxIndex { get; }
    public UInt256? BaseFee { get; }
    public TxReceipt Receipt { get; }
}
