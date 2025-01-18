// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Core.Specs;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.JsonRpc.Modules.Subscribe;
public static class SubscriptionFactoryExtensions
{
    public static void RegisterNewHeadSubscription(
        this ISubscriptionFactory subscriptionFactory,
        IBlockTree? blockTree,
        ILogManager? logManager,
        ISpecProvider specProvider
        )
    {
        subscriptionFactory.RegisterSubscriptionType<TransactionsOption?>(
            SubscriptionType.NewHeads,
            (jsonRpcDuplexClient, args) =>
            new NewHeadSubscription(jsonRpcDuplexClient, blockTree, logManager, specProvider, args)
            );
    }

    public static void RegisterLogsSubscription(
        this ISubscriptionFactory subscriptionFactory,
        IReceiptMonitor receiptMonitor,
        IFilterStore? filterStore,
        IBlockTree? blockTree,
        ILogManager? logManager
        )
    {
        subscriptionFactory.RegisterSubscriptionType<Filter?>(
            SubscriptionType.Logs,
            (jsonRpcDuplexClient, filter) =>
            new LogsSubscription(jsonRpcDuplexClient, receiptMonitor, filterStore, blockTree, logManager, filter)
            );
    }

    public static void RegisterNewPendingTransactionsSubscription(
        this ISubscriptionFactory subscriptionFactory,
        ITxPool? txPool,
        ISpecProvider? specProvider,
        ILogManager? logManager
        )
    {
        subscriptionFactory.RegisterSubscriptionType<TransactionsOption?>(
            SubscriptionType.NewPendingTransactions,
            (jsonRpcDuplexClient, args) =>
            new NewPendingTransactionsSubscription(jsonRpcDuplexClient, txPool, specProvider, logManager, args)
            );
    }

    public static void RegisterDroppedPendingTransactionsSubscription(
        this ISubscriptionFactory subscriptionFactory,
        ITxPool? txPool,
        ILogManager? logManager
        )
    {
        subscriptionFactory.RegisterSubscriptionType(
            SubscriptionType.DroppedPendingTransactions,
            (jsonRpcDuplexClient) =>
            new DroppedPendingTransactionsSubscription(jsonRpcDuplexClient, txPool, logManager)
            );
    }

    public static void RegisterSyncingSubscription(
        this ISubscriptionFactory subscriptionFactory,
        IBlockTree? blockTree,
        IEthSyncingInfo ethSyncingInfo,
        ILogManager? logManager
        )
    {
        subscriptionFactory.RegisterSubscriptionType(
            SubscriptionType.Syncing,
            (jsonRpcDuplexClient) =>
            new SyncingSubscription(jsonRpcDuplexClient, blockTree, ethSyncingInfo, logManager)
            );
    }

    public static void RegisterStandardSubscription(
        this ISubscriptionFactory subscriptionFactory,
        IBlockTree? blockTree,
        ILogManager? logManager,
        ISpecProvider specProvider,
        IReceiptMonitor receiptMonitor,
        IFilterStore? filterStore,
        ITxPool? txPool,
        IEthSyncingInfo ethSyncingInfo
        )
    {
        subscriptionFactory.RegisterNewHeadSubscription(blockTree, logManager, specProvider);
        subscriptionFactory.RegisterLogsSubscription(receiptMonitor, filterStore, blockTree, logManager);
        subscriptionFactory.RegisterNewPendingTransactionsSubscription(txPool, specProvider, logManager);
        subscriptionFactory.RegisterDroppedPendingTransactionsSubscription(txPool, logManager);
        subscriptionFactory.RegisterSyncingSubscription(blockTree, ethSyncingInfo, logManager);
    }
}
