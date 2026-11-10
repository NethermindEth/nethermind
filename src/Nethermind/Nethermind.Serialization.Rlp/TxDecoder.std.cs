// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.ObjectPool;
using Nethermind.Core;

namespace Nethermind.Serialization.Rlp;

public sealed partial class TxDecoder
{
    private static partial ObjectPool<Transaction> CreateTxObjectPool(Transaction.PoolPolicy policy, int maxRetained) =>
        new DefaultObjectPool<Transaction>(policy, maxRetained);
}
