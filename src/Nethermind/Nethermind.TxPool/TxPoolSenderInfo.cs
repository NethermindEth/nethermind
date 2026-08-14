// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Collections.Immutable;
using Nethermind.Core;

namespace Nethermind.TxPool;

public sealed class TxPoolSenderInfo(
    IDictionary<ulong, Transaction> pending,
    IDictionary<ulong, Transaction> queued)
{
    public static readonly TxPoolSenderInfo Empty =
        new(ImmutableDictionary<ulong, Transaction>.Empty, ImmutableDictionary<ulong, Transaction>.Empty);

    public IDictionary<ulong, Transaction> Pending { get; } = pending;
    public IDictionary<ulong, Transaction> Queued { get; } = queued;
}
