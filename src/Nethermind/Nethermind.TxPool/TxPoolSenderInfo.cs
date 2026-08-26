// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Collections.Immutable;
using Nethermind.Core;

namespace Nethermind.TxPool;

public sealed class TxPoolSenderInfo(
    IDictionary<string, Transaction> pending,
    IDictionary<string, Transaction> queued)
{
    public static readonly TxPoolSenderInfo Empty =
        new(ImmutableDictionary<string, Transaction>.Empty, ImmutableDictionary<string, Transaction>.Empty);

    public IDictionary<string, Transaction> Pending { get; } = pending;
    public IDictionary<string, Transaction> Queued { get; } = queued;
}
