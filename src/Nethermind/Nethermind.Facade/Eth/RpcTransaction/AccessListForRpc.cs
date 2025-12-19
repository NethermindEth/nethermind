// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using System.Text.Json.Serialization;
using System.Text.Json;
using System;

namespace Nethermind.Facade.Eth.RpcTransaction;

[JsonConverter(typeof(JsonConverter))]
public class AccessListForRpc
{
    private readonly IEnumerable<Item> _items;

    [JsonConstructor]
    public AccessListForRpc() { }

    private AccessListForRpc(IEnumerable<Item> items)
    {
        _items = items;
    }

    private class Item
    {
        public Address Address { get; set; }

        [JsonConverter(typeof(StorageCellIndexConverter))]
        public IEnumerable<UInt256>? StorageKeys { get; set; }

        [JsonConstructor]
        public Item() { }

        public Item(Address address, IEnumerable<UInt256> storageKeys)
        {
            Address = address;
            StorageKeys = storageKeys;
        }
    }

    public static AccessListForRpc FromAccessList(AccessList? accessList)
    {
        if (accessList is null)
        {
            return new AccessListForRpc([]);
        }

        // Avoid LINQ: some custom runtimes have issues with iterator/enumerator boxing/unboxing for value-type enumerators.
        List<Item> items = new();
        foreach (var item in accessList)
        {
            // Materialize storage keys without LINQ/spread, to keep iteration simple and predictable.
            List<UInt256> keys = new();
            foreach (UInt256 key in item.StorageKeys)
            {
                keys.Add(key);
            }

            items.Add(new Item(item.Address, keys));
        }

        return new AccessListForRpc(items);
    }

    public AccessList ToAccessList()
    {
        AccessList.Builder builder = new();
        foreach (Item item in _items)
        {
            builder.AddAddress(item.Address);
            foreach (UInt256 index in item.StorageKeys ?? [])
            {
                builder.AddStorage(index);
            }
        }

        return builder.Build();
    }

    public class JsonConverter : JsonConverter<AccessListForRpc>
    {
        public override AccessListForRpc? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            List<Item>? list = JsonSerializer.Deserialize<List<Item>>(ref reader, options);
            return list is null ? null : new AccessListForRpc(list);
        }

        public override void Write(Utf8JsonWriter writer, AccessListForRpc value, JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value._items, options);
        }
    }
}
