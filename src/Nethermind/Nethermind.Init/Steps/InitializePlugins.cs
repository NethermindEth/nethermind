// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
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
            if (logger.IsInfo) logger.Info($"Initializing {_api.Plugins.Count} plugins");
            foreach (INethermindPlugin plugin in _api.Plugins)
            {
                try
                {
                    if (logger.IsInfo) logger.Info($"  {plugin.Name} by {plugin.Author}");
                    long startTime = Stopwatch.GetTimestamp();
                    await plugin.Init(_api);
                    if (logger.IsInfo)
                        logger.Info($"  {plugin.Name} by {plugin.Author} initialized in {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds:N0}ms");
                }
                catch (Exception e)
                {
                    if (logger.IsError) logger.Error($"Failed to initialize plugin {plugin.Name} by {plugin.Author}", e);
                    if (plugin.MustInitialize) throw;
                }
            }
        }
    }
}
