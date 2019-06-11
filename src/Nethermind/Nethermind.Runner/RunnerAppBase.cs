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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.CommandLineUtils;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Json;
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

namespace Nethermind.Runner
{
    public abstract class RunnerAppBase
    {
        protected ILogger Logger;
        private IRunner _jsonRpcRunner = NullRunner.Instance;
        private IRunner _ethereumRunner = NullRunner.Instance;
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
                    Logger.Info($"Nethermind config:\n{serializer.Serialize(initConfig, true)}\n");

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
            var initParams = configProvider.GetConfig<IInitConfig>();
            var metricsParams = configProvider.GetConfig<IMetricsConfig>();
            var logManager = new NLogManager(initParams.LogFileName, initParams.LogDirectory);
            IRpcModuleProvider rpcModuleProvider = initParams.JsonRpcEnabled
                ? new RpcModuleProvider(configProvider.GetConfig<IJsonRpcConfig>())
                : (IRpcModuleProvider) NullModuleProvider.Instance;
            var webSocketsManager = new WebSocketsManager();

            _ethereumRunner = new EthereumRunner(rpcModuleProvider, configProvider, logManager);
            await _ethereumRunner.Start().ContinueWith(x =>
            {
                if (x.IsFaulted && Logger.IsError) Logger.Error("Error during ethereum runner start", x.Exception);
            });

            if (initParams.JsonRpcEnabled)
            {
                var serializer = new EthereumJsonSerializer();
                rpcModuleProvider.Register<IWeb3Module>(new Web3Module(logManager));
                var jsonRpcService = new JsonRpcService(rpcModuleProvider, logManager);
                var jsonRpcProcessor = new JsonRpcProcessor(jsonRpcService, serializer, logManager);
                webSocketsManager.AddModule(new JsonRpcWebSocketsModule(jsonRpcProcessor, serializer));
                Bootstrap.Instance.JsonRpcService = jsonRpcService;
                Bootstrap.Instance.LogManager = logManager;
                Bootstrap.Instance.JsonSerializer = serializer;
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

            if (metricsParams.MetricsEnabled)
            {
                var intervalSeconds = metricsParams.MetricsIntervalSeconds;
                _monitoringService = new MonitoringService(new MetricsUpdater(intervalSeconds),
                    metricsParams.MetricsPushGatewayUrl, ClientVersion.Description,
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
            _monitoringService?.StopAsync();
            _jsonRpcRunner?.StopAsync(); // do not await
            var ethereumTask = _ethereumRunner?.StopAsync() ?? Task.CompletedTask;
            await ethereumTask;
        }
    }
}