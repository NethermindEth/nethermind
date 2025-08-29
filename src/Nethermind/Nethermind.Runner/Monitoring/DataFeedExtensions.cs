// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Runner.Monitoring;
using Nethermind.Synchronization.Peers;
using Nethermind.TxPool;

namespace Nethermind.Runner;

public static class DataFeedExtensions
{
    private static DataFeed _dataFeed;

    public static void MapDataFeeds(this IEndpointRouteBuilder endpoints, ApplicationLifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        IServiceProvider services = endpoints.ServiceProvider;

        ITxPool txPool = services.GetRequiredService<ITxPool>();
        ISpecProvider specProvider = services.GetRequiredService<ISpecProvider>();
        IReceiptFinder receiptFinder = services.GetRequiredService<IReceiptFinder>();
        IBlockTree blockTree = services.GetRequiredService<IBlockTree>();
        ISyncPeerPool syncPeerPool = services.GetRequiredService<ISyncPeerPool>();
        IMainProcessingContext mainProcessingContext = services.GetRequiredService<IMainProcessingContext>();
        ILogManager? logManager = services.GetService<ILogManager>() ?? NullLogManager.Instance;

        _dataFeed = new DataFeed(txPool, specProvider, receiptFinder, blockTree, syncPeerPool, mainProcessingContext, logManager, lifetime.ApplicationStopped);

        endpoints.MapGet("/data/events", _dataFeed.ProcessingFeedAsync);
    }
}
