// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;

namespace Nethermind.Runner.JsonRpc;

public sealed class LocalPortMetadata(IReadOnlySet<int> ports)
{
    public IReadOnlySet<int> Ports { get; } = ports ?? throw new ArgumentNullException(nameof(ports));
}

/// <summary>
/// Matches endpoints carrying LocalPortMetadata against the connection's local port.
/// </summary>
public sealed class LocalPortMatcherPolicy : MatcherPolicy, IEndpointSelectorPolicy
{
    public override int Order => 0;

    public bool AppliesToEndpoints(IReadOnlyList<Endpoint> endpoints)
    {
        for (int i = 0; i < endpoints.Count; i++)
        {
            if (endpoints[i].Metadata.GetMetadata<LocalPortMetadata>() is not null) return true;
        }

        return false;
    }

    public Task ApplyAsync(HttpContext httpContext, CandidateSet candidates)
    {
        int localPort = httpContext.Connection.LocalPort;
        for (int i = 0; i < candidates.Count; i++)
        {
            if (!candidates.IsValidCandidate(i)) continue;

            LocalPortMetadata? metadata = candidates[i].Endpoint.Metadata.GetMetadata<LocalPortMetadata>();
            if (metadata is not null && !metadata.Ports.Contains(localPort))
            {
                candidates.SetValidity(i, false);
            }
        }

        return Task.CompletedTask;
    }
}

public static class LocalPortEndpointConventionBuilderExtensions
{
    public static TBuilder RequireLocalPort<TBuilder>(this TBuilder builder, IReadOnlySet<int> ports)
        where TBuilder : IEndpointConventionBuilder
    {
        LocalPortMetadata metadata = new(ports);
        builder.Add(endpointBuilder => endpointBuilder.Metadata.Add(metadata));
        return builder;
    }
}
