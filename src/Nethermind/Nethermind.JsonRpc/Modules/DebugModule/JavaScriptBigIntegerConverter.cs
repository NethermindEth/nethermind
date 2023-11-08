// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.DebugModule;

public class JavaScriptBigIntegerConverter : JsonConverter
{
    [ThreadStatic]
    private static bool Disabled;

    public override bool CanRead => false;

    public override bool CanConvert(Type objectType) =>
        Disabled ? (Disabled = false) : typeof(IJavaScriptObject).IsAssignableFrom(objectType);

    public override void WriteJson(JsonWriter writer, object? o, JsonSerializer serializer)
    {
        // value is marshaled to BigInteger by ClearScript
        if (o is IDictionary<string, object> dictionary
            && dictionary.TryGetValue("value", out object? value)
            && value is BigInteger bigInteger)
        {
            writer.WriteValue(bigInteger.ToString());
        }
        else
        {
            Disabled = true;
            try
            {
                serializer.Serialize(writer, o);
            }
            finally
            {
                Disabled = false;
            }
        }
    }

    public override object? ReadJson(
        JsonReader reader,
        Type objectType,
        object? o,
        JsonSerializer jsonSerializer) => throw new NotSupportedException();
}
