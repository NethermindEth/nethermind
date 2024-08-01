// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.TxPool.Collections
{
    public partial class SortedPool<TKey, TValue, TGroupKey>
    {
#pragma warning disable 67
        public event EventHandler<SortedPoolEventArgs>? Inserted;
        public event EventHandler<SortedPoolRemovedEventArgs>? Removed;
#pragma warning restore 67

        public class SortedPoolEventArgs(TKey key, TValue value)
        {
            public TKey Key { get; } = key;
            public TValue Value { get; } = value;
        }

        public class SortedPoolRemovedEventArgs(TKey key, TValue value, bool evicted)
            : SortedPoolEventArgs(key, value)
        {
            public bool Evicted { get; } = evicted;
        }
    }
}
