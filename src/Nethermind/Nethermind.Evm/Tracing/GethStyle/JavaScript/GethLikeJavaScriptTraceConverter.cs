// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;

namespace Nethermind.Evm.Tracing.GethStyle.JavaScript;

public class GethLikeJavaScriptTraceConverter : JsonConverter<GethLikeJavaScriptTrace>
{
    public override bool CanRead => false;

    public override void WriteJson(JsonWriter writer, GethLikeJavaScriptTrace? value, JsonSerializer serializer)
    {
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        NumberConversion? previousValue = ForcedNumberConversion.ForcedConversion.Value;
        ForcedNumberConversion.ForcedConversion.Value = NumberConversion.Raw;
        try
        {
            serializer.Serialize(writer, value.Value);
        }
        finally
        {
            ForcedNumberConversion.ForcedConversion.Value = previousValue;
        }
    }

    public override GethLikeJavaScriptTrace? ReadJson(JsonReader reader, Type objectType, GethLikeJavaScriptTrace? existingValue, bool hasExistingValue, JsonSerializer serializer) =>
        throw new NotSupportedException();
}
