// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.TxPool;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.Subscribe;

/// <summary>
/// Creates different types of subscriptions.
/// </summary>
/// <remarks>
/// Uses a dictionary which holds the constructors to the different subscription types, using the name
/// of the respective RPC request as key-strings.
/// When SubscriptionFactory is constructed, the basic subscription types are automatically loaded.
/// Plugins may import additional subscription types by calling <see cref="RegisterSubscriptionType"/>.
/// </remarks>
public class SubscriptionFactory : ISubscriptionFactory
{
    private readonly JsonSerializer _jsonSerializer;
    private readonly ConcurrentDictionary<string, CustomSubscriptionType> _subscriptionConstructors;

    public SubscriptionFactory(ILogManager? logManager,
        IBlockTree? blockTree,
        ITxPool? txPool,
        IReceiptMonitor receiptCanonicalityMonitor,
        IFilterStore? filterStore,
        IEthSyncingInfo ethSyncingInfo,
        ISpecProvider specProvider,
        JsonSerializer jsonSerializer)
    {
        _jsonSerializer = jsonSerializer;
        logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
        receiptCanonicalityMonitor = receiptCanonicalityMonitor ?? throw new ArgumentNullException(nameof(receiptCanonicalityMonitor));
        filterStore = filterStore ?? throw new ArgumentNullException(nameof(filterStore));
        ethSyncingInfo = ethSyncingInfo ?? throw new ArgumentNullException(nameof(ethSyncingInfo));
        specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));

        _subscriptionConstructors = new ConcurrentDictionary<string, CustomSubscriptionType>
        {

            //Register the standard subscription types in the dictionary.
            [SubscriptionType.NewHeads] = CreateSubscriptionType<TransactionsOption?>((jsonRpcDuplexClient, args) =>
                new NewHeadSubscription(jsonRpcDuplexClient, blockTree, logManager, specProvider, args)),

            [SubscriptionType.Logs] = CreateSubscriptionType<Filter?>((jsonRpcDuplexClient, filter) =>
                new LogsSubscription(jsonRpcDuplexClient, receiptCanonicalityMonitor, filterStore, blockTree, logManager, filter)),

            [SubscriptionType.NewPendingTransactions] = CreateSubscriptionType<TransactionsOption?>((jsonRpcDuplexClient, args) =>
                new NewPendingTransactionsSubscription(jsonRpcDuplexClient, txPool, logManager, args)),

            [SubscriptionType.DroppedPendingTransactions] = CreateSubscriptionType(jsonRpcDuplexClient =>
                new DroppedPendingTransactionsSubscription(jsonRpcDuplexClient, txPool, logManager)),

            [SubscriptionType.Syncing] = CreateSubscriptionType(jsonRpcDuplexClient =>
                new SyncingSubscription(jsonRpcDuplexClient, blockTree, ethSyncingInfo, logManager))
        };
    }

    public Subscription CreateSubscription(IJsonRpcDuplexClient jsonRpcDuplexClient, string subscriptionType, string? args = null)
    {
        if (_subscriptionConstructors.TryGetValue(subscriptionType, out CustomSubscriptionType customSubscription))
        {
            Type? paramType = customSubscription.ParamType;

            IJsonRpcParam? param = null;
            bool thereIsParameter = paramType is not null;
            bool thereAreArgs = args is not null;
            if (thereIsParameter && (thereAreArgs || paramType.CannotBeAssignedNull()))
            {
                param = (IJsonRpcParam)Activator.CreateInstance(paramType);
                if (thereAreArgs)
                {
                    param!.ReadJson(_jsonSerializer, args);
                }
            }

            return customSubscription.Constructor(jsonRpcDuplexClient, param);
        }

        throw new KeyNotFoundException($"{subscriptionType} is an invalid or unregistered subscription type");
    }

    public void RegisterSubscriptionType<T>(string subscriptionType, Func<IJsonRpcDuplexClient, T, Subscription> customSubscriptionDelegate)
        where T : IJsonRpcParam?, new() =>
        _subscriptionConstructors[subscriptionType] = CreateSubscriptionType(customSubscriptionDelegate);


    private static CustomSubscriptionType CreateSubscriptionType<T>(Func<IJsonRpcDuplexClient, T, Subscription> customSubscriptionDelegate)
        where T : IJsonRpcParam?, new() =>
        new(((client, args) => customSubscriptionDelegate(client, (T)args)), typeof(T));

    public void RegisterSubscriptionType(string subscriptionType, Func<IJsonRpcDuplexClient, Subscription> customSubscriptionDelegate) =>
        _subscriptionConstructors[subscriptionType] = CreateSubscriptionType(customSubscriptionDelegate);

    private static CustomSubscriptionType CreateSubscriptionType(Func<IJsonRpcDuplexClient, Subscription> customSubscriptionDelegate) =>
        new(((client, _) => customSubscriptionDelegate(client)));

    private struct CustomSubscriptionType
    {
        public Func<IJsonRpcDuplexClient, object, Subscription> Constructor { get; }
        public Type? ParamType { get; }

        public CustomSubscriptionType(Func<IJsonRpcDuplexClient, object, Subscription> constructor, Type? paramType = null)
        {
            Constructor = constructor;
            ParamType = paramType;
        }
    }
}
