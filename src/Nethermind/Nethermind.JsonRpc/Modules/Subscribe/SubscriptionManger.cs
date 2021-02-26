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
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.Subscribe
{
    public class SubscriptionManger : ISubscriptionManger
    {
        private readonly ISubscriptionFactory _subscriptionFactory;
        private readonly ILogger _logger;
        
        private readonly ConcurrentDictionary<string, Subscription> _subscriptions =
            new ConcurrentDictionary<string, Subscription>();
        private readonly ConcurrentDictionary<string, ConcurrentBag<Subscription>> _subscriptionsByJsonRpcClient =
            new ConcurrentDictionary<string, ConcurrentBag<Subscription>>();
        
        public SubscriptionManger(ISubscriptionFactory? subscriptionFactory, ILogManager? logManager)
        {
            _subscriptionFactory = subscriptionFactory ?? throw new ArgumentNullException(nameof(subscriptionFactory));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public string AddSubscription(SubscriptionType subscriptionType, Filter? filter = null)
        {
            Subscription subscription = _subscriptionFactory.CreateSubscription(subscriptionType, filter);
            if(_subscriptions.TryAdd(subscription.Id, subscription))
            {
                if (_logger.IsTrace) _logger.Trace("Added subscription to dictionary _subscriptions.");
            } else if (_logger.IsDebug) _logger.Debug($"Failed trying to add subscription {subscription.Id} to dictionary _subscriptions.");

            return subscription.Id;
        }

        public bool RemoveSubscription(string subscriptionId)
        {
            if (_subscriptions.TryRemove(subscriptionId, out var subscription))
            {
                subscription.Dispose();
                if (_logger.IsTrace) _logger.Trace($"Subscription {subscriptionId} removed from dictionary _subscriptions.");
                
                if (_subscriptionsByJsonRpcClient.TryGetValue(subscription.JsonRpcDuplexClient.Id, out var subByJsonRpc))
                {
                    if (subByJsonRpc.TryTake(out _))
                    {
                        if (_logger.IsTrace) _logger.Trace($"Subscription {subscriptionId} removed from client's subscriptions bag.");
                        return true;
                    }
                    if (_logger.IsDebug) _logger.Debug($"Failed trying to remove subscription {subscriptionId} from client's subscriptions bag.");
                    return false;
                }
                if (_logger.IsDebug) _logger.Debug($"Failed trying to get client's subscriptions bag.");
            }
            else if (_logger.IsDebug) _logger.Debug($"Failed trying to remove subscription {subscriptionId} from dictionary _subscriptions.");
            return false;
        }

        public void BindJsonRpcDuplexClient(string subscriptionId, IJsonRpcDuplexClient jsonRpcDuplexClient)
        {
            if (_subscriptions.TryGetValue(subscriptionId, out var subscription))
            {
                subscription.JsonRpcDuplexClient = jsonRpcDuplexClient;
                _subscriptionsByJsonRpcClient.AddOrUpdate(jsonRpcDuplexClient.Id,
                    k =>
                    {
                        if (_logger.IsTrace) _logger.Trace($"Created client's subscriptions bag and added client's first subscription {subscriptionId} to it.");
                        return new ConcurrentBag<Subscription>() {subscription};
                    },
                    (k, b) =>
                    {
                        b.Add(subscription);
                        if (_logger.IsTrace) _logger.Trace($"Subscription {subscriptionId} added to client's subscriptions bag.");
                        return b;
                    });
                subscription.BindEvents();
            }
            else if (_logger.IsDebug) _logger.Debug($"Failed trying to find subscription {subscriptionId} in dictionary _subscriptions.");
        }

        public void RemoveSubscriptions(IJsonRpcDuplexClient jsonRpcDuplexClient)
        {
            if (_subscriptionsByJsonRpcClient.TryRemove(jsonRpcDuplexClient.Id, out var subscriptionsBag))
            {
                foreach (var subscriptionInBag in subscriptionsBag)
                {
                    if(_subscriptions.TryRemove(subscriptionInBag.Id, out var subscription))
                    {
                        if (subscription != null)
                        {
                            subscription.Dispose();
                            if (_logger.IsTrace) _logger.Trace($"Subscription {subscription.Id} removed from dictionary _subscriptions.");
                        }
                    }
                    else if (_logger.IsDebug) _logger.Debug($"Failed trying to remove subscription {subscriptionInBag.Id} from dictionary _subscriptions.");
                }
                if (_logger.IsTrace) _logger.Trace($"Client {jsonRpcDuplexClient.Id} removed from dictionary _subscriptionsByJsonRpcClient.");
            }
            else if (_logger.IsDebug) _logger.Debug($"Failed trying to remove client {jsonRpcDuplexClient.Id} from dictionary _subscriptionsByJsonRpcClient.");
        }
    }
}
