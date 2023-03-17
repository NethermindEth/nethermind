// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.IdentityModel.Tokens.Jwt;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

using Nethermind.Core.Extensions;

namespace Nethermind.Evm.Tracing.ParityStyle
{
    //        {
    //            "cost": 0.0,
    //            "ex": {
    //                "mem": null,
    //                "push": [],
    //                "store": null,
    //                "used": 16961.0
    //            },
    //            "pc": 526.0,
    //            "sub": null
    //        }
    [JsonConverter(typeof(ParityVmOperationTraceConverter))]
    public class ParityVmOperationTrace
    {
        public long Cost { get; set; }
        public ParityMemoryChangeTrace Memory { get; set; }
        public byte[][] Push { get; set; }
        public ParityStorageChangeTrace Store { get; set; }
        public long Used { get; set; }
        public int Pc { get; set; }
        public ParityVmTrace Sub { get; set; }
    }

    public class ParityVmOperationTraceConverter : JsonConverter<ParityVmOperationTrace>
    {
        public override ParityVmOperationTrace Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) => throw new NotImplementedException();

        public override void Write(
            Utf8JsonWriter writer,
            ParityVmOperationTrace value,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();

            writer.WriteNumber("cost"u8, value.Cost);
            writer.WritePropertyName("ex"u8);
            writer.WriteStartObject();
            writer.WritePropertyName("mem"u8);
            if (value.Memory is not null)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("data"u8);
                JsonSerializer.Serialize(writer, value.Memory.Data, options);
                writer.WritePropertyName("off"u8);
                JsonSerializer.Serialize(writer, value.Memory.Offset, options);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNullValue();
            }

            writer.WritePropertyName("push"u8);
            if (value.Push is not null)
            {
                writer.WriteStartArray();
                for (int i = 0; i < value.Push.Length; i++)
                {
                    JsonSerializer.Serialize(writer, value.Push[i], options);
                }

                writer.WriteEndArray();
            }
            else
            {
                writer.WriteNullValue();
            }

            writer.WritePropertyName("store"u8);
            if (value.Store is not null)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("key"u8);
                JsonSerializer.Serialize(writer, value.Store.Key, options);
                writer.WritePropertyName("val"u8);
                JsonSerializer.Serialize(writer, value.Store.Value, options);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNullValue();
            }

            writer.WriteNumber("used"u8, value.Used);
            writer.WriteEndObject();

            writer.WriteNumber("pc"u8, value.Pc);
            writer.WritePropertyName("sub"u8);
            JsonSerializer.Serialize(writer, value.Sub, options);

            writer.WriteEndObject();
        }
    }
}
