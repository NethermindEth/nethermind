//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;

namespace Nethermind.TxPool.Collections
{
    public partial class SortedPool<TKey, TValue, TGroupKey>
    {
        public event EventHandler<SortedPoolEventArgs>? Inserted;
        public event EventHandler<SortedPoolRemovedEventArgs>? Removed;

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
