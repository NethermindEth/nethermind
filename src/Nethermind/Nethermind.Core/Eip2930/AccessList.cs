// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using Nethermind.Int256;

namespace Nethermind.Core.Eip2930;

public class AccessList : IEnumerable<(Address Address, IEnumerable<UInt256> StorageKeys)>
{
    private readonly List<object> _items;

    private AccessList(List<object> items)
    {
        _items = items;
    }

    public static AccessList Empty() => new(new List<object>());

    public class Builder
    {
        private readonly List<object> _items = new();
        private Address? _currentAddress;

        public Builder AddAddress(Address address)
        {
            _items.Add(address);
            _currentAddress = address;

            return this;
        }

        public Builder AddStorage(in UInt256 index)
        {
            if (_currentAddress is null)
            {
                throw new InvalidOperationException("No address known when adding index to the access list");
            }
            _items.Add(index);

            return this;
        }

        public AccessList Build()
        {
            return new AccessList(_items);
        }
    }

    public Enumerator GetEnumerator() => new(_items);
    IEnumerator<(Address Address, IEnumerable<UInt256> StorageKeys)> IEnumerable<(Address Address, IEnumerable<UInt256> StorageKeys)>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<(Address Address, IEnumerable<UInt256> StorageKeys)>
    {
        private readonly List<object> _items;
        private int _index = -1;

        public Enumerator(List<object> items)
        {
            _items = items;
        }

        public bool MoveNext()
        {
            while (++_index < _items.Count && _items[_index] is not Address) { }
            return _index < _items.Count;
        }

        public void Reset() => _index = -1;

        public (Address Address, IEnumerable<UInt256> StorageKeys) Current =>
            ((Address)_items[_index], new StorageKeysEnumerable(_items, _index));

        object IEnumerator.Current => Current;

        public void Dispose() { }
    }

    public readonly struct StorageKeysEnumerable : IEnumerable<UInt256>
    {
        private readonly List<object> _items;
        private readonly int _index;

        public StorageKeysEnumerable(List<object> items, int index)
        {
            _items = items;
            _index = index;
        }

        StorageKeysEnumerator GetEnumerator() => new(_items, _index);
        IEnumerator<UInt256> IEnumerable<UInt256>.GetEnumerator()  => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public struct StorageKeysEnumerator : IEnumerator<UInt256>
    {
        private readonly List<object> _items;
        private readonly int _startingIndex;
        private int _index;

        public StorageKeysEnumerator(List<object> items, int index)
        {
            _items = items;
            _startingIndex = _index = index;
        }

        public bool MoveNext() => ++_index < _items.Count && _items[_index] is UInt256;
        public void Reset() => _index = _startingIndex;
        public UInt256 Current => (UInt256)_items[_index];
        object IEnumerator.Current => Current;
        public void Dispose() { }
    }
}
