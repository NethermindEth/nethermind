// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Tracing.ParityStyle;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.Trace
{
    public class ParityVmOperationTraceConverter : JsonConverter<ParityVmOperationTrace>
    {
        //{
        //  "cost": 0.0,
        //            "ex": {
        //                "mem": null,
        //                "push": [],
        //                "store": null,
        //                "used": 16961.0
        //            },
        //            "pc": 526.0,
        //            "sub": null
        //        }
        public override void WriteJson(JsonWriter writer, ParityVmOperationTrace value, JsonSerializer serializer)
        {
            writer.WriteStartObject();

            writer.WriteProperty("cost", value.Cost);
            writer.WritePropertyName("ex");
            writer.WriteStartObject();
            writer.WritePropertyName("mem");
            if (value.Memory is not null)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("data");
                writer.WriteValue(value.Memory.Data.ToHexString(true, false));
                writer.WritePropertyName("off");
                writer.WriteValue(value.Memory.Offset);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull();
            }

            writer.WritePropertyName("push");
            if (value.Push is not null)
            {
                writer.WriteStartArray();
                for (int i = 0; i < value.Push.Length; i++)
                {
                    writer.WriteValue(value.Push[i].ToHexString(true, true));
                }

                writer.WriteEndArray();
            }
            else
            {
                writer.WriteNull();
            }

            writer.WritePropertyName("store");
            if (value.Store is not null)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("key");
                writer.WriteValue(value.Store.Key.ToHexString(true, true));
                writer.WritePropertyName("val");
                writer.WriteValue(value.Store.Value.ToHexString(true, true));
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull();
            }

            writer.WriteProperty("used", value.Used);
            writer.WriteEndObject();

            writer.WriteProperty("pc", value.Pc, serializer);
            writer.WriteProperty("sub", value.Sub, serializer);
            writer.WriteEndObject();
        }

        public override ParityVmOperationTrace ReadJson(JsonReader reader, Type objectType, ParityVmOperationTrace existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            throw new NotSupportedException();
        }
    }
}
