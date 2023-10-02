// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Int256;

namespace Nethermind.Core.Eip2930;

public class AccessList
{
    private readonly List<object> _items;

    private AccessList(List<object> items)
    {
        _items = items;
    }

    public static AccessList Empty() => new(new List<object>());

    public IEnumerable<(Address Address, IEnumerable<UInt256> StorageKeys)> AsEnumerable()
    {
        IEnumerable<UInt256> GetStorageKeys(int i)
        {
            while (i < _items.Count && _items[i] is UInt256 storageKey)
            {
                yield return storageKey;
                i++;
            }
        }

        for (int i = 0; i < _items.Count; i++)
        {
            object item = _items[i];
            switch (item)
            {
                case Address address:
                    {
                        yield return (address, GetStorageKeys(i + 1));
                        break;
                    }
            }
        }
    }

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
}
