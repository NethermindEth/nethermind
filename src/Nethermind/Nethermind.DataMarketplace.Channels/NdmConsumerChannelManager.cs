// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
