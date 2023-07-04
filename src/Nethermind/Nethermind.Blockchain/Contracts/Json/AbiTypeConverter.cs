// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Abi;
using Newtonsoft.Json;

namespace Nethermind.Blockchain.Contracts.Json
{
    public class AbiTypeConverter : JsonConverter<AbiType>
    {
        public override void WriteJson(JsonWriter writer, AbiType value, JsonSerializer serializer)
        {
            writer.WriteValue(value.Name);
        }

        public override AbiType ReadJson(JsonReader reader, Type objectType, AbiType existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            throw new NotSupportedException();
        }

        public override bool CanRead { get; } = false;
    }
}
