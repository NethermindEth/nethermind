// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Nethermind.Core.Buffers;

#nullable enable

namespace Nethermind.Serialization.Json;

public class JavaScriptObjectConverter : JsonConverter<IJavaScriptObject>
{
    // The serializer's own limit when JsonSerializerOptions.MaxDepth is 0.
    private const int DefaultMaxDepth = 64;

    public override bool CanConvert(Type objectType) => typeof(IJavaScriptObject).IsAssignableFrom(objectType);

    /// <summary>
    /// Writes a script value and everything it holds with one loop and an explicit stack of open objects and
    /// arrays, so the call stack stays flat however deep the value is.
    /// </summary>
    /// <remarks>
    /// Nesting is limited to the options' maximum depth, counted in open JSON objects and arrays as the serializer
    /// does; a cycle the walk follows reaches that limit too. Script scalars are written by their converters directly
    /// when the serializer would apply nothing around them; any other value is passed to the serializer, as before.
    /// </remarks>
    /// <exception cref="JavaScriptResultDepthException">The value nests deeper than the maximum depth.</exception>
    public override void Write(Utf8JsonWriter writer, IJavaScriptObject o, JsonSerializerOptions options)
    {
        int maxDepth = options.MaxDepth == 0 ? DefaultMaxDepth : options.MaxDepth;
        Stack<IDisposable>? open = null;
        try
        {
            object? next = o;
            do
            {
                if (next is not IJavaScriptObject script)
                {
                    WriteValue(writer, next, options, maxDepth, isProperty: open?.Peek() is IEnumerator<KeyValuePair<string, object>>);
                }
                else if (Open(writer, script, maxDepth) is { } enumerator)
                {
                    (open ??= new Stack<IDisposable>()).Push(enumerator);
                }
            } while (TryMoveNext(writer, open, out next));
        }
        finally
        {
            if (open is not null)
            {
                while (open.TryPop(out IDisposable? remaining))
                {
                    remaining.Dispose();
                }
            }
        }
    }

    /// <summary>Writes a script object, or opens it and returns the enumerator of what it holds.</summary>
    private static IDisposable? Open(Utf8JsonWriter writer, IJavaScriptObject o, int maxDepth)
    {
        if (o is IDictionary<string, object> dictionary)
        {
            // value is marshaled to BigInteger by ClearScript
            if (dictionary.TryGetValue("value", out object? value) && value is BigInteger bigInteger)
            {
                writer.WriteStringValue(bigInteger.ToString(CultureInfo.InvariantCulture));
                return null;
            }

            ThrowIfTooDeep(writer, maxDepth);
            writer.WriteStartObject();
            return dictionary.GetEnumerator();
        }

        if (o is IList<object> list)
        {
            ThrowIfTooDeep(writer, maxDepth);
            writer.WriteStartArray();
            return list.GetEnumerator();
        }

        if (o is IArrayBufferView buffer)
        {
            ThrowIfTooDeep(writer, maxDepth);
            int size = (int)buffer.Size;
            using ArrayPoolDisposableReturn handle = ArrayPoolDisposableReturn.Rent(size, out byte[] array);
            buffer.ReadBytes(0, buffer.Size, array, 0);
            writer.WriteStartObject();
            Span<byte> propertyName = stackalloc byte[10];
            for (int i = 0; i < size; i++)
            {
                Utf8Formatter.TryFormat(i, propertyName, out int written);
                writer.WriteNumber(propertyName[..written], array[i]);
            }
            writer.WriteEndObject();
            return null;
        }

        throw new NotSupportedException(o.GetType().ToString());
    }

    /// <summary>Moves to the next value held by the innermost open object or array, closing those that are done.</summary>
    private static bool TryMoveNext(Utf8JsonWriter writer, Stack<IDisposable>? open, out object? next)
    {
        while (open is not null && open.TryPeek(out IDisposable? top))
        {
            if (top is IEnumerator<KeyValuePair<string, object>> properties)
            {
                while (properties.MoveNext())
                {
                    (string key, next) = properties.Current;
                    // JSON.stringify omits properties whose value is undefined
                    if (next == Undefined.Value)
                    {
                        continue;
                    }

                    // Keys are the script's own data, so they skip options.DictionaryKeyPolicy and stay verbatim, as in JSON.stringify.
                    writer.WritePropertyName(key);
                    return true;
                }

                writer.WriteEndObject();
            }
            else
            {
                IEnumerator<object> items = (IEnumerator<object>)top;
                if (items.MoveNext())
                {
                    next = items.Current;
                    return true;
                }

                writer.WriteEndArray();
            }

            open.Pop().Dispose();
        }

        next = null;
        return false;
    }

    /// <summary>Writes a value that is not a script object, as an array item or as the value of a property.</summary>
    /// <remarks>
    /// A script scalar or null goes to its converter directly when the serializer would apply nothing around that
    /// converter, and is then accepted inside the deepest allowed object or array. Any other value goes through the
    /// serializer, which starts at the current depth and rejects a value at the limit; the check here only makes that
    /// rejection the same error as for a script object.
    /// </remarks>
    private static void WriteValue(Utf8JsonWriter writer, object? value, JsonSerializerOptions options, int maxDepth, bool isProperty)
    {
        // The serializer applies any other number handling around its own number converters.
        if (options.NumberHandling == JsonNumberHandling.Strict && TryWriteScalar(writer, value, options, isProperty))
        {
            return;
        }

        ThrowIfTooDeep(writer, maxDepth);
        TypeInfoJsonSerializer.Serialize(writer, value, options);
    }

    private static bool TryWriteScalar(Utf8JsonWriter writer, object? value, JsonSerializerOptions options, bool isProperty)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return true;
            case string text:
                return TryWriteWithOwnConverter(writer, text, options, isProperty);
            case int number:
                return TryWriteWithOwnConverter(writer, number, options, isProperty);
            case double number:
                return TryWriteWithOwnConverter(writer, number, options, isProperty);
            case bool flag:
                return TryWriteWithOwnConverter(writer, flag, options, isProperty);
            case long number:
                return TryWriteWithOwnConverter(writer, number, options, isProperty);
            case float number:
                return TryWriteWithOwnConverter(writer, number, options, isProperty);
            case BigInteger number:
                return TryWriteWithOwnConverter(writer, number, options, isProperty);
            default:
                return false;
        }
    }

    /// <remarks>
    /// The serializer adapts a converter declared for a base type of <typeparamref name="T"/>, so only a converter for
    /// <typeparamref name="T"/> itself is called here. A property value used to be written by a serializer call of its
    /// own, which applies number handling set on the value's type; an array item was written as part of its array,
    /// which applies the list contract's number handling, falling back to the options'. This keeps that distinction
    /// under strict options when the list contract sets no number handling of its own.
    /// </remarks>
    private static bool TryWriteWithOwnConverter<T>(Utf8JsonWriter writer, T value, JsonSerializerOptions options, bool isProperty)
    {
        JsonTypeInfo<T> typeInfo = TypeInfoJsonSerializer.GetTypeInfo<T>(options);
        if (typeInfo.Converter is not JsonConverter<T> converter
            || (isProperty && typeInfo.NumberHandling is not (null or JsonNumberHandling.Strict)))
        {
            return false;
        }

        converter.Write(writer, value, options);
        return true;
    }

    private static void ThrowIfTooDeep(Utf8JsonWriter writer, int maxDepth)
    {
        if (writer.CurrentDepth >= maxDepth)
        {
            throw new JavaScriptResultDepthException(maxDepth);
        }
    }

    public override IJavaScriptObject? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();
}
