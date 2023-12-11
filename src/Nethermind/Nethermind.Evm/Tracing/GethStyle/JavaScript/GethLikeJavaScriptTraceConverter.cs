// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

using Nethermind.Serialization.Json;

namespace Nethermind.Evm.Tracing.GethStyle.JavaScript;

public class GethLikeJavaScriptTraceConverter : JsonConverter<GethLikeJavaScriptTrace>
{
    public override void Write(Utf8JsonWriter writer, GethLikeJavaScriptTrace value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        NumberConversion? previousValue = ForcedNumberConversion.ForcedConversion.Value;
        ForcedNumberConversion.ForcedConversion.Value = NumberConversion.Raw;
        try
        {
            JsonSerializer.Serialize(writer, value.Value, options);
        }
        finally
        {
            ForcedNumberConversion.ForcedConversion.Value = previousValue;
        }
    }

    public override GethLikeJavaScriptTrace? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        throw new NotSupportedException();
    }
}
