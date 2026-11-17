// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Nethermind.JsonRpc.Exceptions;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.Subscribe
{
    public class SubscriptionManager(ISubscriptionFactory? subscriptionFactory, IJsonRpcConfig jsonRpcConfig, ILogManager? logManager) : ISubscriptionManager
    {
        private readonly ISubscriptionFactory _subscriptionFactory = subscriptionFactory ?? throw new ArgumentNullException(nameof(subscriptionFactory));
        private readonly ILogger _logger = logManager?.GetClassLogger<SubscriptionManager>() ?? throw new ArgumentNullException(nameof(logManager));

        private readonly ConcurrentDictionary<string, Subscription> _subscriptions =
            new();
        private readonly ConcurrentDictionary<string, ClientSubscriptions> _subscriptionsByJsonRpcClient =
            new();

        private int _subscriptionCount;

        public string AddSubscription(IJsonRpcDuplexClient jsonRpcDuplexClient, string subscriptionType, string? args = null)
        {
            Subscription? subscription = null;
            try
            {
                subscription = Admit(jsonRpcDuplexClient, subscriptionType, args, out ClientSubscriptions clientSubscriptions);
                Register(jsonRpcDuplexClient, clientSubscriptions, subscription);
                return subscription.Id;
            }
            finally
            {
                // Closed is raised once, after the client is marked closed, so a subscription registered after it would
                // never be disposed. Checking the flag after hooking means either the handler runs or this sees it closed.
                if (jsonRpcDuplexClient.IsClosed) RemoveSubscriptionsOfClosedClient(jsonRpcDuplexClient, subscription);
            }
        }

        private Subscription Admit(IJsonRpcDuplexClient client, string subscriptionType, string? args, out ClientSubscriptions clientSubscriptions)
        {
            clientSubscriptions = GetOrCreateClientSubscriptions(client);
            clientSubscriptions.ReserveSlot(jsonRpcConfig.MaxSubscriptionsPerConnection);
            try
            {
                ReserveNodeSlot(client);
            }
            catch
            {
                clientSubscriptions.ReleaseSlot();
                throw;
            }

            try
            {
                return _subscriptionFactory.CreateSubscription(client, subscriptionType, args);
            }
            catch
            {
                clientSubscriptions.ReleaseSlot();
                Interlocked.Decrement(ref _subscriptionCount);
                throw;
            }
        }

        private void ReserveNodeSlot(IJsonRpcDuplexClient client)
        {
            int count = Interlocked.Increment(ref _subscriptionCount);
            int maxSubscriptions = jsonRpcConfig.MaxSubscriptions;
            if (maxSubscriptions > 0 && !client.IsAuthenticated && count > maxSubscriptions)
            {
                Interlocked.Decrement(ref _subscriptionCount);
                throw new LimitExceededException($"Too many subscriptions. The node limit is {maxSubscriptions}.");
            }
        }

        private void Register(IJsonRpcDuplexClient client, ClientSubscriptions clientSubscriptions, Subscription subscription)
        {
            clientSubscriptions.Subscriptions[subscription.Id] = subscription;
            if (!_subscriptions.TryAdd(subscription.Id, subscription))
            {
                clientSubscriptions.TryRemove(subscription);
                Interlocked.Decrement(ref _subscriptionCount);
                if (_logger.IsDebug) _logger.Debug($"Failed trying to add subscription {subscription.Id} to dictionary _subscriptions.");
                return;
            }

            if (!IsRegistered(client, clientSubscriptions))
            {
                if (TryRemoveFromDictionary(subscription.Id, out _)) subscription.Dispose();
                return;
            }

            if (_logger.IsTrace) _logger.Trace($"Subscription {subscription.Id} added to client's subscriptions.");
        }

        private bool IsRegistered(IJsonRpcDuplexClient client, ClientSubscriptions clientSubscriptions) =>
            _subscriptionsByJsonRpcClient.TryGetValue(client.Id, out ClientSubscriptions? current) && ReferenceEquals(current, clientSubscriptions);

        private ClientSubscriptions GetOrCreateClientSubscriptions(IJsonRpcDuplexClient client)
        {
            if (_subscriptionsByJsonRpcClient.TryGetValue(client.Id, out ClientSubscriptions? existing)) return existing;

            // A connection processes its requests concurrently, so its first two subscriptions can race to create the
            // client's entry: only the caller whose entry was stored hooks Closed, and the other joins the stored entry.
            ClientSubscriptions created = new();
            ClientSubscriptions clientSubscriptions = _subscriptionsByJsonRpcClient.GetOrAdd(client.Id, created);
            if (ReferenceEquals(clientSubscriptions, created))
            {
                client.Closed += OnJsonRpcDuplexClientClosed;
                if (_logger.IsTrace) _logger.Trace($"Created subscriptions of client {client.Id}.");
            }

            return clientSubscriptions;
        }

        private bool TryRemoveFromDictionary(string subscriptionId, out Subscription? subscription)
        {
            if (!_subscriptions.TryRemove(subscriptionId, out subscription)) return false;
            Interlocked.Decrement(ref _subscriptionCount);
            return true;
        }

        private void RemoveSubscriptionsOfClosedClient(IJsonRpcDuplexClient client, Subscription? subscription)
        {
            client.Closed -= OnJsonRpcDuplexClientClosed;
            RemoveClientSubscriptions(client);
            if (subscription is not null && TryRemoveFromDictionary(subscription.Id, out _)) subscription.Dispose();
        }

        private void OnJsonRpcDuplexClientClosed(object? sender, EventArgs e)
        {
            IJsonRpcDuplexClient jsonRpcDuplexClient = (IJsonRpcDuplexClient)sender;
            RemoveClientSubscriptions(jsonRpcDuplexClient!);
            jsonRpcDuplexClient.Closed -= OnJsonRpcDuplexClientClosed;
        }

        public bool RemoveSubscription(IJsonRpcDuplexClient jsonRpcDuplexClient, string subscriptionId)
        {
            if (_subscriptions.TryGetValue(subscriptionId, out Subscription? subscription)
                && subscription is not null
                && subscription.JsonRpcDuplexClient.Id == jsonRpcDuplexClient.Id)
            {
                RemoveFromClient(subscription);
                RemoveFromDictionary(subscription);
                return true;
            }
            if (_logger.IsDebug) _logger.Debug($"Failed trying to unsubscribe {subscriptionId}.");
            return false;
        }

        private void RemoveFromClient(Subscription subscription)
        {
            if (!_subscriptionsByJsonRpcClient.TryGetValue(subscription.JsonRpcDuplexClient.Id, out ClientSubscriptions? clientSubscriptions))
            {
                if (_logger.IsDebug) _logger.Debug($"Failed trying to find subscription {subscription.Id} in subscriptions of client {subscription.JsonRpcDuplexClient.Id}.");
                return;
            }

            if (!clientSubscriptions.TryRemove(subscription))
            {
                if (_logger.IsDebug) _logger.Debug($"Failed trying to remove subscription {subscription.Id} from client's subscriptions.");
            }
            else if (_logger.IsTrace) _logger.Trace($"Subscription {subscription.Id} removed from client's subscriptions.");
        }

        private void RemoveFromDictionary(Subscription subscription)
        {
            if (TryRemoveFromDictionary(subscription.Id, out _))
            {
                subscription.Dispose();
                if (_logger.IsTrace) _logger.Trace($"Subscription {subscription.Id} removed from dictionary _subscriptions.");
            }
            else if (_logger.IsDebug) _logger.Debug($"Failed trying to remove subscription {subscription.Id} from dictionary _subscriptions.");
        }

        public void RemoveClientSubscriptions(IJsonRpcDuplexClient jsonRpcDuplexClient)
        {
            string clientId = jsonRpcDuplexClient.Id;
            if (_subscriptionsByJsonRpcClient.TryRemove(clientId, out ClientSubscriptions? clientSubscriptions))
            {
                DisposeAndRemoveFromDictionary(clientSubscriptions);
                if (_logger.IsTrace) _logger.Trace($"Client {clientId} removed from dictionary _subscriptionsByJsonRpcClient.");
            }
            else if (_logger.IsDebug) _logger.Debug($"Failed trying to remove client {clientId} from dictionary _subscriptionsByJsonRpcClient.");
        }

        private void DisposeAndRemoveFromDictionary(ClientSubscriptions clientSubscriptions)
        {
            foreach (KeyValuePair<string, Subscription> entry in clientSubscriptions.Subscriptions)
            {
                string subscriptionId = entry.Key;
                if (TryRemoveFromDictionary(subscriptionId, out Subscription? subscription)
                    && subscription is not null)
                {
                    subscription.Dispose();
                    if (_logger.IsTrace) _logger.Trace($"Subscription {subscription.Id} removed from dictionary _subscriptions.");
                }
                else if (_logger.IsDebug) _logger.Debug($"Failed trying to remove subscription {subscriptionId} from dictionary _subscriptions.");
            }
        }

        private sealed class ClientSubscriptions
        {
            private int _count;

            public ConcurrentDictionary<string, Subscription> Subscriptions { get; } = new(concurrencyLevel: 1, capacity: 4);

            public void ReserveSlot(int maxPerConnection)
            {
                int count = Interlocked.Increment(ref _count);
                if (maxPerConnection > 0 && count > maxPerConnection)
                {
                    Interlocked.Decrement(ref _count);
                    throw new LimitExceededException($"Too many subscriptions. The connection limit is {maxPerConnection}.");
                }
            }

            public void ReleaseSlot() => Interlocked.Decrement(ref _count);

            public bool TryRemove(Subscription subscription)
            {
                if (!Subscriptions.TryRemove(KeyValuePair.Create(subscription.Id, subscription))) return false;
                ReleaseSlot();
                return true;
            }
        }
    }
}
