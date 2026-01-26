// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Facade.Eth;

namespace Nethermind.Facade;

public readonly struct TransactionLookupResult
{
    public TransactionLookupResult(Transaction? transaction, TransactionConverterExtraData extraData)
    {
        Transaction = transaction;
        ExtraData = extraData;
    }

    public Transaction? Transaction { get; }
    public TransactionConverterExtraData ExtraData { get; }
}
