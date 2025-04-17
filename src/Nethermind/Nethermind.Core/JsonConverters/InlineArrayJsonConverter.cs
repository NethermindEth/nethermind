// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Json;

public class InlineArrayJsonConverter<T> : JsonConverter<T> where T : IInlineArrayConvertable<T>
{
    public override T Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var bytes = ByteArrayConverter.Convert(ref reader);
        return bytes is null ? throw new ArgumentNullException() : T.ToType(bytes);
    }

    public override void Write(
        Utf8JsonWriter writer,
        T item,
        JsonSerializerOptions options)
    {
        ByteArrayConverter.Convert(writer, T.ToBytes(item), skipLeadingZeros: false);
    }

    [SkipLocalsInit]
    public override void WriteAsPropertyName(Utf8JsonWriter writer,
        T item,
        JsonSerializerOptions options)
    {
        Span<byte> addressBytes = stackalloc byte[Address.Size * 2 + 2];
        addressBytes[0] = (byte)'0';
        addressBytes[1] = (byte)'x';
        Span<byte> hex = addressBytes[2..];
        T.ToBytes(item).AsSpan().OutputBytesToByteHex(hex, false);
        writer.WritePropertyName(addressBytes);
    }
}

public interface IInlineArrayConvertable<T>
{
    public static abstract byte[] ToBytes(T item);
    public static abstract T ToType(byte[] bytes);
}
