// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Blockchain.Find;

namespace Nethermind.JsonRpc.Data;

/// <summary>
/// Reads a block number or tag as <see cref="BlockParameterConverter"/> does, but rejects its block-hash forms.
/// </summary>
/// <remarks>
/// Applied to a property, so an explicit null leaves it null instead of reading as latest.
/// </remarks>
internal sealed class BlockNumberOrTagConverter : JsonConverter<BlockParameter>
{
    public override BlockParameter Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // The registered converter, so the configured quantity strictness still applies.
        BlockParameter blockParameter = ((JsonConverter<BlockParameter>)options.GetConverter(typeof(BlockParameter))).Read(ref reader, typeToConvert, options)!;
        return blockParameter.Type == BlockParameterType.BlockHash
            ? throw new BlockParameterParseException("block hash is not a block number or tag")
            : blockParameter;
    }

    public override void Write(Utf8JsonWriter writer, BlockParameter value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, options);
}
