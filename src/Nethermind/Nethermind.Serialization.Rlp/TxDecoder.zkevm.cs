// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.ObjectPool;
using Nethermind.Core;

namespace Nethermind.Serialization.Rlp;

// Lazy, so the guest runs no class constructor for this type.
public sealed partial class TxDecoder
{
    public static ObjectPool<Transaction> TxObjectPool => field ??= new DefaultObjectPool<Transaction>(new Transaction.PoolPolicy(), MaxRetainedTransactions);

    public static TxDecoder Instance => field ??= new TxDecoder(static () => TxObjectPool.Get());
}
