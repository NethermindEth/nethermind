// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Specs;
using Nethermind.Runner.Monitoring;
using Nethermind.Synchronization.Peers;
using Nethermind.TxPool;

namespace Nethermind.Runner;

public static class DataFeedExtensions
{
    private static DataFeed _dataFeed;

    public static void MapDataFeeds(
        this IEndpointRouteBuilder endpoints,
        ITxPool txPool,
        ISpecProvider specProvider,
        IReceiptFinder receiptFinder,
        IBlockTree blockTree,
        ISyncPeerPool syncPeerPool,
        IBlockchainProcessor blockchainProcessor)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        _dataFeed = new DataFeed(txPool, specProvider, receiptFinder, blockTree, syncPeerPool, blockchainProcessor);

        endpoints.MapGet("/data/events", _dataFeed.ProcessingFeed);
    }
}
