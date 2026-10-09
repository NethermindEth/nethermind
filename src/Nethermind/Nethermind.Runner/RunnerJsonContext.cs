// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Nethermind.Runner.Ethereum;
using Nethermind.Runner.Monitoring;
using Nethermind.Serialization.Json;

namespace Nethermind.Runner;

/// <summary>Metadata for the monitoring feed's fork choice updates and the requests the startup warmup sends.</summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(DataFeed.ForkData))]
[JsonSerializable(typeof(StartupPipelineWarmer.WarmupRequest))]
[JsonSerializable(typeof(StartupPipelineWarmer.WarmupCall))]
internal partial class RunnerJsonContext : JsonSerializerContext
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification = "Registers the metadata before any code in this assembly serializes.")]
    internal static void Register() => EthereumJsonSerializer.AddTypeInfoResolver(Default, JsonTypeInfoResolverPriority.External);
}
