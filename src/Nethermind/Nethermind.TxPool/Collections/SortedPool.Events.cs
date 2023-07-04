// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
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

        public class SortedPoolEventArgs
        {
            public TKey Key { get; }
            public TValue Value { get; }
            public TGroupKey Group { get; }

            public SortedPoolEventArgs(TKey key, TValue value, TGroupKey group)
            {
                Key = key;
                Value = value;
                Group = group;
            }
        }

        public class SortedPoolRemovedEventArgs : SortedPoolEventArgs
        {
            public bool Evicted { get; }

            public SortedPoolRemovedEventArgs(TKey key, TValue value, TGroupKey group, bool evicted) : base(key, value, group)
            {
                Evicted = evicted;
            }
        }
    }
}
