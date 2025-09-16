// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Abstractions;
using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain.Services;
using Nethermind.Core;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Core.Extensions;
using Nethermind.Merge.Plugin;

namespace Nethermind.HealthChecks
{
    public class HealthChecksPlugin : INethermindPlugin
    {
        private INethermindApi _api;
        private IHealthChecksConfig _healthChecksConfig;
        private ILogger _logger;
        private IMergeConfig _mergeConfig;

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
            _mergeConfig = _api.Config<IMergeConfig>();
            _logger = api.LogManager.GetClassLogger();

            //will throw an exception and close app or block until enough disk space is available (LowStorageCheckAwaitOnStartup)
            EnsureEnoughFreeSpace();

            return Task.CompletedTask;
        }

        public Task InitRpcModules()
        {
            if (_healthChecksConfig.LowStorageSpaceWarningThreshold > 0 || _healthChecksConfig.LowStorageSpaceShutdownThreshold > 0)
            {
                try
                {
                    FreeDiskSpaceChecker.StartAsync(default);
                }
                catch (Exception ex)
                {
                    if (_logger.IsError) _logger.Error("Failed to initialize available disk space check module", ex);
                }
            }

            if (_mergeConfig.Enabled)
            {
                _ = _api.EngineRequestsTracker.StartAsync(); // Fire and forget
            }

            if (_logger.IsInfo) _logger.Info("Health RPC Module has been enabled");
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

            builder.RegisterType<ClHealthRequestsTracker>()
                .As<IClHealthTracker>()
                .PreserveExistingDefaults();

            builder.RegisterType<ClHealthRequestsTracker>()
                .As<IEngineRequestsTracker>()
                .PreserveExistingDefaults();

            builder
                .AddSingleton<IHealthHintService, HealthHintService>()
                .AddSingleton<INodeHealthService, NodeHealthService>()
                .AddKeyedSingleton<IDriveInfo[]>(nameof(IInitConfig.BaseDbPath), (ctx) =>
                {
                    IFileSystem fileSystem = ctx.Resolve<IFileSystem>();
                    IInitConfig initConfig = ctx.Resolve<IInitConfig>();
                    return fileSystem.GetDriveInfos(initConfig.BaseDbPath);
                })
                .AddSingleton<FreeDiskSpaceChecker>()
                .AddSingleton<IJsonRpcServiceConfigurer, HealthCheckJsonRpcConfigurer>()

                .AddSingleton<ClHealthRequestsTracker>() // Note: Not resolved without merge plugin
                .RegisterSingletonJsonRpcModule<IHealthRpcModule, HealthRpcModule>()
                ;
        }
    }
}
