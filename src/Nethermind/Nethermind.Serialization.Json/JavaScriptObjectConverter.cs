// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
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
    public override bool CanConvert(Type objectType) => typeof(IJavaScriptObject).IsAssignableFrom(objectType);

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

            JsonSerializer.Serialize(writer, dictionary, options);
        }
        else if (o is IList<object> list)
        {
            JsonSerializer.Serialize(writer, list, options);
        }
        else if (o is IArrayBufferView buffer)
        {
            int size = (int)buffer.Size;
            if (size == 0)
            {
                JsonSerializer.Serialize(writer, Array.Empty<int>(), options);
                return;
            }

            byte[] array = ArrayPool<byte>.Shared.Rent(size);

            buffer.ReadBytes(buffer.Offset, buffer.Size, array, 0);
            ByteArrayConverter.Convert(writer, array.AsSpan(0, size), skipLeadingZeros: false);

            ArrayPool<byte>.Shared.Return(array);
        }
        else
        {
            throw new NotSupportedException(o.GetType().ToString());
        }
    }

    public override IJavaScriptObject? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        throw new NotSupportedException();
    }
}
