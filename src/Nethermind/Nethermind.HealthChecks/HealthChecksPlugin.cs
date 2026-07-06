// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.Blockchain.Services;
using Nethermind.Core;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.HealthChecks
{
    public class HealthChecksPlugin : INethermindPlugin
    {
        private INethermindApi _api;
        private IHealthChecksConfig _healthChecksConfig;

        public string Name => "HealthChecks";

        public string Description => "Endpoints that takes care of node`s health";

        public string Author => "Nethermind";

        public bool MustInitialize => true;
        public bool Enabled => true; // Always enabled

        private FreeDiskSpaceChecker FreeDiskSpaceChecker => _api.Context.Resolve<FreeDiskSpaceChecker>();

        public Task Init(INethermindApi api)
        {
            _api = api;
            _healthChecksConfig = _api.Config<IHealthChecksConfig>();

            //will throw an exception and close app or block until enough disk space is available (LowStorageCheckAwaitOnStartup)
            EnsureEnoughFreeSpace();

            return Task.CompletedTask;
        }

        private void EnsureEnoughFreeSpace()
        {
            if (_healthChecksConfig.LowStorageSpaceShutdownThreshold > 0)
            {
                FreeDiskSpaceChecker.EnsureEnoughFreeSpaceOnStart(_api.TimerFactory);
            }
        }

        public IModule Module => new HealthCheckPluginModule();
    }

    public class HealthCheckPluginModule : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            base.Load(builder);

            builder
                .AddSingleton<IHealthHintService, HealthHintService>()
                .AddSingleton<INodeHealthService, NodeHealthService>()
                .AddSingleton<FreeDiskSpaceChecker>()
                .AddSingleton<IJsonRpcServiceConfigurer, HealthCheckJsonRpcConfigurer>()

                .AddSingleton<ClHealthRequestsTracker>() // Note: Not resolved without merge plugin
                .Bind<IClHealthTracker, ClHealthRequestsTracker>()
                .Bind<IEngineRequestsTracker, ClHealthRequestsTracker>()

                .RegisterSingletonJsonRpcModule<IHealthRpcModule, HealthRpcModule>()

                .AddStep(typeof(StartHealthChecks))
                ;
        }
    }
}
