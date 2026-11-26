// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Serialization.Json;

namespace Nethermind.Specs.ChainSpecStyle.Json;

internal partial class ChainSpecJsonContext
{
    // The zkEVM guest serializes no JSON; registering would build the serializer at its startup.
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification = "Registers the metadata before any code in this assembly serializes.")]
    internal static void Register() => EthereumJsonSerializer.AddTypeInfoResolver(Default, JsonTypeInfoResolverPriority.External);
}
