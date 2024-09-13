// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Facade.Eth.RpcTransaction;

public interface IRpcTransactionConverter
{
    IRpcTransaction FromTransaction(Transaction tx);
}

public class ComposeTransactionConverter : IRpcTransactionConverter
{
    private readonly IRpcTransactionConverter?[] _converters = new IRpcTransactionConverter?[Transaction.MaxTxType + 1];

    public ComposeTransactionConverter RegisterConverter(TxType txType, IRpcTransactionConverter converter)
    {
        _converters[(byte)txType] = converter;
        return this;
    }

    public IRpcTransaction FromTransaction(Transaction tx)
    {
        var converter = _converters[(byte)tx.Type] ?? throw new ArgumentException("No converter for transaction type");
        return converter.FromTransaction(tx);
    }
}
