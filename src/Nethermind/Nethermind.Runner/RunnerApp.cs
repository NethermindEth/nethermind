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
using System.Runtime.InteropServices;
using Microsoft.Extensions.CommandLineUtils;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Db.Config;
using Nethermind.JsonRpc.Config;
using Nethermind.KeyStore.Config;
using Nethermind.Network.Config;
using Nethermind.Runner.Config;
using Nethermind.Stats;

namespace Nethermind.Runner
{
    public class RunnerApp : RunnerAppBase, IRunnerApp
    {
        private readonly string _defaultConfigFile = Path.Combine("configs", "mainnet_" + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" : "posix") + ".config.json");

        public RunnerApp(ILogger logger) : base(logger)
        {
        }

        [Todo("find better way to enforce assemblies with config impl are loaded")]
        protected override (CommandLineApplication, Func<IConfigProvider>, Func<string>) BuildCommandLineApp()
        {
            var app = new CommandLineApplication {Name = "Nethermind.Runner"};
            app.HelpOption("-?|-h|--help");
            var configFile = app.Option("-c|--config <configFile>", "config file path", CommandOptionType.SingleValue);
            var dbBasePath = app.Option("-d|--baseDbPath <baseDbPath>", "base db path", CommandOptionType.SingleValue);

            IConfigProvider BuildConfigProvider()
            {
                // ReSharper disable once NotAccessedVariable
                var config = typeof(KeystoreConfig).Assembly;
                config = typeof(NetworkConfig).Assembly;
                config = typeof(JsonRpcConfig).Assembly;
                config = typeof(InitConfig).Assembly;
                config = typeof(DbConfig).Assembly;
                config = typeof(StatsConfig).Assembly;
                config = typeof(BlockchainConfig).Assembly;

                var configProvider = new JsonConfigProvider();
                string configFilePath = configFile.HasValue() ? configFile.Value() : _defaultConfigFile;
                Console.WriteLine($"Reading config file from {configFilePath}");
                configProvider.LoadJsonConfig(configFilePath);
                return configProvider;
            }

            string GetBaseDbPath()
            {
                return dbBasePath.HasValue() ? dbBasePath.Value() : null;
            }

            return (app, BuildConfigProvider, GetBaseDbPath);
        }
    }
}