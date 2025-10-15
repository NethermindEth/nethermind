// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
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

        public Item(Address address, IEnumerable<UInt256>? storageKeys)
        {
            Address = address;
            StorageKeys = storageKeys;
        }
    }

    public static AccessListForRpc FromAccessList(AccessList? accessList) =>
        accessList is null
        ? new AccessListForRpc([])
        : new AccessListForRpc(accessList.Select(static item => new Item(item.Address, [.. item.StorageKeys])));

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
            if (reader.TokenType != JsonTokenType.StartArray)
                throw new JsonException("Expected start of array");

            const int maxItems = 1000;
            const int maxStorageKeysPerItem = 1000;
            const int maxStorageKeys = 10000;

            var items = new List<Item>();
            int itemCount = 0;
            int storageItemsCount = 0;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                    break;

                if (itemCount >= maxItems)
                    throw new JsonException($"Access list cannot have more than {maxItems} items.");

                if (reader.TokenType != JsonTokenType.StartObject)
                    throw new JsonException("Expected start of item object");

                Address? address = null;
                List<UInt256>? storageKeys = null;

                // Read Item properties
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject)
                        break;

                    if (reader.TokenType != JsonTokenType.PropertyName)
                        throw new JsonException("Expected property name");

                    string propName = reader.GetString()!;
                    reader.Read(); // move to property value

                    if (string.Equals(propName, nameof(Item.Address), StringComparison.OrdinalIgnoreCase))
                    {
                        address = JsonSerializer.Deserialize<Address>(ref reader, options);
                    }
                    else if (string.Equals(propName, nameof(Item.StorageKeys), StringComparison.OrdinalIgnoreCase))
                    {
                        if (reader.TokenType == JsonTokenType.Null)
                        {
                            storageKeys = null;
                        }
                        else if (reader.TokenType == JsonTokenType.StartArray)
                        {
                            storageKeys = new List<UInt256>();
                            int keyCount = 0;

                            while (reader.Read())
                            {
                                if (reader.TokenType == JsonTokenType.EndArray)
                                    break;

                                if (keyCount >= maxStorageKeysPerItem)
                                    throw new JsonException($"An item cannot have more than {maxStorageKeysPerItem} storage keys.");

                                if (storageItemsCount >= maxStorageKeys)
                                    throw new JsonException($"Access List cannot have more than {maxStorageKeys} storage keys.");

                                UInt256 key = JsonSerializer.Deserialize<UInt256>(ref reader, options);
                                storageKeys.Add(key);
                                keyCount++;
                                storageItemsCount++;
                            }
                        }
                        else
                        {
                            throw new JsonException("Expected array or null for StorageKeys");
                        }
                    }
                    else
                    {
                        // Skip unknown properties
                        reader.Skip();
                    }
                }

                if (address is null)
                    throw new JsonException("Item missing required Address");

                items.Add(new Item(address, storageKeys));
                itemCount++;
            }

            return new AccessListForRpc(items);
        }


        public override void Write(Utf8JsonWriter writer, AccessListForRpc value, JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value._items, options);
        }
    }
}
