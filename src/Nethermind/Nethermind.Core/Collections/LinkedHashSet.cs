// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Nethermind.Core.Collections
{
    public class LinkedHashSet<T> : ISet<T>, IReadOnlySet<T> where T : notnull
    {
        private readonly IDictionary<T, LinkedListNode<T>> _dict;
        private readonly LinkedList<T> _list;

        public LinkedHashSet(int initialCapacity)
        {
            _dict = new Dictionary<T, LinkedListNode<T>>(initialCapacity);
            _list = new LinkedList<T>();
        }

        public LinkedHashSet()
        {
            _dict = new Dictionary<T, LinkedListNode<T>>();
            _list = new LinkedList<T>();
        }

        public LinkedHashSet(IEnumerable<T> enumerable) : this()
        {
            UnionWith(enumerable);
        }

        public LinkedHashSet(int initialCapacity, IEnumerable<T> enumerable) : this(initialCapacity)
        {
            UnionWith(enumerable);
        }

        public LinkedHashSet(IEqualityComparer<T> equalityComparer)
        {
            _dict = new Dictionary<T, LinkedListNode<T>>(equalityComparer);
            _list = new LinkedList<T>();
        }

        #region ISet

        public bool Add(T item)
        {
            if (!_dict.ContainsKey(item))
            {
                LinkedListNode<T> node = _list.AddLast(item);
                _dict[item] = node;
                return true;
            }

            return false;
        }

        public void ExceptWith(IEnumerable<T>? other)
        {
            if (other is null) throw new ArgumentNullException(nameof(other));

            foreach (T t in other)
            {
                Remove(t);
            }
        }

        public void IntersectWith(IEnumerable<T>? other)
        {
            if (other is null) throw new ArgumentNullException(nameof(other));

            T[] ts = new T[Count];
            CopyTo(ts, 0);
            ISet<T> set = other.ToHashSet();
            foreach (T t in this.ToArray())
            {
                if (!set.Contains(t))
                {
                    Remove(t);
                }
            }
        }

        public bool IsProperSubsetOf(IEnumerable<T>? other)
        {
            if (other is null) throw new ArgumentNullException(nameof(other));

            int contains = 0;
            int noContains = 0;
            foreach (T t in other)
            {
                if (Contains(t))
                {
                    contains++;
                }
                else
                {
                    noContains++;
                }
            }

            return contains == Count && noContains > 0;
        }

        public bool IsProperSupersetOf(IEnumerable<T>? other)
        {
            if (other is null) throw new ArgumentNullException(nameof(other));

            ISet<T> set = other.ToHashSet();
            int otherCount = set.Count;
            if (Count > otherCount)
            {
                int contains = 0;
                int noContains = 0;
                foreach (T t in this)
                {
                    if (set.Contains(t))
                    {
                        contains++;
                    }
                    else
                    {
                        noContains++;
                    }
                }

                return contains == otherCount && noContains > 0;
            }

            return false;
        }

        public bool IsSubsetOf(IEnumerable<T>? other)
        {
            if (other is null) throw new ArgumentNullException(nameof(other));

            ISet<T> set = other.ToHashSet();
            return this.All(t => set.Contains(t));
        }

        public bool IsSupersetOf(IEnumerable<T>? other)
        {
            if (other is null) throw new ArgumentNullException(nameof(other));

            return other.All(Contains);
        }

        public bool Overlaps(IEnumerable<T>? other)
        {
            if (other is null) throw new ArgumentNullException(nameof(other));

            return other.Any(Contains);
        }

        public bool SetEquals(IEnumerable<T>? other)
        {
            if (other is null) throw new ArgumentNullException(nameof(other));

            ISet<T> set = other.ToHashSet();
            return Count == set.Count && IsSupersetOf(set);
        }

        public void SymmetricExceptWith(IEnumerable<T> other)
        {
            if (other is null) throw new ArgumentNullException(nameof(other));

            T[] ts = new T[Count];
            CopyTo(ts, 0);

            ISet<T> set = other.ToHashSet();
            for (int index = 0; index < ts.Length; index++)
            {
                T t = ts[index];
                if (set.Contains(t))
                {
                    Remove(t);
                    set.Remove(t);
                }
            }

            foreach (T t in set)
            {
                Add(t);
            }
        }

        public void UnionWith(IEnumerable<T>? other)
        {
            if (other is null) throw new ArgumentNullException(nameof(other));

            foreach (T t in other)
            {
                Add(t);
            }
        }

        #endregion


        #region ICollection<T>

        public int Count => _dict.Count;

        public bool IsReadOnly => _dict.IsReadOnly;

        void ICollection<T>.Add(T item)
        {
            Add(item);
        }

        public void Clear()
        {
            _dict.Clear();
            _list.Clear();
        }

        public bool Contains(T item) => _dict.ContainsKey(item);

        public void CopyTo(T[] array, int arrayIndex)
        {
            _list.CopyTo(array, arrayIndex);
        }

        public bool Remove(T item)
        {
            if (_dict.TryGetValue(item, out LinkedListNode<T>? node))
            {
                _dict.Remove(item);
                _list.Remove(node);
                return true;
            }

            return false;
        }

        #endregion

        #region IEnumerable<T>, IEnumerable

        public IEnumerator<T> GetEnumerator() => _list.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => _list.GetEnumerator();

        #endregion
    }
}
