// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Autofac;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Logging;
using Nethermind.Runner.Modules;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Runner.Ethereum.Api
{
    public class ApiBuilder
    {
        private readonly IConfigProvider _configProvider;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly ILogManager _logManager;
        private readonly ILogger _logger;
        private readonly IProcessExitSource _processExitSource;
        private readonly IInitConfig _initConfig;

        public ApiBuilder(IConfigProvider configProvider, IProcessExitSource processExitSource, ILogManager logManager)
        {
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _logger = _logManager.GetClassLogger();
            _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
            _initConfig = configProvider.GetConfig<IInitConfig>();
            _processExitSource = processExitSource;
            _jsonSerializer = new EthereumJsonSerializer();
        }

        public IContainer Create(IEnumerable<Type> plugins)
        {
            ChainSpec chainSpec = LoadChainSpec(_jsonSerializer);
            bool wasCreated = Interlocked.CompareExchange(ref _apiCreated, 1, 0) == 1;
            if (wasCreated)
            {
                throw new NotSupportedException("Creation of multiple APIs not supported.");
            }

            ContainerBuilder containerBuilder = new ContainerBuilder();
            containerBuilder.RegisterInstance(_configProvider).SingleInstance();
            containerBuilder.RegisterInstance(_processExitSource).SingleInstance();
            containerBuilder.RegisterInstance(_jsonSerializer).SingleInstance();
            containerBuilder.RegisterInstance(_logManager).SingleInstance();
            containerBuilder.RegisterInstance(chainSpec).SingleInstance();

            containerBuilder.RegisterModule(new BaseModule());
            containerBuilder.RegisterModule(new CoreModule());
            containerBuilder.RegisterModule(new RunnerModule());
            ApplyPluginModule(plugins, chainSpec, containerBuilder);

            return containerBuilder.Build();
        }

        private void ApplyPluginModule(IEnumerable<Type> plugins, ChainSpec chainSpec, ContainerBuilder containerBuilder)
        {
            ContainerBuilder pluginLoaderBuilder = new ContainerBuilder();
            pluginLoaderBuilder.RegisterInstance(_configProvider).SingleInstance();
            pluginLoaderBuilder.RegisterInstance(_processExitSource).SingleInstance();
            pluginLoaderBuilder.RegisterInstance(_jsonSerializer).SingleInstance();
            pluginLoaderBuilder.RegisterInstance(_logManager).SingleInstance();
            pluginLoaderBuilder.RegisterInstance(chainSpec).SingleInstance();
            pluginLoaderBuilder.RegisterModule(new BaseModule());
            foreach (Type plugin in plugins)
            {
                pluginLoaderBuilder.RegisterType(plugin)
                    .As<INethermindPlugin>()
                    .ExternallyOwned();
            }

            // This is just to load the plugins in a way that its constructor injectable with related config.
            // The plugins need to have enough information in order for it to know that it should enable itself or not.
            using IContainer pluginLoader = pluginLoaderBuilder.Build();

            foreach (INethermindPlugin nethermindPlugin in pluginLoader.Resolve<IEnumerable<INethermindPlugin>>())
            {
                if (_logger.IsDebug) _logger.Warn($"Plugin {nethermindPlugin.Name} enabled: {nethermindPlugin.Enabled}");
                if (!nethermindPlugin.Enabled)
                {
                    continue;
                }

                containerBuilder.AddInstance(nethermindPlugin);

                if (nethermindPlugin.ContainerModule is Module module)
                {
                    containerBuilder.AddModule(nethermindPlugin.ContainerModule);
                }
            }
        }

        private int _apiCreated;

        private ChainSpec LoadChainSpec(IJsonSerializer ethereumJsonSerializer)
        {
            bool hiveEnabled = Environment.GetEnvironmentVariable("NETHERMIND_HIVE_ENABLED")?.ToLowerInvariant() == "true";
            bool hiveChainSpecExists = File.Exists(_initConfig.HiveChainSpecPath);

            string chainSpecFile;
            if (hiveEnabled && hiveChainSpecExists)
                chainSpecFile = _initConfig.HiveChainSpecPath;
            else
                chainSpecFile = _initConfig.ChainSpecPath;

            if (_logger.IsDebug) _logger.Debug($"Loading chain spec from {chainSpecFile}");

            ThisNodeInfo.AddInfo("Chainspec    :", $"{chainSpecFile}");

            IChainSpecLoader loader = new ChainSpecLoader(ethereumJsonSerializer);
            ChainSpec chainSpec = loader.LoadEmbeddedOrFromFile(chainSpecFile, _logger);
            SetLoggerVariables(chainSpec);
            return chainSpec;
        }

        private void SetLoggerVariables(ChainSpec chainSpec)
        {
            _logManager.SetGlobalVariable("chain", chainSpec.Name);
            _logManager.SetGlobalVariable("chainId", chainSpec.ChainId);
            _logManager.SetGlobalVariable("engine", chainSpec.SealEngineType);
        }
    }
}
