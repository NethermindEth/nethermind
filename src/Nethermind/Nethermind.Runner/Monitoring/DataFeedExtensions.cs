// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Nethermind.Api;
using Nethermind.Runner.Monitoring;
using Nethermind.TxPool;

namespace Nethermind.Runner;

public static class DataFeedExtensions
{
    private static DataFeed _dataFeed;

    public static void MapDataFeeds(this IEndpointRouteBuilder endpoints, INethermindApi api)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(api);

        _dataFeed = new DataFeed(api);

        endpoints.MapGet("/data/events", _dataFeed.ProcessingFeed);
    }
}
