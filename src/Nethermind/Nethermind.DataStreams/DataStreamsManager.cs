/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Concurrent;

namespace Nethermind.DataStreams
{
    public class DataStreamsManager : IDataStreamsManager
    {
        private ConcurrentDictionary<string, Subscription> _subscriptions = new ConcurrentDictionary<string, Subscription>();

        public event EventHandler<SubscriptionEventArgs> SubscriptionAdded;
        public event EventHandler<SubscriptionEventArgs> SubscriptionRemoved;

        public Subscription GetSubscription(string topic)
        {
            if (_subscriptions.ContainsKey(topic)) return _subscriptions[topic];

            return null;
        }

        public void Publish(string topic, byte[] data)
        {
            if (!_subscriptions.ContainsKey(topic)) return;

            throw new NotImplementedException();
        }

        public void Subscribe(Subscription subscription)
        {
            _subscriptions.TryAdd(subscription.Topic, subscription);
            SubscriptionAdded?.Invoke(this, new SubscriptionEventArgs(subscription));
        }

        public void Unsubscribe(Subscription subscription)
        {
            _subscriptions.TryRemove(subscription.Topic, out Subscription _);
            SubscriptionRemoved?.Invoke(this, new SubscriptionEventArgs(subscription));
        }
    }
}