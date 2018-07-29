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
using System.Reflection;
using Microsoft.Extensions.CommandLineUtils;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.JsonRpc.Config;
using Nethermind.KeyStore.Config;
using Nethermind.Network.Config;
using Nethermind.Runner.Config;

namespace Nethermind.Runner
{
    public class RunnerApp : RunnerAppBase, IRunnerApp
    {
        private static readonly PrivateKey PrivateKey = new PrivateKey("49a7b37aa6f6645917e7b807e9d1c00d4fa71f18343b0d4122a4d2df64dd6fee");

        private const string DefaultConfigFile = "configs\\ropsten_local.config.json";

        public RunnerApp(ILogger logger) : base(logger, new PrivateKeyProvider(PrivateKey))
        {
        }

        protected override (CommandLineApplication, Func<IConfigProvider>, Func<string>) BuildCommandLineApp()
        {
            var app = new CommandLineApplication {Name = "Nethermind.Runner"};
            app.HelpOption("-?|-h|--help");
            var configFile = app.Option("-c|--config <configFile>", "config file path", CommandOptionType.SingleValue);
            var dbBasePath = app.Option("-d|--baseDbPath <baseDbPath>", "base db path", CommandOptionType.SingleValue);

            IConfigProvider BuildConfigProvider()
            {
                //TODO find better way to enforce assemblies with config impl are loaded
                var kConfig = typeof(KeystoreConfig).Assembly;
                var nConfig = typeof(NetworkConfig).Assembly;
                var jConfig = typeof(JsonRpcConfig).Assembly;
                var iConfig = typeof(InitConfig).Assembly;

                var configProvider = new JsonConfigProvider();
                configProvider.LoadJsonConfig(configFile.HasValue() ? configFile.Value() : DefaultConfigFile);
                return configProvider;
            };

            string GetBaseDbPath()
            {
                return dbBasePath.HasValue() ? dbBasePath.Value() : null;
            }
            //InitParams InitParams()
            //{
            //    IJsonSerializer serializer = new UnforgivingJsonSerializer();
            //    return serializer.Deserialize<InitParams>(File.ReadAllText(configFile.HasValue() ? configFile.Value() : DefaultConfigFile));
            //};

            return (app, BuildConfigProvider, GetBaseDbPath);
        }
    }
}