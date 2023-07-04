// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using JsonSerializer = Newtonsoft.Json.JsonSerializer;

namespace Nethermind.JsonRpc.Modules.Trace
{
    public class ParityTraceAddressConverter : JsonConverter<int[]>
    {
        public override void WriteJson(JsonWriter writer, int[] value, JsonSerializer serializer)
        {
            if (value is null)
            {
                writer.WriteNull();
            }
            else
            {
                writer.WriteStartArray();
                foreach (int i in value)
                {
                    writer.WriteValue(i);
                }

                writer.WriteEndArray();
            }
        }

        public override int[] ReadJson(JsonReader reader, Type objectType, int[] existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            List<int> result = new();
            int? pathPart;

            do
            {
                pathPart = reader.ReadAsInt32();
                if (pathPart.HasValue)
                {
                    result.Add(pathPart.Value);
                }
            } while (pathPart is not null);

            return result.ToArray();
        }
    }
}
