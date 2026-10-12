// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.ObjectPool;
using Nethermind.Core;

namespace Nethermind.Serialization.Rlp;

public sealed partial class TxDecoder
{
    public static readonly ObjectPool<Transaction> TxObjectPool;

    public static readonly TxDecoder Instance;

    static TxDecoder()
    {
        // Retain reusable gossip and owned block-body transactions across receive/processing threads. This caps lazy retention, not preallocation;
        // PoolPolicy clears payload references before retaining a transaction.
        TxObjectPool = new DefaultObjectPool<Transaction>(new Transaction.PoolPolicy(), MaxRetainedTransactions);
        Instance = new TxDecoder(static () => TxObjectPool.Get());
    }
}
