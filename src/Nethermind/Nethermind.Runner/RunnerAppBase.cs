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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.CommandLineUtils;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Logging;
using Nethermind.Core.Specs.ChainSpec;
using Nethermind.Core.Utils;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Runner.Config;
using Nethermind.Runner.Runners;

namespace Nethermind.Runner
{
    public abstract class RunnerAppBase
    {
        protected ILogger Logger;
        private IJsonRpcRunner _jsonRpcRunner = NullRunner.Instance;
        private IEthereumRunner _ethereumRunner = NullRunner.Instance;
        private TaskCompletionSource<object> _cancelKeySource;

        protected RunnerAppBase(ILogger logger)
        {
            Logger = logger;
            AppDomain.CurrentDomain.ProcessExit += CurrentDomainOnProcessExit;
        }

        private void CurrentDomainOnProcessExit(object sender, EventArgs e)
        {
        }

        public void Run(string[] args)
        {
            var (app, buildConfigProvider, getDbBasePath) = BuildCommandLineApp();
            ManualResetEvent appClosed = new ManualResetEvent(false);
            app.OnExecute(async () =>
            {
                var configProvider = buildConfigProvider();
                var initConfig = configProvider.GetConfig<IInitConfig>();

                if (initConfig.RemovingLogFilesEnabled)
                {
                    RemoveLogFiles(initConfig.LogDirectory);
                }

                Logger = new NLogLogger(initConfig.LogFileName, initConfig.LogDirectory);

                var pathDbPath = getDbBasePath();
                if (!string.IsNullOrWhiteSpace(pathDbPath))
                {
                    var newDbPath = Path.Combine(pathDbPath, initConfig.BaseDbPath);
                    if(Logger.IsInfo) Logger.Info($"Adding prefix to baseDbPath, new value: {newDbPath}, old value: {initConfig.BaseDbPath}");
                    initConfig.BaseDbPath = newDbPath;
                }

                Console.Title = initConfig.LogFileName;
                Console.CancelKeyPress += ConsoleOnCancelKeyPress;

                var serializer = new UnforgivingJsonSerializer();
                if(Logger.IsInfo) Logger.Info($"Running Nethermind Runner, parameters:\n{serializer.Serialize(initConfig, true)}\n");

                _cancelKeySource = new TaskCompletionSource<object>();
                Task userCancelTask = Task.Factory.StartNew(() =>
                {
                    Console.WriteLine("Enter 'e' to exit");
                    while (true)
                    {
                        ConsoleKeyInfo keyInfo = Console.ReadKey();
                        if (keyInfo.KeyChar == 'e')
                        {
                            break;
                        }
                    }
                });

                await StartRunners(configProvider);
                await Task.WhenAny(userCancelTask, _cancelKeySource.Task);
                                                                                                                                                                                                                                                                                                        
                Console.WriteLine("Closing, please wait until all functions are stopped properly...");
                StopAsync().Wait();
                Console.WriteLine("All done, goodbye!");
                appClosed.Set();

                return 0;
            });

            app.Execute(args);
            appClosed.WaitOne();
        }

        private void ConsoleOnCancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            _cancelKeySource.SetResult(null);
            e.Cancel = false;
        }

        protected async Task StartRunners(IConfigProvider configProvider)
        {
            var initParams = configProvider.GetConfig<IInitConfig>();
            var logManager = new NLogManager(initParams.LogFileName, initParams.LogDirectory);

            if (initParams.RunAsReceiptsFiller)
            {
                _ethereumRunner = new ReceiptsFiller(configProvider, logManager);
            }
            else
            {
                //discovering and setting local, remote ips for client machine
                var networkHelper = new NetworkHelper(Logger);
                var localHost = networkHelper.GetLocalIp()?.ToString() ?? "127.0.0.1";
                var networkConfig = configProvider.GetConfig<INetworkConfig>();
                networkConfig.MasterExternalIp = localHost;
                networkConfig.MasterHost = localHost;

                string path = initParams.ChainSpecPath;
                ChainSpecLoader chainSpecLoader = new ChainSpecLoader(new UnforgivingJsonSerializer());
                ChainSpec chainSpec = chainSpecLoader.LoadFromFile(path);

                var nodes = chainSpec.NetworkNodes.Select(nn => GetNode(nn, localHost)).ToArray();
                networkConfig.BootNodes = nodes;
                networkConfig.DbBasePath = initParams.BaseDbPath;
                _ethereumRunner = new EthereumRunner(configProvider, networkHelper, logManager);
            }

            await _ethereumRunner.Start().ContinueWith(x =>
            {
                if (x.IsFaulted && Logger.IsError) Logger.Error("Error during ethereum runner start", x.Exception);
            });

            if (initParams.JsonRpcEnabled && !initParams.RunAsReceiptsFiller)
            {
                Bootstrap.Instance.ConfigProvider = configProvider;
                Bootstrap.Instance.LogManager = logManager;
                Bootstrap.Instance.BlockchainBridge = _ethereumRunner.BlockchainBridge;
                Bootstrap.Instance.DebugBridge = _ethereumRunner.DebugBridge;
                Bootstrap.Instance.NetBridge = _ethereumRunner.NetBridge;

                _jsonRpcRunner = new JsonRpcRunner(configProvider, logManager);
                await _jsonRpcRunner.Start().ContinueWith(x =>
                {
                    if (x.IsFaulted && Logger.IsError) Logger.Error("Error during jsonRpc runner start", x.Exception);
                });
            }
            else
            {
                if (Logger.IsInfo) Logger.Info("Json RPC is disabled");
            }
        }

        protected abstract (CommandLineApplication, Func<IConfigProvider>, Func<string>) BuildCommandLineApp();

        protected async Task StopAsync()
        {
            _jsonRpcRunner?.StopAsync(); // do not await
            var ethereumTask = _ethereumRunner?.StopAsync() ?? Task.CompletedTask;
            await ethereumTask;
        }

        private ConfigNode GetNode(NetworkNode networkNode, string localHost)
        {
            var node = new ConfigNode
            {
                NodeId = networkNode.NodeId.PublicKey.ToString(false),
                Host = networkNode.Host == "127.0.0.1" ? localHost : networkNode.Host,
                Port = networkNode.Port,
                Description = networkNode.Description
            };
            return node;
        }

        private void RemoveLogFiles(string logDirectory)
        {
            Console.WriteLine("Removing log files.");

            var logsDir = string.IsNullOrEmpty(logDirectory) ? Path.Combine(PathUtils.GetExecutingDirectory(), "logs") : logDirectory;
            if (!Directory.Exists(logsDir))
            {
                return;
            }

            var files = Directory.GetFiles(logsDir);
            foreach (string file in files)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Error removing log file: {file}, exp: {e}");
                }
            }
        }
    }
}