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

    public IReadOnlyDictionary<Address, IReadOnlySet<UInt256>> AsDictionary()
    {
        Dictionary<Address, IReadOnlySet<UInt256>> result = new();

        Address? currentAddress = null;
        HashSet<UInt256> currentStorageKeys = new();

        foreach (AccessListItem item in _items)
        {
            switch (item)
            {
                case AccessListItem.Address address:
                    {
                        if (currentAddress is not null)
                        {
                            if (result.TryGetValue(currentAddress, out IReadOnlySet<UInt256>? existingStorageKeys))
                            {
                                ((HashSet<UInt256>)existingStorageKeys).UnionWith(currentStorageKeys);
                            }
                            else
                            {
                                result[currentAddress] = currentStorageKeys;
                            }
                        }
                        currentAddress = address.Value;
                        currentStorageKeys = new HashSet<UInt256>();
                        break;
                    }
                case AccessListItem.StorageKey storageKey:
                    {
                        currentStorageKeys.Add(storageKey.Value);
                        break;
                    }
            }
        }
        if (currentAddress is not null)
        {
            if (result.TryGetValue(currentAddress, out IReadOnlySet<UInt256>? existingStorageKeys))
            {
                ((HashSet<UInt256>)existingStorageKeys).UnionWith(currentStorageKeys);
            }
            else
            {
                result[currentAddress] = currentStorageKeys;
            }
        }

        return result;
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
