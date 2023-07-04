// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Evm.Tracing.ParityStyle;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.Trace
{
    public class ParityTraceResultConverter : JsonConverter<ParityTraceResult>
    {
        public override void WriteJson(JsonWriter writer, ParityTraceResult value, JsonSerializer serializer)
        {
            writer.WriteStartObject();

            if (value.Address is not null)
            {
                writer.WriteProperty("address", value.Address, serializer);
                writer.WriteProperty("code", value.Code, serializer);
            }

            writer.WriteProperty("gasUsed", string.Concat("0x", value.GasUsed.ToString("x")));

            if (value.Address is null)
            {
                writer.WriteProperty("output", value.Output, serializer);
            }

            writer.WriteEndObject();
        }

        public override ParityTraceResult ReadJson(JsonReader reader, Type objectType, ParityTraceResult existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            throw new NotSupportedException();
        }
    }
}
