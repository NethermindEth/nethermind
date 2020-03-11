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
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Channels
{
    public class JsonRpcNdmConsumerChannel : IJsonRpcNdmConsumerChannel
    {
        public const int MaxCapacity = 10000;
        private readonly ConcurrentDictionary<Keccak, ConcurrentQueue<string>> _data =
            new ConcurrentDictionary<Keccak, ConcurrentQueue<string>>();
        private readonly ILogger _logger;

        public NdmConsumerChannelType Type => NdmConsumerChannelType.JsonRpc;

        public JsonRpcNdmConsumerChannel(ILogManager logManager)
        {
            _logger = logManager.GetClassLogger();
        }

        public Task PublishAsync(Keccak depositId, string client, string data)
        {
            _data.AddOrUpdate(depositId, id =>
            {
                var queue = new ConcurrentQueue<string>();
                queue.Enqueue(data);

                return queue;
            }, (id, queue) =>
            {
                if (queue.Count >= MaxCapacity)
                {
                    if (_logger.IsWarn) _logger.Warn($"NDM data channel for JSON RPC has reached its max capacity: {MaxCapacity} items.");
                    
                    return queue;
                }
                
                queue.Enqueue(data);

                return queue;
            });

            return Task.CompletedTask;
        }

        public string? Pull(Keccak depositId)
        {
            if (!_data.TryGetValue(depositId, out var queue))
            {
                return null;
            }

            queue.TryDequeue(out var result);

            return result;
        }
    }
}