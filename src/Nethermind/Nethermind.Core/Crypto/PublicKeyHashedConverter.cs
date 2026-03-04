// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json.Serialization;
using System.Text.Json;
using Nethermind.Core.Crypto;

namespace Nethermind.Serialization.Json;

public class PublicKeyHashedConverter : JsonConverter<PublicKey>
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

        if (bytes.Length >= 64)
        {
            return new PublicKey(bytes);
        }
        else
        {
            Span<byte> span = stackalloc byte[64];
            bytes.AsSpan().CopyTo(span.Slice(64 - bytes.Length));
            return new PublicKey(span);
        }
    }

    public override void Write(Utf8JsonWriter writer, PublicKey publicKey, JsonSerializerOptions options)
    {
        writer.WriteStringValue(publicKey.Hash.ToString(false));
    }
}
