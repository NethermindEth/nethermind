// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Core;

namespace Nethermind.Serialization.Rlp;

public sealed partial class TxDecoder
{
    private static partial ObjectPool<Transaction> CreateTxObjectPool(Transaction.PoolPolicy policy, int maxRetained) =>
        new SingleThreadedPool(policy, maxRetained);

    /// <summary>
    /// The guest's <see cref="DefaultObjectPool{T}"/>: it runs on one thread, so a plain stack replaces the concurrent queue.
    /// </summary>
    private sealed class SingleThreadedPool(Transaction.PoolPolicy policy, int maxRetained) : ObjectPool<Transaction>
    {
        private readonly Stack<Transaction> _items = new();

        public override Transaction Get() => _items.TryPop(out Transaction? item) ? item : policy.Create();

        public override void Return(Transaction obj)
        {
            if (policy.Return(obj) && _items.Count < maxRetained) _items.Push(obj);
        }
    }
}
