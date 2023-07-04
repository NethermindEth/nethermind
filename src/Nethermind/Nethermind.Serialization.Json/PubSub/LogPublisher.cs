// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core.PubSub;
using Nethermind.Logging;

namespace Nethermind.Serialization.Json.PubSub
{
    public class LogPublisher : IPublisher
    {
        private ILogger _logger;
        private IJsonSerializer _jsonSerializer;

        public LogPublisher(IJsonSerializer jsonSerializer, ILogManager logManager)
        {
            _logger = logManager.GetClassLogger<LogPublisher>();
            _jsonSerializer = jsonSerializer;
        }

        public Task PublishAsync<T>(T data) where T : class
        {
            if (_logger.IsInfo) _logger.Info(_jsonSerializer.Serialize(data));
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }
}
