// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Facade.Eth;

namespace Nethermind.Facade;

public readonly struct TransactionLookupResult(Transaction? transaction, TransactionForRpcContext extraData)
{
    public Transaction? Transaction { get; } = transaction;
    public TransactionForRpcContext ExtraData { get; } = extraData;
}
