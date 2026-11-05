// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Serialization.Json;

namespace Nethermind.Xdc.RPC;

/// <summary>
/// Reads an XDPoS epoch number given either as a plain JSON number or as a hex quantity string.
/// </summary>
/// <remarks>
/// The reference client types these arguments as a Go <c>uint64</c>, which only accepts a plain JSON number, so its
/// callers send that form. Hex strings are still read by <see cref="ULongConverter"/> under the configured
/// <see cref="EthereumJsonSerializer.StrictHexFormat"/>.
/// </remarks>
internal sealed class XdcEpochNumberConverter : JsonConverter<ulong>
{
    private static readonly ULongConverter StrictConverter = new(strictQuantity: true);
    private static readonly ULongConverter LenientConverter = new();

    public override ulong Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Number
            ? reader.GetUInt64()
            : (EthereumJsonSerializer.StrictHexFormat ? StrictConverter : LenientConverter).Read(ref reader, typeToConvert, options);

    public override void Write(Utf8JsonWriter writer, ulong value, JsonSerializerOptions options) =>
        LenientConverter.Write(writer, value, options);
}
