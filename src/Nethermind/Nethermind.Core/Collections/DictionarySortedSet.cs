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

using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Nethermind.Core.Collections
{
    public class DictionarySortedSet<TKey, TValue> : SortedSet<KeyValuePair<TKey, TValue>>
    {
        public DictionarySortedSet() : base(GetComparer()) { }

        public DictionarySortedSet(IComparer<TKey> comparer) : base(GetComparer(comparer)) { }


        public DictionarySortedSet(IEnumerable<KeyValuePair<TKey, TValue>> collection) : base(collection, GetComparer()) { }

        public DictionarySortedSet(IEnumerable<KeyValuePair<TKey, TValue>> collection, IComparer<TKey> comparer) : base(collection, GetComparer(comparer)) { }

        protected DictionarySortedSet(SerializationInfo info, StreamingContext context) : base(info, context) { }
        
        private static IComparer<KeyValuePair<TKey, TValue>> GetComparer(IComparer<TKey>? comparer = null) => 
            new KeyValuePairKeyOnlyComparer(comparer ?? Comparer<TKey>.Default);
        
        public bool Add(TKey key, TValue value) => Add(new KeyValuePair<TKey, TValue>(key, value));
        
#pragma warning disable 8604
        // fixed C# 9
        public bool Remove(TKey key) => Remove(new KeyValuePair<TKey, TValue>(key, default));
#pragma warning restore 8604

        public bool TryGetValue(TKey key, out TValue value)
        {
#pragma warning disable 8604
            // fixed C# 9
            if (TryGetValue(new KeyValuePair<TKey, TValue>(key, default), out var found))
#pragma warning restore 8604
            {
                value = found.Value;
                return true;
            }

#pragma warning disable 8601
            // fixed C# 9
            value = default;
#pragma warning restore 8601
            return false;
        }
        
#pragma warning disable 8604
        // fixed C# 9
        public bool ContainsKey(TKey key) => Contains(new KeyValuePair<TKey, TValue>(key, default));
#pragma warning restore 8604

        private sealed class KeyValuePairKeyOnlyComparer : Comparer<KeyValuePair<TKey, TValue>>
        {
            private readonly IComparer<TKey> _keyComparer;

            public KeyValuePairKeyOnlyComparer(IComparer<TKey>? keyComparer = null)
            {
                _keyComparer = keyComparer ?? Comparer<TKey>.Default;
            }

            public override int Compare(KeyValuePair<TKey, TValue> x, KeyValuePair<TKey, TValue> y) 
                => _keyComparer.Compare(x.Key, y.Key);
        }
        
    }
}
