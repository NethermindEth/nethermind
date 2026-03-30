// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;

namespace Nethermind.StateComposition;

/// <summary>
/// API discovery response for statecomp_getModuleInfo.
/// </summary>
public class ModuleInfo
{
    public required string Version { get; init; }
    public required string Description { get; init; }
    public required ImmutableArray<EndpointInfo> Endpoints { get; init; }
}

/// <summary>
/// Describes a single RPC endpoint.
/// </summary>
public readonly record struct EndpointInfo
{
    public required string Name { get; init; }
    public required string Description { get; init; }
}
