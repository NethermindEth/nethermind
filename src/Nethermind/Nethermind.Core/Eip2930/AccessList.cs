// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

using Nethermind.Int256;

namespace Nethermind.Core.Eip2930;

public class AccessList : IEnumerable<(Address Address, AccessList.StorageKeysEnumerable StorageKeys)>
{
    private readonly List<(Address address, int count)> _addresses;
    private readonly List<UInt256> _keys;

    private AccessList(List<(Address address, int count)> addresses, List<UInt256> keys)
    {
        _addresses = addresses;
        _keys = keys;
    }

    public static AccessList Empty { get; } = new(new List<(Address, int)>(), new List<UInt256>());

    public bool IsEmpty => _addresses.Count == 0;

    public class Builder
    {
        private readonly List<(Address address, int count)> _addresses = new();
        private readonly List<UInt256> _keys = new();

        private Address? _currentAddress;

        public Builder AddAddress(Address address)
        {
            _addresses.Add((address, 0));
            _currentAddress = address;

            return this;
        }

        public Builder AddStorage(in UInt256 index)
        {
            if (_currentAddress is null)
            {
                ThrowNoAddress();
            }

            CollectionsMarshal.AsSpan(_addresses)[^1].count++;
            _keys.Add(index);

            return this;

            [DoesNotReturn]
            [StackTraceHidden]
            static void ThrowNoAddress()
            {
                throw new InvalidOperationException("No address known when adding index to the access list");
            }
        }

        public AccessList Build()
        {
            return new AccessList(_addresses, _keys);
        }
    }

    public Enumerator GetEnumerator() => new(this);
    IEnumerator<(Address Address, StorageKeysEnumerable StorageKeys)> IEnumerable<(Address Address, StorageKeysEnumerable StorageKeys)>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<(Address Address, StorageKeysEnumerable StorageKeys)>, IEnumerator<(Address Address, IEnumerable<UInt256> StorageKeys)>
    {
        private readonly AccessList _accessList;
        private int _index = -1;
        private int _keysIndex = 0;

        public Enumerator(AccessList accessList)
        {
            _accessList = accessList;
        }

        public bool MoveNext()
        {
            _index++;
            if (_index > 0)
            {
                _keysIndex += CollectionsMarshal.AsSpan(_accessList._addresses)[_index - 1].count;
            }

            return _index < _accessList._addresses.Count;
        }

        public void Reset()
        {
            _index = -1;
            _keysIndex = 0;
        }

        public readonly (Address Address, StorageKeysEnumerable StorageKeys) Current
        {
            get
            {
                ref readonly var addressCount = ref CollectionsMarshal.AsSpan(_accessList._addresses)[_index];
                return (addressCount.address, new StorageKeysEnumerable(_accessList, _keysIndex, addressCount.count));
            }
        }

        readonly (Address Address, IEnumerable<UInt256> StorageKeys) IEnumerator<(Address Address, IEnumerable<UInt256> StorageKeys)>.Current => Current;

        readonly object IEnumerator.Current => Current;
        public readonly void Dispose() { }
    }

    public readonly struct StorageKeysEnumerable : IEnumerable<UInt256>
    {
        private readonly AccessList _accessList;
        private readonly int _index;
        private readonly int _count;

        public StorageKeysEnumerable(AccessList accessList, int index, int count)
        {
            _accessList = accessList;
            _index = index;
            _count = count;
        }

        StorageKeysEnumerator GetEnumerator() => new(_accessList, _index, _count);
        IEnumerator<UInt256> IEnumerable<UInt256>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public struct StorageKeysEnumerator : IEnumerator<UInt256>
    {
        private readonly AccessList _accessList;
        private readonly int _startingIndex;
        private readonly int _count;
        private int _index = -1;

        public StorageKeysEnumerator(AccessList accessList, int index, int count)
        {
            _accessList = accessList;
            _startingIndex = index;
            _count = count;
        }

        public bool MoveNext() => ++_index < _count;
        public void Reset() => _index = -1;
        public readonly UInt256 Current => _accessList._keys[_startingIndex + _index];

        readonly object IEnumerator.Current => Current;
        public readonly void Dispose() { }
    }
}
