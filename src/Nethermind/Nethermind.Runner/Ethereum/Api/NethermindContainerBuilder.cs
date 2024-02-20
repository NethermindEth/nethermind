// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Autofac;
using Autofac.Core;
using Autofac.Features.ResolveAnything;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.JsonRpc.Data;
using Nethermind.Logging;
using Nethermind.Runner.Modules;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Runner.Ethereum.Api
{
    public class NethermindContainerBuilder
    {
        private readonly IConfigProvider _configProvider;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IProcessExitSource _processExitSource;
        private readonly ILogManager _logManager;
        private readonly ILogger _logger;
        private readonly IInitConfig _initConfig;

        public NethermindContainerBuilder(IConfigProvider configProvider, IProcessExitSource processExitSource,
            ILogManager logManager)
        {
            _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
            _processExitSource = processExitSource ?? throw new ArgumentNullException(nameof(processExitSource));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _logger = _logManager.GetClassLogger();
            _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
            _initConfig = configProvider.GetConfig<IInitConfig>();
            _jsonSerializer = new EthereumJsonSerializer();
        }

        public IContainer Create(params Type[] plugins) =>
            Create((IEnumerable<Type>)plugins);

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
            containerBuilder.RegisterModule(new CoreModule(_configProvider));
            containerBuilder.RegisterModule(new DatabaseModule(_configProvider));
            containerBuilder.RegisterModule(new StateModule(_configProvider));
            containerBuilder.RegisterModule(new NetworkModule());
            containerBuilder.RegisterModule(new KeyStoreModule());
            containerBuilder.RegisterModule(new StepModule());
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

            using IContainer pluginLoader = pluginLoaderBuilder.Build();

            foreach (INethermindPlugin nethermindPlugin in pluginLoader.Resolve<IEnumerable<INethermindPlugin>>())
            {
                if (!nethermindPlugin.Enabled) continue;

                containerBuilder.RegisterInstance(nethermindPlugin).As<INethermindPlugin>();
                IModule? pluginModule = nethermindPlugin.Module;
                if (pluginModule != null)
                {
                    containerBuilder.RegisterModule(pluginModule);
                }
            }
        }

        private int _apiCreated;

        private ChainSpec LoadChainSpec(IJsonSerializer ethereumJsonSerializer)
        {
            bool hiveEnabled = Environment.GetEnvironmentVariable("NETHERMIND_HIVE_ENABLED")?.ToLowerInvariant() ==
                               "true";
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
            TransactionForRpc.DefaultChainId = chainSpec.ChainId;
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
