// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth;

public sealed class StorageValuesRequest : IJsonRpcParam
{
    public const int MaxSlots = 1024;

    public Dictionary<Address, UInt256[]> Entries { get; } = new();
    public bool TooManySlots { get; private set; }
    public int TotalSlots { get; private set; }

    public void ReadJson(JsonElement jsonValue, JsonSerializerOptions options)
    {
        if (jsonValue.ValueKind != JsonValueKind.Object)
            return;

        foreach (JsonProperty property in jsonValue.EnumerateObject())
        {
            Address address = new(Bytes.FromHexString(property.Name, Address.Size));

            if (property.Value.ValueKind != JsonValueKind.Array)
                continue;

            int arrayLength = property.Value.GetArrayLength();
            if (arrayLength > MaxSlots - TotalSlots)
            {
                TooManySlots = true;
                return;
            }

            UInt256[] slots = new UInt256[arrayLength];
            int index = 0;
            foreach (JsonElement slotElement in property.Value.EnumerateArray())
            {
                slots[index++] = slotElement.Deserialize<UInt256>(options)!;
            }

            TotalSlots += arrayLength;
            Entries[address] = slots;
        }
    }
}
