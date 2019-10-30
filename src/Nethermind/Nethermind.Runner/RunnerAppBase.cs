/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.CommandLineUtils;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Json;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Channels.Grpc;
using Nethermind.DataMarketplace.Consumers.Infrastructure;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Infrastructure.Modules;
using Nethermind.DataMarketplace.Initializers;
using Nethermind.DataMarketplace.WebSockets;
using Nethermind.Grpc;
using Nethermind.Grpc.Servers;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Web3;
using Nethermind.JsonRpc.WebSockets;
using Nethermind.Monitoring;
using Nethermind.Monitoring.Metrics;
using Nethermind.Logging;
using Nethermind.Monitoring.Config;
using Nethermind.Runner.Config;
using Nethermind.Runner.Runners;
using Nethermind.WebSockets;
using NLog;
using NLog.Config;
using ILogger = Nethermind.Logging.ILogger;

namespace Nethermind.Runner
{
    public abstract class RunnerAppBase
    {
        protected ILogger Logger;
        private IRunner _jsonRpcRunner = NullRunner.Instance;
        private IRunner _ethereumRunner = NullRunner.Instance;
        private IRunner _grpcRunner = NullRunner.Instance;
        private TaskCompletionSource<object> _cancelKeySource;
        private IMonitoringService _monitoringService;
        
        protected RunnerAppBase(ILogger logger)
        {
            Logger = logger;
            AppDomain.CurrentDomain.ProcessExit += CurrentDomainOnProcessExit;
        }

        private void CurrentDomainOnProcessExit(object sender, EventArgs e)
        {
        }

        private void LogMemoryConfiguration()
        {
            if (Logger.IsDebug) Logger.Debug($"Server GC           : {System.Runtime.GCSettings.IsServerGC}");
            if (Logger.IsDebug) Logger.Debug($"GC latency mode     : {System.Runtime.GCSettings.LatencyMode}");
            if (Logger.IsDebug)
                Logger.Debug($"LOH compaction mode : {System.Runtime.GCSettings.LargeObjectHeapCompactionMode}");
        }

        public void Run(string[] args)
        {
            var (app, buildConfigProvider, getDbBasePath) = BuildCommandLineApp();
            ManualResetEventSlim appClosed = new ManualResetEventSlim(false);
            app.OnExecute(async () =>
            {
                var configProvider = buildConfigProvider();
                var initConfig = configProvider.GetConfig<IInitConfig>();
                LogManager.Configuration = new XmlLoggingConfiguration("NLog.config".GetApplicationResourcePath());
                Logger = new NLogLogger(initConfig.LogFileName, initConfig.LogDirectory);
                LogMemoryConfiguration();

                var pathDbPath = getDbBasePath();
                if (!string.IsNullOrWhiteSpace(pathDbPath))
                {
                    var newDbPath = Path.Combine(pathDbPath, initConfig.BaseDbPath);
                    if (Logger.IsDebug) Logger.Debug($"Adding prefix to baseDbPath, new value: {newDbPath}, old value: {initConfig.BaseDbPath}");
                    initConfig.BaseDbPath = newDbPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "db");
                }

                Console.Title = initConfig.LogFileName;
                Console.CancelKeyPress += ConsoleOnCancelKeyPress;

                var serializer = new UnforgivingJsonSerializer();
                if (Logger.IsInfo)
                {
                    Logger.Info($"Nethermind config:\n{serializer.Serialize(initConfig, true)}\n");
                }

                _cancelKeySource = new TaskCompletionSource<object>();

                await StartRunners(configProvider);
                await _cancelKeySource.Task;

                Console.WriteLine("Closing, please wait until all functions are stopped properly...");
                StopAsync().Wait();
                Console.WriteLine("All done, goodbye!");
                appClosed.Set();

                return 0;
            });

            app.Execute(args);
            appClosed.Wait();
        }

