// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json.Serialization;
using System.Text.Json;

using Nethermind.Core.Crypto;

namespace Nethermind.Serialization.Json;

public class PublicKeyConverter : JsonConverter<PublicKey>
{
    public override PublicKey? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        byte[]? bytes = ByteArrayConverter.Convert(ref reader);
        if (bytes is null)
        {
            return null;
        }
        if (bytes.Length < 64)
        {
            var newArray = new byte[64];
            bytes.AsSpan().CopyTo(newArray.AsSpan(64 - bytes.Length));
            bytes = newArray;
        }

        return new PublicKey(bytes);
    }

    public override void Write(
        Utf8JsonWriter writer,
        PublicKey publicKey,
        JsonSerializerOptions options)
    {
        ByteArrayConverter.Convert(writer, publicKey.Bytes);
    }
}
