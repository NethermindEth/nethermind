//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.Subscribe
{
    public class SubscriptionManager : ISubscriptionManager
    {
        private readonly ISubscriptionFactory _subscriptionFactory;
        private readonly ILogger _logger;
        
        private readonly ConcurrentDictionary<string, Subscription> _subscriptions =
            new();
        private readonly ConcurrentDictionary<string, HashSet<Subscription>> _subscriptionsByJsonRpcClient =
            new();
        
        public SubscriptionManager(ISubscriptionFactory? subscriptionFactory, ILogManager? logManager)
        {
            _subscriptionFactory = subscriptionFactory ?? throw new ArgumentNullException(nameof(subscriptionFactory));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public string AddSubscription(IJsonRpcDuplexClient jsonRpcDuplexClient, SubscriptionType subscriptionType, Filter? filter = null)
        {
            Subscription subscription = _subscriptionFactory.CreateSubscription(jsonRpcDuplexClient, subscriptionType, filter);
            AddToDictionary(subscription);
            AddOrUpdateClientsBag(subscription);
            
            return subscription.Id;
        }

        private void AddToDictionary(Subscription subscription)
        {
            if (_subscriptions.TryAdd(subscription.Id, subscription))
            {
                if (_logger.IsTrace) _logger.Trace("Added subscription to dictionary _subscriptions.");
            }
            else if (_logger.IsDebug) _logger.Debug($"Failed trying to add subscription {subscription.Id} to dictionary _subscriptions.");
        }

        private void AddOrUpdateClientsBag(Subscription subscription)
        {
            _subscriptionsByJsonRpcClient.AddOrUpdate(subscription.JsonRpcDuplexClient.Id,
                k =>
                {
                    if (_logger.IsTrace) _logger.Trace($"Created client's subscriptions bag and added client's first subscription {subscription.Id} to it.");
                    subscription.JsonRpcDuplexClient.Closed += RemoveClientSubscriptions;
                    return new HashSet<Subscription>() {subscription};
                },
                (k, b) =>
                {
                    b.Add(subscription);
                    if (_logger.IsTrace) _logger.Trace($"Subscription {subscription.Id} added to client's subscriptions bag.");
                    return b;
                });
        }

        public bool RemoveSubscription(IJsonRpcDuplexClient jsonRpcDuplexClient, string subscriptionId)
        {
            if (_subscriptions.TryGetValue(subscriptionId, out var subscription)
                && subscription != null
                && subscription.JsonRpcDuplexClient.Id == jsonRpcDuplexClient.Id)
            {
                subscription.Dispose();
                RemoveFromClientsBag(subscription);
                RemoveFromDictionary(subscriptionId);
                return true;
            }
            if (_logger.IsDebug) _logger.Debug($"Failed trying to unsubscribe {subscriptionId}.");
            return false;
        }
        
        private void RemoveFromClientsBag(Subscription subscription)
        {
            if (!_subscriptionsByJsonRpcClient.TryGetValue(subscription.JsonRpcDuplexClient.Id, out var clientsSubscriptionsBag))
            {
                if (_logger.IsDebug) _logger.Debug($"Failed trying to find subscription {subscription.Id} in subscriptions bag of client {subscription.JsonRpcDuplexClient.Id}.");
            }
            else if (!clientsSubscriptionsBag.Remove(subscription))
            {
                if (_logger.IsDebug) _logger.Debug($"Failed trying to remove subscription {subscription.Id} from client's subscriptions bag.");
            }
            else if (_logger.IsTrace) _logger.Trace($"Subscription {subscription.Id} removed from client's subscriptions bag.");
        }

        private void RemoveFromDictionary(string subscriptionId)
        {
            if (_subscriptions.TryRemove(subscriptionId, out _))
            {
                if (_logger.IsTrace) _logger.Trace($"Subscription {subscriptionId} removed from dictionary _subscriptions.");
            }
            else if (_logger.IsDebug) _logger.Debug($"Failed trying to remove subscription {subscriptionId} from dictionary _subscriptions.");
        }

        public void RemoveClientSubscriptions(object? sender, EventArgs e)
        {
            IJsonRpcDuplexClient jsonRpcDuplexClient = (IJsonRpcDuplexClient)sender;

            if (jsonRpcDuplexClient != null
                && _subscriptionsByJsonRpcClient.TryRemove(jsonRpcDuplexClient.Id, out var subscriptionsBag))
            {
                DisposeAndRemoveFromDictionary(subscriptionsBag);
                if (_logger.IsTrace) _logger.Trace($"Client {jsonRpcDuplexClient.Id} removed from dictionary _subscriptionsByJsonRpcClient.");
            }
            else if (_logger.IsDebug) _logger.Debug($"Failed trying to remove client {jsonRpcDuplexClient?.Id} from dictionary _subscriptionsByJsonRpcClient.");
        }

        private void DisposeAndRemoveFromDictionary(HashSet<Subscription> subscriptionsBag)
        {
            foreach (var subscriptionInBag in subscriptionsBag)
            {
                if(_subscriptions.TryRemove(subscriptionInBag.Id, out var subscription)
                   && subscription != null)
                {
                    subscription.Dispose();
                    if (_logger.IsTrace) _logger.Trace($"Subscription {subscription.Id} removed from dictionary _subscriptions.");
                }
                else if (_logger.IsDebug) _logger.Debug($"Failed trying to remove subscription {subscriptionInBag.Id} from dictionary _subscriptions.");
            }
        }
    }
}