        private void ConsoleOnCancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            _cancelKeySource?.SetResult(null);
            e.Cancel = false;
        }

        [Todo(Improve.Refactor, "network config can be handled internally in EthereumRunner")]
        protected async Task StartRunners(IConfigProvider configProvider)
        {
            IInitConfig initConfig = configProvider.GetConfig<IInitConfig>();
            IJsonRpcConfig jsonRpcConfig = configProvider.GetConfig<IJsonRpcConfig>();
            var metricsParams = configProvider.GetConfig<IMetricsConfig>();
            var logManager = new NLogManager(initConfig.LogFileName, initConfig.LogDirectory);
            IRpcModuleProvider rpcModuleProvider = jsonRpcConfig.Enabled
                ? new RpcModuleProvider(configProvider.GetConfig<IJsonRpcConfig>(), logManager)
                : (IRpcModuleProvider) NullModuleProvider.Instance;
            var jsonSerializer = new EthereumJsonSerializer();
            var webSocketsManager = new WebSocketsManager();

            INdmDataPublisher ndmDataPublisher = null;
            INdmConsumerChannelManager ndmConsumerChannelManager = null;
            INdmInitializer ndmInitializer = null;
            var ndmConfig = configProvider.GetConfig<INdmConfig>();
            var ndmEnabled = ndmConfig.Enabled;
            if (ndmEnabled)
            {
                ndmDataPublisher = new NdmDataPublisher();
                ndmConsumerChannelManager = new NdmConsumerChannelManager();
                var initializerName = ndmConfig.InitializerName;
                if (Logger.IsInfo) Logger.Info($"NDM initializer: {initializerName}");
                var ndmInitializerType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => a.GetTypes())
                    .FirstOrDefault(t =>
                        t.GetCustomAttribute<NdmInitializerAttribute>()?.Name == initializerName);
                var ndmModule = new NdmModule();
                var ndmConsumersModule = new NdmConsumersModule();
                ndmInitializer = new NdmInitializerFactory(ndmInitializerType, ndmModule, ndmConsumersModule,
                    logManager).CreateOrFail();
            }

            var grpcConfig = configProvider.GetConfig<IGrpcConfig>();
            GrpcServer grpcServer = null;
            if (grpcConfig.Enabled)
            {
                grpcServer = new GrpcServer(jsonSerializer, logManager);
                if (ndmEnabled)
                {
                    ndmConsumerChannelManager.Add(new GrpcNdmConsumerChannel(grpcServer));
                }
                
                _grpcRunner = new GrpcRunner(grpcServer, grpcConfig, logManager);
                await _grpcRunner.Start().ContinueWith(x =>
                {
                    if (x.IsFaulted && Logger.IsError) Logger.Error("Error during GRPC runner start", x.Exception);
                });
            }
            
            if (initConfig.WebSocketsEnabled)
            {
                if (ndmEnabled)
                {
                    webSocketsManager.AddModule(new NdmWebSocketsModule(ndmConsumerChannelManager, ndmDataPublisher,
                        jsonSerializer));
                }
            }
            
            _ethereumRunner = new EthereumRunner(rpcModuleProvider, configProvider, logManager, grpcServer,
                ndmConsumerChannelManager, ndmDataPublisher, ndmInitializer, webSocketsManager, jsonSerializer);
            await _ethereumRunner.Start().ContinueWith(x =>
            {
                if (x.IsFaulted && Logger.IsError) Logger.Error("Error during ethereum runner start", x.Exception);
            });

            if (jsonRpcConfig.Enabled)
            {
                rpcModuleProvider.Register(new SingletonModulePool<IWeb3Module>(new Web3Module(logManager), true));
                var jsonRpcService = new JsonRpcService(rpcModuleProvider, logManager);
                var jsonRpcProcessor = new JsonRpcProcessor(jsonRpcService, jsonSerializer, logManager);
                if (initConfig.WebSocketsEnabled)
                {
                    webSocketsManager.AddModule(new JsonRpcWebSocketsModule(jsonRpcProcessor, jsonSerializer));
                }
                
                Bootstrap.Instance.JsonRpcService = jsonRpcService;
                Bootstrap.Instance.LogManager = logManager;
                Bootstrap.Instance.JsonSerializer = jsonSerializer;
                _jsonRpcRunner = new JsonRpcRunner(configProvider, rpcModuleProvider, logManager, jsonRpcProcessor,
                    webSocketsManager);
                await _jsonRpcRunner.Start().ContinueWith(x =>
                {
                    if (x.IsFaulted && Logger.IsError) Logger.Error("Error during jsonRpc runner start", x.Exception);
                });
            }
            else
            {
                if (Logger.IsInfo) Logger.Info("Json RPC is disabled");
            }

            if (metricsParams.Enabled)
            {
                var intervalSeconds = metricsParams.IntervalSeconds;
                _monitoringService = new MonitoringService(new MetricsUpdater(intervalSeconds),
                    metricsParams.PushGatewayUrl, ClientVersion.Description,
                    metricsParams.NodeName, intervalSeconds, logManager);
                await _monitoringService.StartAsync().ContinueWith(x =>
                {
                    if (x.IsFaulted && Logger.IsError) Logger.Error("Error during starting a monitoring.", x.Exception);
                });
            }
            else
            {
                if (Logger.IsInfo) Logger.Info("Monitoring is disabled");
            }
        }

        protected abstract (CommandLineApplication, Func<IConfigProvider>, Func<string>) BuildCommandLineApp();

        protected async Task StopAsync()
        {
            _grpcRunner?.StopAsync();
            _monitoringService?.StopAsync();
            _jsonRpcRunner?.StopAsync(); // do not await
            var ethereumTask = _ethereumRunner?.StopAsync() ?? Task.CompletedTask;
            await ethereumTask;
        }
    }
}