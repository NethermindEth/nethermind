// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Int256;

namespace Nethermind.Core.Eip2930;

public abstract record AccessListItem
{
    public record Address(Core.Address Value) : AccessListItem;

    public record StorageKey(UInt256 Value) : AccessListItem;
}

public class AccessList
{
    private readonly List<AccessListItem> _items;

    private AccessList(List<AccessListItem> items)
    {
        _items = items;
    }

    public static AccessList Empty() => new AccessList(new List<AccessListItem>());

    public IReadOnlyCollection<AccessListItem> Raw => _items;

    public IEnumerable<(Address Address, IEnumerable<UInt256> StorageKeys)> AsEnumerable()
    {
        IEnumerable<UInt256> GetStorageKeys(int i)
        {
            while (i < _items.Count && _items[i] is AccessListItem.StorageKey storageKey)
            {
                yield return storageKey.Value;
                i++;
            }
        }

        for (int i = 0; i < _items.Count; i++)
        {
            AccessListItem item = _items[i];
            switch (item)
            {
                case AccessListItem.Address address:
                {
                    yield return (address.Value, GetStorageKeys(i + 1));
                    break;
                }
            }
        }
    }

    public class Builder
    {
        private readonly List<AccessListItem> _items = new();
        private Address? _currentAddress;

        public Builder AddAddress(Address address)
        {
            _items.Add(new AccessListItem.Address(address));
            _currentAddress = address;

            return this;
        }

        public Builder AddStorage(in UInt256 index)
        {
            if (_currentAddress is null)
            {
                throw new InvalidOperationException("No address known when adding index to the access list");
            }
            _items.Add(new AccessListItem.StorageKey(index));

            return this;
        }

        public AccessList Build()
        {
            return new AccessList(_items);
        }
    }
}
