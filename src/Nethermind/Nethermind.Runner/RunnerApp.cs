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
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.CommandLineUtils;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Runner
{
    public class RunnerApp : IRunnerApp
    {
        private readonly ILogger _logger;

        //TODO temp solution - fix it
        public static InitParams InitParams;

        private string _host = "0.0.0.0";
        private string _bootNode = "localhost";
        private int _httpPort = 8545;
        private int _discoveryPort = 30303;
        private string _genesisFile = "genesis.json";
        private string _chainFile = "chain.rlp";
        private string _blocksDir = "blocks";
        private string _keysDir = "keys";

        public RunnerApp(ILogger logger)
        {
            _logger = logger;
        }

        public void Start(string[] args)
        {
            var app = new CommandLineApplication { Name = "Nethermind.Runner" };
            app.HelpOption("-?|-h|--help");
            
            var host = app.Option("-ho|--host <host>", "server host", CommandOptionType.SingleValue);
            var bootNode = app.Option("-b|--bootNode <bootNode>", "enode URL of the remote bootstrap node", CommandOptionType.SingleValue);
            var httpPort = app.Option("-p|--httpPort <httpPort>", "JsonRPC http listening port", CommandOptionType.SingleValue);
            var discoveryPort = app.Option("-d|--discoveryPort <discoveryPort>", "discovery UDP listening port", CommandOptionType.SingleValue);
            var genesisFile = app.Option("-gf|--genesisFile <genesisFile>", "genesis file path", CommandOptionType.SingleValue);
            var chainFile = app.Option("-cf|--chainFile <chainFile>", "chain file path", CommandOptionType.SingleValue);
            var blocksDir = app.Option("-bd|--blocksDir <blocksDir>", "blocks directory path", CommandOptionType.SingleValue);
            var keysDir = app.Option("-kd|--keysDir <keysDir>", "keys directory path", CommandOptionType.SingleValue);
            var homesteadBlockNr = app.Option("-fh|--homesteadBlockNr <homesteadBlockNr>", "the block number of the Ethereum Homestead transition", CommandOptionType.SingleValue);

            app.OnExecute(() => {
                
                var initParams = new InitParams
                {
                    Host = host.HasValue() ? host.Value() : _host,
                    BootNode = bootNode.HasValue() ? bootNode.Value() : _bootNode,
                    HttpPort = httpPort.HasValue() ? GetIntValue(httpPort.Value(), "httpPort") : _httpPort,
                    DiscoveryPort = discoveryPort.HasValue() ? GetIntValue(discoveryPort.Value(), "discoveryPort") : _discoveryPort,
                    GenesisFilePath = genesisFile.HasValue() ? genesisFile.Value() : _genesisFile,
                    ChainFile = chainFile.HasValue() ? chainFile.Value() : _chainFile,
                    BlocksDir = blocksDir.HasValue() ? blocksDir.Value() : _blocksDir,
                    KeysDir = keysDir.HasValue() ? keysDir.Value() : _keysDir,
                    HomesteadBlockNr = homesteadBlockNr.HasValue() ? GetBigIntValue(homesteadBlockNr.Value(), "homesteadBlockNr") : (BigInteger?)null
                };

                Console.WriteLine($"Running Nethermind Runner, parameters: {initParams}");
                InitParams = initParams;

                Run(initParams);
                return 0;
            });

            app.Execute(args);
        }

        private void Run(InitParams initParams)
        {
            try
            {
                var host = $"http://{initParams.Host}:{initParams.HttpPort}";
                _logger.Log($"Running server, url: {host}");

                var webHost = WebHost.CreateDefaultBuilder()
                    .UseStartup<Startup>()
                    .UseUrls(host)
                    .Build();

                var ethereumRunner = webHost.Services.GetService<IEthereumRunner>();
                ethereumRunner.Start(initParams);

                var jsonRpcRunner = webHost.Services.GetService<IJsonRpcRunner>();
                jsonRpcRunner.Start(webHost);

                while (true)
                {
                    Console.WriteLine("Enter e to exit");
                    var value = Console.ReadLine();
                    if ("e".CompareIgnoreCaseTrim(value))
                    {
                        _logger.Log("Closing app");
                        break;
                    }
                    Thread.Sleep(2000);
                }
            }
            catch (Exception e)
            {
                _logger.Error("Error while starting Nethermind.Runner", e);
                throw;
            }
        }

        //private void TestConnection()
        //{
        //    Thread.Sleep(2);

        //    var client = new RunnerTestCient(_logger, new JsonSerializer(_logger));
        //    _logger.Log("Running client");
        //    var result = client.SendEthProtocolVersion();
        //    result.Wait();
        //    _logger.Log($"Result connection: {result.Result}");
        //}

        private int GetIntValue(string rawValue, string argName)
        {
            if (int.TryParse(rawValue, out var value))
            {
                return value;
            }

            throw new Exception($"Incorrect argument value, arg: {argName}, value: {rawValue}");
        }

        private BigInteger GetBigIntValue(string rawValue, string argName)
        {
            if (BigInteger.TryParse(rawValue, out var value))
            {
                return value;
            }

            throw new Exception($"Incorrect argument value, arg: {argName}, value: {rawValue}");
        }
    }
}