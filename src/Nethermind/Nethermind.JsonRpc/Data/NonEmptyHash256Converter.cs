// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;

namespace Nethermind.JsonRpc.Data;

/// <summary>
/// Reads a hash as the registered converter does, but rejects the empty string, which that converter reads as null.
/// </summary>
/// <remarks>
/// Applied to a property, so an explicit null still leaves it null.
/// </remarks>
public sealed class NonEmptyHash256Converter : JsonConverter<Hash256>
{
    public override Hash256 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ((JsonConverter<Hash256>)options.GetConverter(typeof(Hash256))).Read(ref reader, typeToConvert, options)
        ?? throw new SafePublicMessageFormatException("empty hash");

    public override void Write(Utf8JsonWriter writer, Hash256 value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, options);
}
