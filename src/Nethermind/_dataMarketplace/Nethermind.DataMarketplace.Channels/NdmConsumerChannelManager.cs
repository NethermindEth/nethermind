//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;

namespace Nethermind.DataMarketplace.Channels
{
    public class NdmConsumerChannelManager : INdmConsumerChannelManager
    {
        private readonly ConcurrentDictionary<object, INdmConsumerChannel> _channels =
            new ConcurrentDictionary<object, INdmConsumerChannel>();

        public void Add(INdmConsumerChannel ndmConsumerChannel)
        {
            _channels.TryAdd(ndmConsumerChannel, ndmConsumerChannel);
        }

        public void Remove(INdmConsumerChannel ndmConsumerChannel)
        {
            _channels.TryRemove(ndmConsumerChannel, out _);
        }

        public async Task PublishAsync(Keccak depositId, string client, string data)
        {
            var channels = _channels.Values.ToArray();
            await Task.WhenAll(channels.Select(c => c.PublishAsync(depositId, client, data)));
        }
    }
}