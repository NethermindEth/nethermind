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
using Nethermind.Runner.Analytics;
using Nethermind.Runner.Ethereum.Context;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependencies(typeof(StartBlockProcessor))]
    public class StartWebSocketProducer : IStep
    {
        private readonly EthereumRunnerContext _context;

        public StartWebSocketProducer(EthereumRunnerContext context)
        {
            _context = context;
        }

        public Task Execute(CancellationToken cancellationToken)
        {
            IInitConfig initConfig = _context.Config<IInitConfig>();
            if (initConfig.WebSocketsEnabled)
            {
                AnalyticsWebSocketsModule? analyticsModule =
                    _context.WebSocketsManager?.GetModule("analytics") as AnalyticsWebSocketsModule;
                if (analyticsModule != null)
                {
                    _context.Producers.Add(analyticsModule);
                }
            }

            return Task.CompletedTask;
        }
    }
}