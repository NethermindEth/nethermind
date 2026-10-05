// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Nethermind.OpcodeTracing.Plugin.Output;

/// <summary>Metadata for the opcode trace files the writers produce.</summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(TraceOutput))]
[JsonSerializable(typeof(PerBlockTraceOutput))]
[JsonSerializable(typeof(CumulativeTraceOutput))]
internal partial class TraceOutputJsonContext : JsonSerializerContext;
