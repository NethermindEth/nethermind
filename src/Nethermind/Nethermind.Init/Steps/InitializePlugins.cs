//  Copyright (c) 2021 Demerzel Solutions Limited
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

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Logging;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(InitializeBlockTree))]
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
            if(logger.IsInfo) logger.Info($"Initializing {_api.Plugins.Count} plugins");
            foreach (INethermindPlugin plugin in _api.Plugins)
            {
                try
                {
                    if(logger.IsInfo) logger.Info($"  {plugin.Name} by {plugin.Author}");
                    Stopwatch stopwatch = Stopwatch.StartNew();
                    await plugin.Init(_api);
                    stopwatch.Stop();
                    if (logger.IsInfo)
                        logger.Info($"  {plugin.Name} by {plugin.Author} initialized in {stopwatch.ElapsedMilliseconds}ms");
                }
                catch (Exception e)
                {
                    if(logger.IsError) logger.Error($"Failed to initialize plugin {plugin.Name} by {plugin.Author}", e);
                }
            }
        }
    }
}
