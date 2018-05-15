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
using Microsoft.Extensions.CommandLineUtils;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Runner
{
    public class RunnerApp : RunnerAppBase, IRunnerApp
    {
        private static readonly PrivateKey PrivateKey = new PrivateKey("49a7b37aa6f6645917e7b807e9d1c00d4fa71f18343b0d4122a4d2df64dd6fee");

        private const string DefaultConfigFile = "runner.config.json";

        public RunnerApp(ILogger logger) : base(logger, new PrivateKeyProvider(PrivateKey))
        {
        }

        protected override (CommandLineApplication, Func<InitParams>) BuildCommandLineApp()
        {
            var app = new CommandLineApplication {Name = "Nethermind.Runner"};
            app.HelpOption("-?|-h|--help");
            var configFile = app.Option("-c|--config <configFile>", "config file path", CommandOptionType.SingleValue);

            InitParams InitParams()
            {
                IJsonSerializer serializer = new UnforgivingJsonSerializer();
                return serializer.Deserialize<InitParams>(File.ReadAllText(configFile.HasValue() ? configFile.Value() : DefaultConfigFile));
            };

            return (app, InitParams);
        }
    }
}