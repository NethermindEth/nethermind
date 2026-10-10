// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Nethermind.Specs.ChainSpecStyle.Json;

/// <summary>Metadata for the chain specification and genesis files the node loads at startup.</summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ChainSpecJson))]
[JsonSerializable(typeof(GethGenesisJson))]
[JsonSerializable(typeof(ulong))]
internal partial class ChainSpecJsonContext : JsonSerializerContext
{
}
