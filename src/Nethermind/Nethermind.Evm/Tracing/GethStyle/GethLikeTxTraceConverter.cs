// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Serialization.Json;

namespace Nethermind.Evm.Tracing.GethStyle;

public class GethLikeTxTraceConverter : JsonConverter<GethLikeTxTrace>
{
    public override GethLikeTxTrace Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) => throw new NotSupportedException();

    public override void Write(
        Utf8JsonWriter writer,
        GethLikeTxTrace value,
        JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else if (value.CustomTracerResult is not null)
        {
            JsonSerializer.Serialize(writer, value.CustomTracerResult, options);
        }
        else
        {
            writer.WriteStartObject();

            NumberConversion? previousValue = ForcedNumberConversion.ForcedConversion.Value;
            ForcedNumberConversion.ForcedConversion.Value = NumberConversion.Raw;
            try
            {
                writer.WritePropertyName("gas"u8);
                JsonSerializer.Serialize(writer, value.Gas, options);
            }
            finally
            {
                ForcedNumberConversion.ForcedConversion.Value = previousValue;
            }

            writer.WritePropertyName("failed"u8);
            JsonSerializer.Serialize(writer, value.Failed, options);

            writer.WritePropertyName("returnValue"u8);
            ByteArrayConverter.Convert(writer, value.ReturnValue, skipLeadingZeros: false, addHexPrefix: false);

            writer.WritePropertyName("structLogs"u8);
            JsonSerializer.Serialize(writer, value.Entries, options);

            writer.WriteEndObject();
        }
    }
}
