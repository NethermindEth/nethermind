// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.DebugModule;

public class JavaScriptObjectConverter : JsonConverter
{
    [ThreadStatic]
    private static bool _disabled;

    public override bool CanRead => false;

    public override bool CanConvert(Type objectType) =>
        _disabled ? (_disabled = false) : typeof(IJavaScriptObject).IsAssignableFrom(objectType);

    public override void WriteJson(JsonWriter writer, object? o, JsonSerializer serializer)
    {
        if (o is IDictionary<string, object> dictionary)
        {
            if (dictionary.TryGetValue("value", out object? value))
            {
                // value is marshaled to BigInteger by ClearScript
                if (value is BigInteger bigInteger)
                {
                    writer.WriteValue(bigInteger.ToString());
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
            serializer.Serialize(writer, o);
        }
        finally
        {
            _disabled = false;
        }
    }

    public override object? ReadJson(
        JsonReader reader,
        Type objectType,
        object? o,
        JsonSerializer jsonSerializer) => throw new NotSupportedException();
}
