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
// 

using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Logging;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependencies(typeof(InitializeBlockchain))]
    public class InitializePlugins : IStep
    {
        private readonly INethermindApi _api;

        public InitializePlugins(INethermindApi api)
        {
            _api = api;
        }
        
        public async Task Execute(CancellationToken cancellationToken)
        {
            ILogger logger = _api.LogManager.GetClassLogger();
            foreach (INethermindPlugin plugin in _api.Plugins)
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                await plugin.Init(_api);
                stopwatch.Stop();
                if(logger.IsInfo) logger.Info(
                    $"Initialized {plugin.Name} plugin by {plugin.Author} in {stopwatch.ElapsedMilliseconds}ms");
            }
        }
    }
}