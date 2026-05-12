// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Core.Crypto;

public class SignatureConverter : JsonConverter<Signature>
{
    public override Signature? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetString() is { } hex ? new Signature(hex) : null;

    public override void Write(Utf8JsonWriter writer, Signature value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
