// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;

#nullable enable

namespace Nethermind.Serialization.Json;

public class JavaScriptObjectConverter : JsonConverter<IJavaScriptObject>
{
    [ThreadStatic]
    private static bool _disabled;

    public override bool CanConvert(Type objectType) =>
        _disabled ? (_disabled = false) : typeof(IJavaScriptObject).IsAssignableFrom(objectType);

    public override void Write(Utf8JsonWriter writer, IJavaScriptObject o, JsonSerializerOptions options)
    {
        if (o is IDictionary<string, object> dictionary)
        {
            if (dictionary.TryGetValue("value", out object? value))
            {
                // value is marshaled to BigInteger by ClearScript
                if (value is BigInteger bigInteger)
                {
                    writer.WriteStringValue(bigInteger.ToString(CultureInfo.InvariantCulture));
                    return;
                }

                if (value == Undefined.Value)
                {
                    dictionary.Remove("value");
                }
            }

            // remove undefined errors
            if (dictionary.TryGetValue("error", out object? error) && error == Undefined.Value)
            {
                dictionary.Remove("error");
            }
        }

        // fallback to standard serialization
        _disabled = true;
        try
        {
            JsonSerializer.Serialize(writer, o, options);
        }
        finally
        {
            _disabled = false;
        }
    }

    public override IJavaScriptObject? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        throw new NotSupportedException();
    }
}
