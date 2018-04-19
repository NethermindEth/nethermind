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
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.CommandLineUtils;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.Runner.Runners;

namespace Nethermind.Runner
{
    public abstract class RunnerAppBase
    {
        protected readonly ILogger Logger;
        protected readonly IPrivateKeyProvider PrivateKeyProvider;
        private IJsonRpcRunner _jsonRpcRunner = NullRunner.Instance;
        private IEthereumRunner _ethereumRunner = NullRunner.Instance;
        private IDiscoveryRunner _discoveryRunner = NullRunner.Instance;

        protected RunnerAppBase(ILogger logger, IPrivateKeyProvider privateKeyProvider)
        {
            Logger = logger;
            PrivateKeyProvider = privateKeyProvider;
        }

        protected void StartRunners(InitParams initParams)
        {
            try
            {
                //Configuring app DI
                var configProvider = new ConfigurationProvider();
                Bootstrap.ConfigureContainer(configProvider, PrivateKeyProvider, Logger, initParams);

                if (initParams.JsonRpcEnabled)
                {
                    //It needs to run first to finalize objects registration in the container
                    _jsonRpcRunner = new JsonRpcRunner(configProvider, Logger);
                    _jsonRpcRunner.Start(initParams);
                }

                _ethereumRunner = Bootstrap.ServiceProvider.GetService<IEthereumRunner>();
                _ethereumRunner.Start(initParams);

                if (initParams.DiscoveryEnabled)
                {
                    _discoveryRunner = Bootstrap.ServiceProvider.GetService<IDiscoveryRunner>();
                    _discoveryRunner.Start(initParams);
                }
            }
            catch (Exception e)
            {
                Logger.Error("Error while starting Nethermind.Runner", e);
                throw;
            }
        }

        protected abstract (CommandLineApplication, Func<InitParams>) BuildCommandLineApp();

        public void Run(string[] args)
        {
            (var app, var buildInitParams) = BuildCommandLineApp();
            ManualResetEvent appClosed = new ManualResetEvent(false);
            app.OnExecute(() =>
            {
                var initParams = buildInitParams();
                Logger.Info($"Running Hive Nethermind Runner, parameters: {initParams}");

                StartRunners(initParams);

                Console.WriteLine("Enter 'e' to exit");
                while (true)
                {
                    ConsoleKeyInfo keyInfo = Console.ReadKey();
                    if (keyInfo.KeyChar == 'e')
                    {
                        break;
                    }
                }

                Console.WriteLine("Closing, please wait until all functions are stopped properly...");
                StopAsync().Wait();
                Console.WriteLine("All done, goodbye!");
                appClosed.Set();

                return 0;
            });

            app.Execute(args);
            appClosed.WaitOne();
        }

        protected async Task StopAsync()
        {
            await _jsonRpcRunner.StopAsync();
            await _discoveryRunner.StopAsync();
            await _ethereumRunner.StopAsync();
        }

        protected int GetIntValue(string rawValue, string argName)
        {
            if (int.TryParse(rawValue, out var value))
            {
                return value;
            }

            throw new Exception($"Incorrect argument value, arg: {argName}, value: {rawValue}");
        }

        protected BigInteger GetBigIntValue(string rawValue, string argName)
        {
            if (BigInteger.TryParse(rawValue, out var value))
            {
                return value;
            }

            throw new Exception($"Incorrect argument value, arg: {argName}, value: {rawValue}");
        }
    }
}