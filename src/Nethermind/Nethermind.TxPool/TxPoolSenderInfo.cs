// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Collections.Immutable;
using Nethermind.Core;

namespace Nethermind.TxPool;

public sealed class TxPoolSenderInfo(
    IDictionary<TxPoolTxKey, Transaction> pending,
    IDictionary<TxPoolTxKey, Transaction> queued)
{
    public static readonly TxPoolSenderInfo Empty =
        new(ImmutableDictionary<TxPoolTxKey, Transaction>.Empty, ImmutableDictionary<TxPoolTxKey, Transaction>.Empty);

    public IDictionary<TxPoolTxKey, Transaction> Pending { get; } = pending;
    public IDictionary<TxPoolTxKey, Transaction> Queued { get; } = queued;
}
