// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Core.Collections
{
    public class SortedRealList<TKey, TValue> : SortedList<TKey, TValue>, IList<KeyValuePair<TKey, TValue>> where TKey : notnull
    {
        // Constructs a new sorted list. The sorted list is initially empty and has
        // a capacity of zero. Upon adding the first element to the sorted list the
        // capacity is increased to DefaultCapacity, and then increased in multiples of two as
        // required. The elements of the sorted list are ordered according to the
        // IComparable interface, which must be implemented by the keys of
        // all entries added to the sorted list.
        public SortedRealList() { }

        // Constructs a new sorted list. The sorted list is initially empty and has
        // a capacity of zero. Upon adding the first element to the sorted list the
        // capacity is increased to 16, and then increased in multiples of two as
        // required. The elements of the sorted list are ordered according to the
        // IComparable interface, which must be implemented by the keys of
        // all entries added to the sorted list.
        //
        public SortedRealList(int capacity) : base(capacity) { }

        // Constructs a new sorted list with a given IComparer
        // implementation. The sorted list is initially empty and has a capacity of
        // zero. Upon adding the first element to the sorted list the capacity is
        // increased to 16, and then increased in multiples of two as required. The
        // elements of the sorted list are ordered according to the given
        // IComparer implementation. If comparer is null, the
        // elements are compared to each other using the IComparable
        // interface, which in that case must be implemented by the keys of all
        // entries added to the sorted list.
        //
        public SortedRealList(IComparer<TKey>? comparer) : base(comparer)
        {
        }

        // Constructs a new sorted dictionary with a given IComparer
        // implementation and a given initial capacity. The sorted list is
        // initially empty, but will have room for the given number of elements
        // before any reallocations are required. The elements of the sorted list
        // are ordered according to the given IComparer implementation. If
        // comparer is null, the elements are compared to each other using
        // the IComparable interface, which in that case must be implemented
        // by the keys of all entries added to the sorted list.
        //
        public SortedRealList(int capacity, IComparer<TKey>? comparer) : base(capacity, comparer) { }

        // Constructs a new sorted list containing a copy of the entries in the
        // given dictionary. The elements of the sorted list are ordered according
        // to the IComparable interface, which must be implemented by the
        // keys of all entries in the given dictionary as well as keys
        // subsequently added to the sorted list.
        //
        public SortedRealList(IDictionary<TKey, TValue> dictionary) : base(dictionary) { }

        // Constructs a new sorted list containing a copy of the entries in the
        // given dictionary. The elements of the sorted list are ordered according
        // to the given IComparer implementation. If comparer is
        // null, the elements are compared to each other using the
        // IComparable interface, which in that case must be implemented
        // by the keys of all entries in the given dictionary as well as keys
        // subsequently added to the sorted list.
        //
        public SortedRealList(IDictionary<TKey, TValue> dictionary, IComparer<TKey>? comparer) : base(dictionary, comparer) { }

        public int IndexOf(KeyValuePair<TKey, TValue> item) => IndexOfKey(item.Key);

        public void Insert(int index, KeyValuePair<TKey, TValue> item) => this.TryAdd(item.Key, item.Value);

        public KeyValuePair<TKey, TValue> this[int index]
        {
            get => new(Keys[index], Values[index]);
            set => this.TryAdd(value.Key, value.Value);
        }
    }
}
