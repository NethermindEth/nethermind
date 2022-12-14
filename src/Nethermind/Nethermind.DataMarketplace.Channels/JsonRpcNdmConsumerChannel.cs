// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
