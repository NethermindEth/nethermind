//  Copyright (c) 2022 Demerzel Solutions Limited
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Nethermind.Core.MessageBus
{
    public class SimpleInMemoryMessageBus : ISimpleMessageBus
    {
        private readonly Dictionary<Type, List<IBusSubcription>> _allSubscriptions = new();

        public IBusSubcription Subscribe<T>(Func<T, Task> action) where T : IMessage
        {
            Type mType = typeof(T);
            if (!_allSubscriptions.TryGetValue(mType, out List<IBusSubcription>? typeSubscriptions))
                _allSubscriptions[mType] = typeSubscriptions = new List<IBusSubcription>();

            BusSubscription<T> newSubscription = new(action);
            newSubscription.Disposed += (s, e) =>
            {
                typeSubscriptions.Remove(newSubscription);
                if (typeSubscriptions.Count == 0)
                    _allSubscriptions.Remove(mType);
            };

            typeSubscriptions.Add(newSubscription);

            return newSubscription;
        }

        public void Unsubscribe<T>(IBusSubcription subcription) where T : IMessage
        {
            subcription.Dispose();
        }

        public async Task Publish<T>(T message) where T : IMessage
        {
            var messageType = typeof(T);
            if (_allSubscriptions.TryGetValue(messageType, out List<IBusSubcription>? typeSubscriptions) && typeSubscriptions.Count > 0)
            {
                var typeSubscriptionsCopy = typeSubscriptions.ToList();
                foreach (var subscription in typeSubscriptionsCopy)
                {
                    await subscription.Process(message);
                }
            }
            else
            {
                //nothing to do...
                return;
            }
        }
    }
}
