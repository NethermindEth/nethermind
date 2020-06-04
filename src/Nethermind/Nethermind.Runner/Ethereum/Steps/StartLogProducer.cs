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

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.PubSub;
using Nethermind.Runner.Analytics;
using Nethermind.Runner.Ethereum.Context;
using Nethermind.Serialization.Json;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependencies(typeof(StartBlockProcessor))]
    public class StartLogProducer : IStep
    {
        private readonly EthereumRunnerContext _context;

        public StartLogProducer(EthereumRunnerContext context)
        {
            _context = context;
        }

        public Task Execute(CancellationToken cancellationToken)
        {
            IAnalyticsConfig analyticsConfig = _context.Config<IAnalyticsConfig>();
            if (analyticsConfig.LogPublishedData)
            {
                LogProducer logProducer = new LogProducer(_context.EthereumJsonSerializer!, _context.LogManager);
                _context.Producers.Add(logProducer);
            }

            return Task.CompletedTask;
        }

        private class LogProducer : IProducer
        {
            private ILogger _logger;
            private IJsonSerializer _jsonSerializer;

            public LogProducer(IJsonSerializer jsonSerializer, ILogManager logManager)
            {
                _logger = logManager.GetClassLogger<LogProducer>();
                _jsonSerializer = jsonSerializer;
            }

            public Task PublishAsync<T>(T data) where T : class
            {
                if (_logger.IsInfo) _logger.Info(_jsonSerializer.Serialize(data));
                return Task.CompletedTask;
            }
        }
    }
}