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
using System.Security.Cryptography.Xml;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.CommandLineUtils;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Runner.Runners;

namespace Nethermind.Runner
{
    public class RunnerApp : BaseRunnerApp, IRunnerApp
    {
        private static readonly PrivateKey PrivateKey = new PrivateKey("49a7b37aa6f6645917e7b807e9d1c00d4fa71f18343b0d4122a4d2df64dd6fee");

        private readonly string _defaultHost = "0.0.0.0";
        private readonly int _defaultHttpPort = 8545;
        private readonly string _defaultGenesisFile = Path.Combine("Data", "genesis.json");

        public RunnerApp(ILogger logger) : base(logger, new PrivateKeyProvider(PrivateKey))
        {
        }

        public void Run(string[] args)
        {
            var app = new CommandLineApplication { Name = "Nethermind.Runner" };
            app.HelpOption("-?|-h|--help");

            var host = app.Option("-ho|--httpHost <httpHost>", "JsonRPC http server host", CommandOptionType.SingleValue);
            var httpPort = app.Option("-p|--httpPort <httpPort>", "JsonRPC http listening port", CommandOptionType.SingleValue);
            var discoveryPort = app.Option("-d|--discoveryPort <discoveryPort>", "discovery UDP listening port", CommandOptionType.SingleValue);
            var genesisFile = app.Option("-gf|--genesisFile <genesisFile>", "genesis file path", CommandOptionType.SingleValue);

            ManualResetEvent appClosed = new ManualResetEvent(false);
            app.OnExecute(() => {

                var initParams = new InitParams
                {
                    HttpHost = host.HasValue() ? host.Value() : _defaultHost,
                    HttpPort = httpPort.HasValue() ? GetIntValue(httpPort.Value(), "httpPort") : _defaultHttpPort,
                    DiscoveryPort = discoveryPort.HasValue() ? GetIntValue(discoveryPort.Value(), "discoveryPort") : (int?)null,
                    GenesisFilePath = genesisFile.HasValue() ? genesisFile.Value() : _defaultGenesisFile,
                    EthereumRunnerType = EthereumRunnerType.Default
                };

                Logger.Log($"Running Nethermind Runner, parameters: {initParams}");

                RunInternal(initParams);

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
    }
}