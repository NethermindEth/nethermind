// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Evm.Tracing.ParityStyle
{
    using Nethermind.JsonRpc.Modules.Trace;

    [JsonConverter(typeof(ParityVmTraceConverter))]
    public class ParityVmTrace
    {
        public byte[] Code { get; set; }
        public ParityVmOperationTrace[] Operations { get; set; }
    }
}

namespace Nethermind.JsonRpc.Modules.Trace
{
    using Nethermind.Evm.Tracing.ParityStyle;

    public class ParityVmTraceConverter : JsonConverter<ParityVmTrace>
    {
        public override void Write(Utf8JsonWriter writer, ParityVmTrace value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("code"u8);
            JsonSerializer.Serialize(writer, value.Code ?? Array.Empty<byte>(), options);
            writer.WritePropertyName("ops"u8);
            JsonSerializer.Serialize(writer, value.Operations, options);
            writer.WriteEndObject();
        }

        public override ParityVmTrace? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            throw new NotSupportedException();
        }
    }
}
