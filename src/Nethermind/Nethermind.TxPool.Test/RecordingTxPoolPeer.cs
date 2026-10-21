// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.TxPool.Test;

/// <summary>Records what a broadcaster sends, since a span argument cannot be matched by a substitute.</summary>
/// <remarks>A single local transaction reaches <see cref="SendNewTransactions"/> as a full batch of one, through the interface default.</remarks>
internal sealed class RecordingTxPoolPeer(PublicKey id, ulong headNumber = 0) : ITxPoolPeer
{
    private readonly ConcurrentQueue<(Transaction[] Txs, bool SendFullTx)> _batches = [];

    public PublicKey Id => id;
    public ulong HeadNumber { get; set; } = headNumber;
    public IReadOnlyCollection<(Transaction[] Txs, bool SendFullTx)> Batches => _batches;
    public IEnumerable<Transaction> Sent => _batches.SelectMany(static batch => batch.Txs);

    public int BatchCount(bool sendFullTx) => _batches.Count(batch => batch.SendFullTx == sendFullTx);

    // The broadcaster's 1 s timer notifies every peer also when it has nothing to announce; an empty batch sends
    // nothing, so recording it would only make the counts depend on how long a test runs.
    public void SendNewTransactions(ReadOnlySpan<Transaction> txs, bool sendFullTx)
    {
        if (!txs.IsEmpty) _batches.Enqueue((txs.ToArray(), sendFullTx));
    }
}
